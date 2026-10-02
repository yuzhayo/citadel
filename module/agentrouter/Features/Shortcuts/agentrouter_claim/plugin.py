"""Entry pendaftaran plugin pyhost untuk Agentrouter.

Pyhost memuat package ini lewat ``CITADEL_PYHOST_PLUGINS`` lalu memanggil
``install(host)``; command ``agentrouter.claim`` terdaftar lewat helper generik
``host.register_commands`` — core tetap tidak tahu arti command fitur.

Flow-nya sinkron (Playwright sync API + ``time.sleep``), jadi dijalankan di
worker thread. Kalau dijalankan langsung di event loop, host tidak bisa
membalas apa pun sampai claim selesai.
"""

from __future__ import annotations

import asyncio

from . import flow

OWNER = "agentrouter.claim"

# The flow enforces this wall-clock budget itself. A REQUEST timeout shorter
# than it would cancel this await while the worker thread kept driving the
# browser -- the caller would see TIMEOUT while the account was still being
# mutated. Callers must send a longer timeout than this value.
FLOW_BUDGET_S = flow.TOTAL_BUDGET_S


async def cmd_claim(host, msg):
    """Jalankan SATU claim untuk SATU profil.

    Selalu mengembalikan hasil terstruktur dengan ``outcome`` eksplisit, bukan
    melempar: ``flow.main`` menolak profil yang tidak ada di tabel dengan
    ``SystemExit``, dan itu BUKAN ``Exception`` -- kalau dibiarkan naik, pyhost
    mati dan pemanggil hanya melihat EOF, bukan alasan penolakannya.
    """
    profile = msg.get("profile")
    if not isinstance(profile, str) or not profile:
        raise ValueError("profile wajib: nama folder profil CamoProf")
    proxy = msg.get("proxy")
    if proxy is not None and (not isinstance(proxy, str) or not proxy.strip()):
        raise ValueError("proxy harus berupa URL atau null")

    try:
        returncode = await asyncio.to_thread(
            flow.main, [profile], bool(msg.get("headless")), proxy=proxy)
    except SystemExit as exc:
        return {"outcome": "refused", "returncode": 2, "detail": str(exc)}

    data = flow.load_profile_json(profile)
    return {
        "outcome": "saved" if returncode == 0 else "failed",
        "returncode": returncode,
        "keys": sorted(key for key in data if key != "profile"),
        "status_file": str(flow.OUT_DIR
                           / f"flow_1x_single_{profile}.status.json"),
    }


def install(host):
    """Daftarkan command pada host yang sedang hidup."""
    host.register_commands(OWNER, {"agentrouter.claim": cmd_claim})
    return {"agentrouter.claim": cmd_claim}
