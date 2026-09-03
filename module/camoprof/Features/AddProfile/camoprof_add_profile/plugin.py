"""Entry pendaftaran plugin Add Profile (camoprof) untuk pyhost.

Pyhost memuat package ini sebagai plugin (CITADEL_PYHOST_PLUGINS) dan
command ``camoprof.add_profile.*`` terdaftar lewat helper generik
``host.register_commands`` — pyhost tidak mendapat branch fitur dan
tidak tahu arti command ini. Hook lifecycle generik dipasang lewat
``host.add_lifecycle_hook``; koneksi satu arah (plugin -> host).
"""

from camoprof_add_profile import commands, enrollment

OWNER = "camoprof.add_profile"

COMMANDS = {
    "camoprof.add_profile.start": commands.cmd_start,
    "camoprof.add_profile.status": commands.cmd_status,
    "camoprof.add_profile.finish": commands.cmd_finish,
    "camoprof.add_profile.cancel": commands.cmd_cancel,
}

# Hook lifecycle generik: dipanggil host dari jalur _drop_session.
# Signature: (host, sid, profile) -> None, sinkron.
LIFECYCLE_HOOKS = [
    enrollment.disarm_for_session,
]


def install(host):
    """Daftarkan command + hook pada host yang sedang hidup."""
    host.register_commands(OWNER, COMMANDS)
    host.add_profile_enrollments = getattr(
        host, "add_profile_enrollments", {})
    for hook in LIFECYCLE_HOOKS:
        host.add_lifecycle_hook(hook)
    return COMMANDS
