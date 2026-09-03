"""Batasan command fitur Add Profile: validasi + delegasi ke enrollment.

Idiom proyek (pola google.inspect/relogin): handler pyhost menerima
dict session dan menyerahkannya ke fitur — tidak ada akses registry
lain, tidak ada layer tambahan.
"""

from providers import PyhostError

from camoprof_add_profile import enrollment


async def cmd_start(host, msg):
    sid = _session_of(msg)
    sess = host.get_session(sid)
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
