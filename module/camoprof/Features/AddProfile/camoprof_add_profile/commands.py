"""Batasan command fitur Add Profile: validasi + delegasi ke enrollment.

Lapisan ini memastikan handler menerima bentuk pesan yang benar sebelum
menyentuh state machine, dan mengembalikan nama field yang stabil.
"""

from providers import PyhostError

from camoprof_add_profile import enrollment
from camoprof_add_profile.add_profile_state import TERMINAL_STATES


async def cmd_start(host, msg):
    sid = _session_of(msg)
    sess = _get_session(host, sid)
    return await enrollment.start(host, sess, sid, msg)


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


def _get_session(host, sid):
    sess = host.sessions.get(sid)
    if sess is None:
        raise PyhostError("SESSION_NOT_FOUND", "session: %r" % (sid,))
    return sess
