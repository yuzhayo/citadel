"""Entry plugin pyhost untuk Yuzvid Downloader.

Pyhost memuat package ini lewat ``CITADEL_PYHOST_PLUGINS`` lalu memanggil
``install(host)``; command ``yuzvid.*`` terdaftar lewat helper generik
``host.register_commands`` — core tetap tidak tahu arti command fitur.

Protokol v1 memproses request berurutan (tanpa event), jadi unduhan yang
panjang dijalankan di worker thread dan C# melakukan POLL lewat
``yuzvid.download.status``. Setiap handler async dan cepat; tidak ada handler
yang menahan event loop selama transfer.

Handler browsersync (resolve) dijalankan lewat ``asyncio.to_thread`` mengikuti
pola agentrouter_claim — kalau dijalankan langsung di event loop, host tidak
bisa membalas apa pun sampai selesai.
"""

from __future__ import annotations

import asyncio
import threading
import uuid

from . import engine, resolve

OWNER = "yuzvid.downloader"

# Registry unduhan aktif: job_id -> dict(state, engine, thread, stop_flag).
_jobs: dict = {}
_jobs_lock = threading.Lock()


def _job(job_id):
    with _jobs_lock:
        return _jobs.get(job_id)


async def cmd_resolve(host, msg):
    """Resolve link player → {cdn_url, filename, file_size, etag}."""
    url = msg.get("url")
    if not isinstance(url, str) or not url.strip():
        raise ValueError("url wajib")
    timeout = int(msg.get("timeout_s") or 15)
    # Browser sync -> worker thread (event loop tetap bisa membalas).
    return await asyncio.to_thread(resolve.extract_cdn_url, url, timeout)


async def cmd_download_start(host, msg):
    """Mulai unduhan di worker thread; balas job_id + metadata segera."""
    job_id = uuid.uuid4().hex

    if msg.get("cdn_url"):
        # Jalur normal: C# sudah resolve (atau memakai CDN tersimpan).
        cdn_url = msg["cdn_url"]
        filename = msg.get("filename") or "video.mp4"
        file_size = int(msg.get("file_size") or 0)
        etag = msg.get("etag") or ""
        info = {"success": True, "cdn_url": cdn_url, "filename": filename,
                "file_size": file_size, "etag": etag}
    else:
        # Jalur resolve-di-dalam (jarang): resolve dulu, baru unduh.
        url = msg.get("url")
        if not isinstance(url, str) or not url.strip():
            raise ValueError("cdn_url atau url wajib")
        info = await asyncio.to_thread(resolve.extract_cdn_url, url, 15)
        if not info.get("success"):
            return {"outcome": "failed", "error": info.get("error")}
        cdn_url = info["cdn_url"]
        filename = info["filename"]
        file_size = info["file_size"]
        etag = info.get("etag") or ""

    output_dir = msg.get("output_dir")
    if not isinstance(output_dir, str) or not output_dir.strip():
        raise ValueError("output_dir wajib (path absolut dari C#)")
    threads = int(msg.get("threads") or 4)
    proxy = msg.get("proxy") or None

    stop_flag = threading.Event()
    state = {"status": "running", "percent": 0.0, "downloaded": 0,
             "total": file_size, "speed": 0.0, "eta": 0.0,
             "save_path": None, "error": None, "filename": filename,
             "cdn_url": cdn_url}

    dl = engine.DoodDownloader(
        threads=threads, output_dir=output_dir, proxy=proxy)

    def on_progress(p):
        state["percent"] = p.percent
        state["downloaded"] = p.downloaded
        state["total"] = p.total
        state["speed"] = p.speed
        state["eta"] = p.eta

    def worker():
        try:
            result = dl.download(
                cdn_url=cdn_url, filename=filename, file_size=file_size,
                etag=etag, on_progress=on_progress,
                should_stop=stop_flag.is_set)
            with _jobs_lock:
                if result.success:
                    state["status"] = "done"
                    state["percent"] = 100.0
                    state["save_path"] = result.save_path
                elif stop_flag.is_set():
                    state["status"] = "stopped"
                else:
                    state["status"] = "failed"
                    state["error"] = result.error
        except Exception as e:  # noqa: BLE001 - laporkan terstruktur ke C#
            with _jobs_lock:
                state["status"] = "failed"
                state["error"] = "%s: %s" % (type(e).__name__, e)

    thread = threading.Thread(target=worker, daemon=True)

    with _jobs_lock:
        _jobs[job_id] = {"state": state, "engine": dl, "thread": thread,
                         "stop_flag": stop_flag}
    thread.start()

    return {"outcome": "started", "job_id": job_id, "filename": filename,
            "file_size": file_size, "cdn_url": cdn_url}


async def cmd_download_status(host, msg):
    """Balas snapshot progres satu job (dipanggil C# ~2x/detik)."""
    job = _job(msg.get("job_id"))
    if job is None:
        return {"outcome": "unknown", "status": "unknown"}
    s = job["state"]
    return {"outcome": "ok", "status": s["status"], "percent": s["percent"],
            "downloaded": s["downloaded"], "total": s["total"],
            "speed": s["speed"], "eta": s["eta"], "save_path": s["save_path"],
            "error": s["error"], "filename": s["filename"]}


async def cmd_download_stop(host, msg):
    """Hentikan satu job; biarkan checkpoint untuk resume."""
    job = _job(msg.get("job_id"))
    if job is None:
        return {"outcome": "unknown"}
    job["stop_flag"].set()
    job["engine"].stop()
    return {"outcome": "stopping"}


def install(host):
    """Daftarkan command pada host yang sedang hidup."""
    host.register_commands(OWNER, {
        "yuzvid.resolve": cmd_resolve,
        "yuzvid.download.start": cmd_download_start,
        "yuzvid.download.status": cmd_download_status,
        "yuzvid.download.stop": cmd_download_stop,
    })
    return OWNER
