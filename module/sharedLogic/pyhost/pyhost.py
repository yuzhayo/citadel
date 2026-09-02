"""pyhost: satu-satunya jahitan C# <-> Python untuk citadel.

Protokol NDJSON (lihat README.md — kontraknya di sana, bukan di sini):
satu objek JSON per baris di stdin, tepat satu respons per request di
stdout. stdout murni protokol; SEMUA log ke stderr.

v1 commands: ping, session.open, session.navigate, session.verify,
google.inspect, google.relogin, session.close, shutdown.

Perilaku Google hidup di providers/google/ (inspection, relogin,
enrollment); file ini hanya protokol, registry session, dan lifecycle.
Resep browser dipindahkan (ported) dari reference/human_login.py —
AsyncCamoufox langsung, persistent context, headed. BUKAN lewat stealthB.
"""

import asyncio
import importlib.metadata
import json
import os
import platform
import re
import sys
from urllib.parse import urlparse

from providers import PyhostError as _PyhostError, log as _log
from providers.google import is_browser_closed_error as _is_browser_closed_error
from providers.google import inspection, relogin

PROTOCOL_VERSION = 1
DEFAULT_TIMEOUT_SEC = 120.0
DEFAULT_START_URL = "https://www.google.com/"
NAME_RE = re.compile(r"^[A-Za-z0-9._-]+$")


def _log(msg):
    """Log HANYA ke stderr — stdout adalah protokol."""
    print("[pyhost] " + msg, file=sys.stderr, flush=True)


