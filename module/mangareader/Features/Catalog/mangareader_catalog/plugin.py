"""Entry pendaftaran plugin Catalog (MangaReader) untuk pyhost.

Pyhost memuat package ini sebagai plugin lewat ``CITADEL_PYHOST_PLUGINS`` dan
command ``catalog.*`` terdaftar melalui helper generik
``host.register_commands`` — core tidak mendapat branch fitur dan tidak tahu
arti command ini.

Plugin ini tidak memasang lifecycle hook: Catalog tidak memegang secret, dan
pembersihan browser sudah dijamin jalur ``_drop_session``/``close_all`` milik
core yang juga berjalan saat EOF dan shutdown.
"""

from mangareader_catalog import browser

OWNER = "mangareader.catalog"

COMMANDS = {
    "catalog.open": browser.cmd_open,
    "catalog.api": browser.cmd_api,
    "catalog.fetch": browser.cmd_fetch,
    "catalog.close": browser.cmd_close,
}


def install(host):
    """Daftarkan command pada host yang sedang hidup."""
    host.register_commands(OWNER, COMMANDS)
    return COMMANDS
