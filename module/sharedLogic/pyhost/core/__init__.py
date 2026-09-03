"""pyhost core: infrastruktur generik — registry command, SessionHost.

Kontrak kepemilikan file inti: TIDAK ada semantik fitur di sini (tidak
ada Google, enrollment, Add Profile, email, password, credential). Core
hanya menyediakan transport, lifecycle session, lease primary-page, dan
error terstruktur. Fitur (plugin) bergantung ke core, tidak sebaliknya.
"""

from core.command_registry import CommandRegistry
from core.session_host import SessionHost, SessionLease

__all__ = ["CommandRegistry", "SessionHost", "SessionLease"]
