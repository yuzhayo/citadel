"""Entry pendaftaran plugin Downloader (MangaReader) untuk pyhost.

Pyhost memuat package ini sebagai plugin lewat ``CITADEL_PYHOST_PLUGINS`` dan
command ``downloader.*`` terdaftar melalui helper generik
``host.register_commands`` — core tidak mendapat branch fitur dan tidak tahu
arti command ini.

Plugin ini tidak memasang lifecycle hook: Downloader tidak memegang secret, dan
pembersihan browser sudah dijamin jalur ``_drop_session``/``close_all`` milik
core yang juga berjalan saat EOF dan shutdown.
"""

from mangareader_downloader import browser

OWNER = "mangareader.downloader"

COMMANDS = {
    "downloader.open": browser.cmd_open,
    "downloader.api": browser.cmd_api,
    "downloader.fetch": browser.cmd_fetch,
    "downloader.close": browser.cmd_close,
}


def install(host):
    """Daftarkan command pada host yang sedang hidup."""
    host.register_commands(OWNER, COMMANDS)
    return COMMANDS
