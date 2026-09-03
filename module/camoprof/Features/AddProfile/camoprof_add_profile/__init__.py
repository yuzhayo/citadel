"""Entry pendaftaran plugin Add Profile (camoprof) untuk pyhost.

Pyhost core memuat package ini sebagai plugin dan namespace
``camoprof.add_profile.*`` terdaftar lewat CommandRegistry — core tidak
mendapat branch fitur dan tidak tahu arti command ini.

Hook lifecycle: core memanggil ``disarm_for_session`` lewat dict
``LIFECYCLE_HOOKS`` saat session mati — koneksi satu arah (plugin ->
core); core hanya melihat callable generik.
"""

from providers import PyhostError

from camoprof_add_profile import commands, enrollment

NAMESPACE = "camoprof"
OWNER = "camoprof.add_profile"

COMMANDS = {
    "camoprof.add_profile.start": commands.cmd_start,
    "camoprof.add_profile.status": commands.cmd_status,
    "camoprof.add_profile.finish": commands.cmd_finish,
    "camoprof.add_profile.cancel": commands.cmd_cancel,
}

# Hook lifecycle generik: dipasang core ke jalur _drop_session.
# Signature: (host, sid, profile) -> None, sinkron.
LIFECYCLE_HOOKS = [
    enrollment.disarm_for_session,
]


def install(host):
    """Daftarkan command + hook pada host yang sedang hidup."""
    host.registry.register_namespace(OWNER, NAMESPACE, COMMANDS)
    host.add_profile_enrollments = getattr(
        host, "add_profile_enrollments", {})
    for hook in LIFECYCLE_HOOKS:
        host.add_lifecycle_hook(hook)
    return COMMANDS
