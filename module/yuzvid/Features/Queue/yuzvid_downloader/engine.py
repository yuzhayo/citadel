"""Multi-thread range download engine untuk Yuzvid.

Port setia dari ``LTX-QUASAR/dood/dood_downloader/doodlib.py`` — bagian engine
saja (tanpa browser): byte-range split, seek-write ke satu file (tanpa merge),
resume lewat checkpoint JSON, dan retry yang sadar-progress. Teknik aslinya
diadopsi dari AB Download Manager.

Provenance (jangan ubah host/aturan tanpa memperbarui ini):
  Sumber:   C:\\VSCODE\\LTX-QUASAR\\dood\\dood_downloader\\doodlib.py
  Di-port:  2026-10-04
  Yang di-port: HEADERS, split_to_ranges, DownloadResult, ProgressInfo,
    PartState, DownloadState, PartDownloader, ProgressTracker, DoodDownloader
    (bagian unduh). TIDAK di-port: extract_cdn_url (pindah ke resolve.py,
    dijalankan lewat camoufox milik Citadel, bukan Playwright mandiri).
  Ketergantungan: hanya ``requests`` (sudah ada di shared venv Citadel).

Hanya ``requests`` yang dipakai di sini — tidak ada browser, tidak ada
Playwright — supaya logika urutan byte bisa diuji tanpa jaringan.
"""

import json
import logging
import threading
import time
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Callable, List, Optional

log = logging.getLogger("yuzvid_downloader.engine")


# ─── Data Classes ───────────────────────────────────────────────────────────

@dataclass
class DownloadResult:
    success: bool
    save_path: Optional[str] = None
    cdn_url: Optional[str] = None
    filename: Optional[str] = None
    file_size: int = 0
    elapsed: float = 0.0
    error: Optional[str] = None


@dataclass
class ProgressInfo:
    downloaded: int = 0
    total: int = 0
    percent: float = 0.0
    speed: float = 0.0          # bytes/sec
    active_threads: int = 0
    elapsed: float = 0.0
    eta: float = 0.0            # detik tersisa


@dataclass
class PartState:
    """State satu part — dipersist untuk resume."""
    index: int
    start: int          # range start (inklusif)
    end: int            # range end (inklusif)
    current: int        # byte berikutnya yang diunduh
    completed: bool = False

    @property
    def remaining(self) -> int:
        return self.end - self.current + 1 if not self.completed else 0

    @property
    def downloaded(self) -> int:
        return self.current - self.start


@dataclass
class DownloadState:
    """State unduhan penuh — dipersist ke JSON untuk resume."""
    cdn_url: str = ""
    filename: str = ""
    file_size: int = 0
    etag: str = ""
    parts: List[dict] = field(default_factory=list)
    created_at: float = 0.0
    updated_at: float = 0.0

    def save(self, path: Path):
        self.updated_at = time.time()
        path.write_text(json.dumps(asdict(self), indent=2), encoding="utf-8")

    @classmethod
    def load(cls, path: Path) -> "DownloadState":
        data = json.loads(path.read_text(encoding="utf-8"))
        return cls(**data)


# ─── Byte-Range Splitting (dari AB Download Manager) ────────────────────────

def split_to_ranges(size: int, min_part_size: int, max_parts: int) -> List[tuple]:
    """Bagi `size` byte jadi range (start, end) inklusif.

    Sisa byte dibagi satu-per-range ke N range pertama. Hasil tanpa celah,
    tanpa tumpang tindih.
    """
    if size <= 0:
        return []

    min_parts = (size + min_part_size - 1) // min_part_size  # ceiling division
    actual_parts = min(max_parts, min_parts)
    ideal_part_size = size // actual_parts

    ranges = []
    start = 0
    for i in range(actual_parts):
        end = start + ideal_part_size - 1
        if i < size % actual_parts:
            end += 1  # distribusikan sisa
        ranges.append((start, end))
        start = end + 1

    return ranges


# ─── Header HTTP (dibekukan dari doodlib) ───────────────────────────────────

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                  "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
    "Accept": "*/*",
    "Referer": "https://playmogo.com/",
    "Accept-Encoding": "identity",
}


# ─── Part Downloader Thread ─────────────────────────────────────────────────

