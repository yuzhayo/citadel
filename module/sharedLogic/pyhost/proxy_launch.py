"""Validation for the optional C# -> Camofox proxy launch payload.

This module never reads the pool and never logs or returns credentials.
"""

from urllib.parse import urlparse

from providers import PyhostError

_BROWSER_SCHEMES = frozenset(("http", "https", "socks5"))


def proxy_launch_options(message):
    """Return {} for Direct or {'proxy': PlaywrightProxySettings} for Proxy."""
    raw = message.get("proxy")
    if raw is None:
        return {}
    if not isinstance(raw, dict):
        raise PyhostError("BAD_PROXY", "proxy harus object")

    server = raw.get("server")
    if not isinstance(server, str):
        raise PyhostError("BAD_PROXY", "proxy.server harus string")
    parsed = urlparse(server)
    if (parsed.scheme.lower() not in _BROWSER_SCHEMES
            or not parsed.hostname
            or parsed.port is None
            or parsed.username is not None
            or parsed.password is not None
            or parsed.path not in ("", "/")
            or parsed.query
            or parsed.fragment):
        raise PyhostError("BAD_PROXY", "proxy.server tidak valid atau tidak kompatibel")

    result = {"server": server}
    username = raw.get("username")
    password = raw.get("password")
    if username is not None:
        if not isinstance(username, str) or not username:
            raise PyhostError("BAD_PROXY", "proxy.username tidak valid")
        result["username"] = username
    if password is not None:
        if username is None or not isinstance(password, str):
            raise PyhostError("BAD_PROXY", "proxy.password tidak valid")
        result["password"] = password
    return {"proxy": result}
