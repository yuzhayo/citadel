"""Provider packages untuk pyhost.

Level ini memegang primitif protokol yang dipakai lintas provider:
error terstruktur dan log stderr. stdout tetap murni protokol —
kontrak transport tetap dimiliki pyhost.py / README.md.
"""

import sys


class PyhostError(Exception):
    """Kegagalan terstruktur: ``code`` naik ke wire sebagai ``error.code``."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def log(msg):
    """Log HANYA ke stderr — stdout adalah protokol."""
    print("[pyhost] " + msg, file=sys.stderr, flush=True)