def _respond(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _err(rid, code, message):
    return {"id": rid, "ok": False,
            "error": {"code": code, "message": str(message)}}


def _pkg_version(name):
    try:
        return importlib.metadata.version(name)
    except Exception:  # noqa: BLE001 - versi bersifat diagnostik
        return None


class _Host:
    """Menyimpan state runtime: credenz, registry session, flag shutdown."""

    def __init__(self):
        self.credenz = self._read_credenz()
        self.sessions = {}          # sid -> dict(profile, cm, ctx, page, dir)
        self.next_sid = 0
        self.stopping = False

    @staticmethod
    def _read_credenz():
        raw = os.environ.get("CITADEL_CREDENZ", "")
        if not raw or not os.path.isabs(raw):
            _log("CITADEL_CREDENZ absen/bukan absolute: %r" % raw)
            return None
        return os.path.abspath(raw)

    def _require_credenz(self):
        if self.credenz is None:
            raise _PyhostError("STARTUP_NO_CREDENZ",
                               "env CITADEL_CREDENZ wajib absolute path")
        return self.credenz

    def _profile_dir(self, profile):
        if not isinstance(profile, str) or not NAME_RE.match(profile) \
                or profile in (".", ".."):
            raise _PyhostError("BAD_PROFILE_NAME",
                               "nama profile tidak sah: %r" % (profile,))
        root = os.path.realpath(
            os.path.join(self._require_credenz(), "google", "profiles"))
        candidate = os.path.realpath(os.path.join(root, profile))
        if os.path.commonpath((root, candidate)) != root:
            raise _PyhostError("PATH_ESCAPE",
                               "path profile keluar dari root: %r"
                               % (profile,))
        return candidate

    def _profile_busy(self, profile):
        return any(s["profile"] == profile for s in self.sessions.values())

    async def _drop_session(self, sid, forget_on_failure=False):
        """Tutup session dan baru lepaskan handle setelah close terkonfirmasi.

        Cancellation sengaja tidak ditelan: session tetap berada di registry
        sehingga close dapat dicoba lagi. ``forget_on_failure`` hanya boleh
        dipakai ketika caller sudah membuktikan browser memang mati.
        """

        sess = self.sessions.get(sid)
        if sess is None:
            return True
        try:
            if sess.get("ctx") is not None:
                await sess["cm"].__aexit__(None, None, None)
            elif sess.get("cm") is not None:
                # __aenter__ belum selesai; coba tetap tutup sumber dayanya.
                await sess["cm"].__aexit__(None, None, None)
        except asyncio.CancelledError:
            _log("close context %s dibatalkan; session dipertahankan" % sid)
            raise
        except Exception as e:  # noqa: BLE001 - cleanup tidak boleh melempar
            _log("close context %s: %s" % (sid, e))
            if forget_on_failure:
                self.sessions.pop(sid, None)
            return False
        self.sessions.pop(sid, None)
        return True

    # ---- commands -----------------------------------------------------

    async def cmd_ping(self, _msg):
        return {
            "protocol": PROTOCOL_VERSION,
            "python": platform.python_version(),
            "camoufox": _pkg_version("camoufox"),
            "playwright": _pkg_version("playwright"),
            "credenz_ready": self.credenz is not None,
        }

    async def cmd_session_open(self, msg):
        profile = msg.get("profile")
        headless = msg.get("headless", False)
        if not isinstance(headless, bool):
            raise _PyhostError("BAD_HEADLESS", "headless harus boolean")
        pdir = self._profile_dir(profile)
        if self._profile_busy(profile):
            raise _PyhostError("PROFILE_BUSY",
                               "profile sudah punya session: " + profile)
        os.makedirs(pdir, exist_ok=True)

        from camoufox.async_api import AsyncCamoufox  # lazim: dependensi berat

        cm = AsyncCamoufox(
            persistent_context=True,
            user_data_dir=pdir,
            headless=headless,
            humanize=True,
            os="windows",
            disable_coop=True,
            i_know_what_im_doing=True,
            config={"forceScopeAccess": True},
        )
        # Daftarkan session SEBELUM masuk context: kalau timeout/cancel terjadi
        # saat launch, cleanup punya pegangan. (codex audit #3)
        self.next_sid += 1
        sid = "s%d" % self.next_sid
        self.sessions[sid] = {"profile": profile, "cm": cm, "ctx": None,
                              "page": None, "dir": pdir,
                              "headless": headless}
        try:
            ctx = await cm.__aenter__()
            self.sessions[sid]["ctx"] = ctx

            page = ctx.pages[0] if ctx.pages else await ctx.new_page()
            self.sessions[sid]["page"] = page
            start_url = msg.get("start_url") or DEFAULT_START_URL
            try:
                await page.goto(start_url, wait_until="domcontentloaded",
                                timeout=45000)
            except Exception as e:  # noqa: BLE001 - navigasi awal tidak fatal
                _log("goto awal %s: %s" % (start_url, e))
        except asyncio.CancelledError:
            # wait_for cancels the handler at its deadline. Finish cleanup
            # before propagating cancellation; otherwise the caller receives
            # TIMEOUT with an inaccessible, profile-locking session behind it.
            await self._drop_session(sid)
            raise
        except Exception as e:  # noqa: BLE001 - laporkan terstruktur
            closed = await self._drop_session(sid)
            detail = str(e)
            if not closed:
                detail += " (cleanup belum selesai; pindah screen untuk force cleanup)"
            raise _PyhostError("BROWSER_LAUNCH", detail)

        _log("session %s dibuka untuk %s" % (sid, profile))
        return {"session": sid, "profile": profile, "profile_dir": pdir,
                "headless": headless}

    async def cmd_session_verify(self, msg):
        result = await self.cmd_google_inspect(msg)
        return {
            "alive": result["state"] == "active",
            "url": result["url"],
            "state_saved": result.get("state_saved", False),
        }

    async def cmd_session_navigate(self, msg):
        sess = self._get_session(msg)
        sid = msg.get("session")
        url = msg.get("url")
        parsed = urlparse(url) if isinstance(url, str) else None
        if (parsed is None or parsed.scheme not in ("http", "https")
                or not parsed.netloc):
            raise _PyhostError("BAD_URL", "url harus HTTP(S) absolut")
        try:
            await sess["page"].goto(url, wait_until="domcontentloaded",
                                    timeout=45000)
        except Exception as e:  # noqa: BLE001 - browser/network split
            if _is_browser_closed_error(e):
                await self._drop_session(sid, forget_on_failure=True)
                raise _PyhostError("BROWSER_GONE", "jendela browser ditutup")
            raise _PyhostError(
                "NAVIGATE_FAILED",
                "navigasi gagal; session dipertahankan")
        return {"session": sid, "url": sess["page"].url or url}

    async def cmd_google_inspect(self, msg):
        sess = self._get_session(msg)
        return await inspection.inspect(self, sess, msg.get("session"))

    async def cmd_google_relogin(self, msg):
        sess = self._get_session(msg)
        return await relogin.relogin(self, sess, msg.get("session"), msg)

    async def cmd_session_close(self, msg):
        sid = msg.get("session")
        if sid not in self.sessions:
            raise _PyhostError("SESSION_NOT_FOUND", "session: %r" % (sid,))
        if not await self._drop_session(sid):
            raise _PyhostError(
                "BROWSER_CLOSE_FAILED",
                "browser belum terkonfirmasi tertutup; session dipertahankan")
        _log("session %s ditutup" % sid)
        return {"closed": sid}

    async def cmd_shutdown(self, _msg):
        self.stopping = True
        return {"stopping": True}

    # ---- helpers -------------------------------------------------------

    def _get_session(self, msg):
        sid = msg.get("session")
        sess = self.sessions.get(sid)
        if sess is None:
            raise _PyhostError("SESSION_NOT_FOUND", "session: %r" % (sid,))
        return sess

    async def close_all(self):
        for sid in list(self.sessions):
            await self._drop_session(sid)


HANDLERS = {
    "ping": _Host.cmd_ping,
    "session.open": _Host.cmd_session_open,
    "session.navigate": _Host.cmd_session_navigate,
    "session.verify": _Host.cmd_session_verify,
    "google.inspect": _Host.cmd_google_inspect,
    "google.relogin": _Host.cmd_google_relogin,
    "session.close": _Host.cmd_session_close,
    "shutdown": _Host.cmd_shutdown,
}


async def _handle(host, msg):
    rid = msg.get("id")
    cmd = msg.get("cmd")
    handler = HANDLERS.get(cmd)
    if handler is None:
        return _err(rid, "UNKNOWN_COMMAND", "cmd: %r" % (cmd,))
    try:
        timeout = float(msg.get("timeout") or DEFAULT_TIMEOUT_SEC)
    except (TypeError, ValueError):
        timeout = DEFAULT_TIMEOUT_SEC
    try:
        result = await asyncio.wait_for(handler(host, msg), timeout)
    except asyncio.TimeoutError:
        return _err(rid, "TIMEOUT", "%s melebihi %ss" % (cmd, timeout))
    except _PyhostError as e:
        return _err(rid, e.code, str(e))
    except Exception as e:  # noqa: BLE001 - gate protokol
        return _err(rid, "INTERNAL", "%s: %s" % (type(e).__name__, e))
    result["id"] = rid
    result["ok"] = True
    return result


async def _main():
    host = _Host()
    _log("pyhost hidup (protocol v%d, python %s)"
         % (PROTOCOL_VERSION, platform.python_version()))
    try:
        while not host.stopping:
            line = await asyncio.to_thread(sys.stdin.readline)
            if line == "":
                _log("stdin EOF — cleanup dan keluar")
                break
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except ValueError as e:
                _respond(_err(None, "BAD_JSON", str(e)))
                continue
            if not isinstance(msg, dict):
                _respond(_err(None, "BAD_JSON", "request harus objek"))
                continue
            _respond(await _handle(host, msg))
    finally:
        await host.close_all()
    _log("pyhost keluar bersih")


if __name__ == "__main__":
    try:
        sys.exit(asyncio.run(_main()))
    except KeyboardInterrupt:
        sys.exit(0)