class PartDownloader(threading.Thread):
    """Unduh satu byte range dari CDN dan tulis langsung ke file output di
    offset yang benar. Tanpa file temp, tanpa merge."""

    def __init__(
        self,
        part: PartState,
        cdn_url: str,
        file_obj,
        lock: threading.Lock,
        progress: "ProgressTracker",
        proxy: Optional[str] = None,
        max_retries: int = 10,
        retry_delay: float = 1.0,
    ):
        super().__init__(daemon=True)
        self.part = part
        self.cdn_url = cdn_url
        self.file_obj = file_obj
        self.lock = lock
        self.progress = progress
        self.proxy = proxy
        self.max_retries = max_retries
        self.retry_delay = retry_delay

        self._stop_event = threading.Event()
        self._pause_event = threading.Event()
        self._pause_event.set()  # awalnya tidak di-pause
        self._bytes_before_retry = 0
        self._retry_count = 0

    def stop(self):
        self._stop_event.set()
        self._pause_event.set()  # unblock bila sedang pause

    @property
    def is_stopped(self) -> bool:
        return self._stop_event.is_set()

    def run(self):
        while not self._stop_event.is_set():
            self._pause_event.wait()
            if self._stop_event.is_set():
                break

            if self.part.completed:
                break

            self._bytes_before_retry = self.part.downloaded
            try:
                self._download_range()
                break  # sukses
            except Exception as e:
                self._retry_count += 1
                if self._retry_count > self.max_retries:
                    log.error("Part %d: max retries terlampaui: %s", self.part.index, e)
                    break

                # Retry sadar-progress: reset bila ada kemajuan
                if self.part.downloaded > self._bytes_before_retry:
                    self._retry_count = 0

                log.warning("Part %d: retry %d/%d: %s",
                            self.part.index, self._retry_count, self.max_retries, e)
                time.sleep(self.retry_delay)

    def _download_range(self):
        import requests

        headers = {
            **HEADERS,
            "Range": "bytes=%d-%d" % (self.part.current, self.part.end),
        }
        proxies = {"https": self.proxy, "http": self.proxy} if self.proxy else None

        resp = requests.get(
            self.cdn_url,
            headers=headers,
            timeout=60,
            stream=True,
            allow_redirects=True,
            proxies=proxies,
        )

        if resp.status_code not in (200, 206):
            raise RuntimeError("HTTP %d" % resp.status_code)

        if resp.status_code == 200:
            log.warning("Part %d: server balas 200, bukan 206", self.part.index)

        buf_size = 256 * 1024  # 256KB
        # Fix port: mode blind (end==0 sentinel, ukuran tak diketahui) harus
        # dikonsumsi sampai EOF — loop `current > end` bawaan berhenti di
        # chunk pertama karena 0 > 0 langsung true.
        blind = (self.part.start == 0 and self.part.end == 0)
        for chunk in resp.iter_content(buf_size):
            if self._stop_event.is_set():
                resp.close()
                return

            self._pause_event.wait()
            if self._stop_event.is_set():
                resp.close()
                return

            if not chunk:
                continue

            chunk_len = len(chunk)

            # Seek-write ke offset yang benar (atomik dengan lock)
            with self.lock:
                self.file_obj.seek(self.part.current)
                self.file_obj.write(chunk)
                self.part.current += chunk_len

            self.progress.add_bytes(chunk_len)

            if not blind and self.part.current > self.part.end:
                self.part.completed = True
                break

        # Blind: selesai saat stream habis.
        if blind:
            self.part.completed = True
            return

        # Bila server mengirim semuanya (200 tanpa Range), tandai selesai
        if resp.status_code == 200 and self.part.current >= self.part.end:
            self.part.completed = True


# ─── Progress Tracker ───────────────────────────────────────────────────────

class ProgressTracker:
    """Pelacak progres thread-safe dengan kalkulasi kecepatan."""

    def __init__(self, total: int, callback: Optional[Callable] = None):
        self.total = total
        self.callback = callback
        self._downloaded = 0
        self._lock = threading.Lock()
        self._start_time = time.time()
        self._last_callback_time = 0
        self._active_threads = 0

    def add_bytes(self, n: int):
        with self._lock:
            self._downloaded += n
            now = time.time()
            if now - self._last_callback_time >= 0.5 and self.callback:
                self._last_callback_time = now
                elapsed = now - self._start_time
                speed = self._downloaded / elapsed if elapsed > 0 else 0
                remaining = self.total - self._downloaded
                eta = remaining / speed if speed > 0 else 0
                pct = (self._downloaded / self.total * 100) if self.total > 0 else 0

                info = ProgressInfo(
                    downloaded=self._downloaded,
                    total=self.total,
                    percent=pct,
                    speed=speed,
                    active_threads=self._active_threads,
                    elapsed=elapsed,
                    eta=eta,
                )
                try:
                    self.callback(info)
                except Exception:
                    pass

    def set_active_threads(self, n: int):
        self._active_threads = n


