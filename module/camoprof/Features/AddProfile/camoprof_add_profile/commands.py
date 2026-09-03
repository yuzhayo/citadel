"""Batasan command fitur Add Profile: validasi + delegasi ke enrollment.

Lapisan ini memastikan handler menerima bentuk pesan yang benar sebelum
menyentuh state machine. Akses metadata session lewat SessionHost
(baca-only) — plugin tidak memegang registry mentah.
"""

from providers import PyhostError

from camoprof_add_profile import enrollment


async def cmd_start(host, msg):
    sid = _session_of(msg)
    info = host.session_host.get(sid)
    if info is None:
        raise PyhostError("SESSION_NOT_FOUND", "session: %r" % (sid,))
    return await enrollment.start(host, info, sid, msg)


async def cmd_status(host, msg):
    return await enrollment.status(host, _session_of(msg))


async def cmd_finish(host, msg):
    return await enrollment.finish(host, _session_of(msg))


async def cmd_cancel(host, msg):
    return await enrollment.cancel(host, _session_of(msg))


def _session_of(msg):
    sid = msg.get("session")
    if not isinstance(sid, str) or not sid:
        raise PyhostError("SESSION_NOT_FOUND", "session: %r" % (sid,))
    return sid
