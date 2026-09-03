"""pyhost core: infrastruktur generik — registry command, SessionHost.

Kontrak kepemilikan file inti: fitur tidak boleh hidup di sini. Core
hanya menyediakan transport, lifecycle session, lease primary-page,
dan error terstruktur; nama command, selector, dan konsep domain
apa pun milik plugin yang memakainya. Fitur (plugin) bergantung ke
core, tidak sebaliknya.
"""

from core.command_registry import CommandRegistry
from core.session_host import SessionHost, SessionLease

__all__ = ["CommandRegistry", "SessionHost", "SessionLease"]