# ─── Main Downloader ────────────────────────────────────────────────────────

class DoodDownloader:
    """Engine unduh range multi-thread (tanpa browser).

    Fitur:
      - multi-thread seek-write (tanpa merge)
      - resume dari checkpoint
      - proxy opsional per-thread
      - callback progres
      - dukungan stop/pause
    """

    DEFAULT_MIN_PART_SIZE = 256 * 1024  # minimum 256KB per part

    def __init__(
        self,
        threads: int = 4,
        output_dir: str = "./downloads",
        min_part_size: int = 256 * 1024,
        max_retries: int = 10,
        retry_delay: float = 1.0,
        proxy: Optional[str] = None,
        proxy_list: Optional[List[str]] = None,
    ):
        self.threads = threads
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self.min_part_size = min_part_size
        self.max_retries = max_retries
        self.retry_delay = retry_delay
        self.proxy = proxy
        self.proxy_list = proxy_list or []

        self._active_parts: List[PartDownloader] = []
        self._stop_event = threading.Event()
        self._pause_event = threading.Event()
        self._pause_event.set()
        self._lock = threading.Lock()
        # Port-fix (doodlib tidak punya): penanda sesi selesai. Proses pyhost
        # hidup lama, jadi auto-saver WAJIB berhenti saat unduhan selesai —
        # kalau tidak, checkpoint basi ditulis terus dan resume berikutnya
        # salah mengira unduhan sudah tuntas.
        self._finished = threading.Event()

    def _get_proxy(self, thread_index: int) -> Optional[str]:
        if self.proxy_list:
            return self.proxy_list[thread_index % len(self.proxy_list)]
        return self.proxy

    def _state_path(self, filename: str) -> Path:
        return self.output_dir / ("%s.dlstate.json" % filename)

    def download(
        self,
        cdn_url: str,
        filename: str,
        file_size: int,
        etag: str = "",
        on_progress: Optional[Callable[[ProgressInfo], None]] = None,
        output_path: Optional[str] = None,
        force_single_thread: bool = False,
        should_stop: Optional[Callable[[], bool]] = None,
    ) -> DownloadResult:
        """Unduh CDN URL yang SUDAH di-resolve (dari resolve.py).

        Args:
            cdn_url: URL CDN langsung (cloudatacdn).
            filename: nama file hasil.
            file_size: ukuran byte (0 = mode blind).
            etag: ETag dari HEAD (untuk cocokkan resume).
            on_progress: callback(ProgressInfo) ~2x/detik.
            output_path: override path output.
            force_single_thread: matikan multi-thread.
            should_stop: predikat eksternal; bila True, unduhan dihentikan
                pada pemeriksaan berikutnya (dipakai command stop pyhost).
        """
        self._stop_event.clear()
        self._pause_event.set()
        self._finished.clear()
        start_time = time.time()

        save_path = Path(output_path) if output_path else self.output_dir / filename

        # Cek state yang ada (resume). Token CDN berubah tiap resolve, jadi
        # dicocokkan lewat filename + file_size + etag.
        state_file = self._state_path(filename)
        state = None
        if state_file.exists():
            try:
                state = DownloadState.load(state_file)
                incomplete_exists = save_path.exists() and save_path.stat().st_size > 0
                if (state.file_size == file_size or file_size == 0) and incomplete_exists:
                    state.cdn_url = cdn_url
                    log.info("Resume dari checkpoint (%d part)", len(state.parts))
                elif state.file_size == file_size and not incomplete_exists:
                    log.info("Checkpoint ada tapi file terhapus, mulai baru")
                    state = None
                elif state.file_size != file_size and file_size > 0:
                    log.info("Ukuran file berubah, mulai baru")
                    state = None
                else:
                    log.info("Checkpoint basi, mulai baru")
                    state = None
            except Exception:
                state = None

        # Tentukan parts
        if state and state.parts:
            parts = [PartState(**p) for p in state.parts]
            log.info("Memuat %d part dari checkpoint", len(parts))
        elif file_size > 0 and not force_single_thread and self.threads > 1:
            ranges = split_to_ranges(file_size, self.min_part_size, self.threads)
            parts = [PartState(index=i, start=s, end=e, current=s)
                     for i, (s, e) in enumerate(ranges)]
            log.info("Dipisah jadi %d part (Range didukung)", len(parts))
        else:
            end = file_size - 1 if file_size > 0 else 0
            parts = [PartState(index=0, start=0, end=end, current=0)]
            if file_size == 0:
                log.info("Ukuran tidak diketahui — mode blind download")
            else:
                log.info("Mode single-thread")

        # Sudah selesai?
        if all(p.completed for p in parts):
            log.info("Semua part sudah selesai")
            result = DownloadResult(
                success=True, save_path=str(save_path),
                cdn_url=cdn_url, filename=filename,
                file_size=file_size, elapsed=time.time() - start_time,
            )
            self._cleanup_state(state_file)
            self._finished.set()
            return result

        file_obj = open(save_path, "r+b" if save_path.exists() else "wb")

        progress = ProgressTracker(
            total=file_size if file_size > 0 else 0,
            callback=on_progress,
        )
        progress._downloaded = sum(p.downloaded for p in parts)

        self._active_parts = []
        for i, part in enumerate(parts):
            if part.completed:
                continue
            proxy = self._get_proxy(i)
            pd = PartDownloader(
                part=part, cdn_url=cdn_url, file_obj=file_obj,
                lock=self._lock, progress=progress, proxy=proxy,
                max_retries=self.max_retries, retry_delay=self.retry_delay,
            )
            self._active_parts.append(pd)

        log.info("Memulai %d thread", len(self._active_parts))
        progress.set_active_threads(len(self._active_parts))

        for pd in self._active_parts:
            pd.start()

        # Auto-save state tiap 1 detik
        save_thread = threading.Thread(
            target=self._auto_saver,
            args=(parts, state_file, filename, cdn_url, file_size, etag, should_stop),
            daemon=True)
        save_thread.start()

        # Tunggu selesai — dengan dukungan stop eksternal
        for pd in self._active_parts:
            while pd.is_alive():
                pd.join(timeout=0.5)
                if should_stop is not None and should_stop():
                    self.stop()
                    break

        self._save_state(parts, state_file, filename, cdn_url, file_size, etag)

        file_obj.close()
        elapsed = time.time() - start_time

        actual_size = save_path.stat().st_size if save_path.exists() else 0
        if file_size > 0 and actual_size != file_size:
            log.warning("Ukuran tidak cocok: harap %d, dapat %d", file_size, actual_size)

        all_done = all(p.completed for p in parts)
        if all_done:
            self._cleanup_state(state_file)

        # Sinyalkan semua thread latar (auto-saver) untuk berhenti — fix port:
        # proses pyhost hidup lama, jadi tanpa ini auto-saver menulis checkpoint
        # basi terus-menerus setelah unduhan tuntas.
        self._finished.set()

        result = DownloadResult(
            success=all_done,
            save_path=str(save_path) if all_done else None,
            cdn_url=cdn_url,
            filename=filename,
            file_size=actual_size,
            elapsed=elapsed,
            error=None if all_done else "Download incomplete",
        )

        log.info("%s: %s (%.1f MB, %.1fs)",
                 "DONE" if all_done else "INCOMPLETE", save_path,
                 actual_size / 1024 / 1024, elapsed)
        return result

    def _auto_saver(self, parts, state_file, filename, cdn_url, file_size, etag,
                    should_stop=None):
        while not self._stop_event.is_set() and not self._finished.is_set():
            time.sleep(1)
            self._save_state(parts, state_file, filename, cdn_url, file_size, etag)
            if should_stop is not None and should_stop():
                break

    def _save_state(self, parts, state_file, filename, cdn_url, file_size, etag):
        state = DownloadState(
            cdn_url=cdn_url, filename=filename,
            file_size=file_size, etag=etag,
            parts=[asdict(p) for p in parts],
            created_at=time.time(),
        )
        try:
            state.save(state_file)
        except Exception as e:
            log.debug("Simpan state gagal: %s", e)

    def _cleanup_state(self, state_file):
        try:
            if state_file.exists():
                state_file.unlink()
        except Exception:
            pass

    def stop(self):
        self._stop_event.set()
        self._pause_event.set()
        for pd in self._active_parts:
            pd.stop()
