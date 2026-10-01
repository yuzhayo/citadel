#!/usr/bin/env python3
r"""Thin CLI shim -- the api_key capture lives in actions/grab_api_key.py.

Kept as its own file because callers run, and flow.py's SubprocessStrategy
spawns from %TEMP%\opencode, exactly:
    python grab_api_key.py <profileId>

Behaviour is unchanged: the status file (api_key_<id>.status.json), the masked
stdout, the exit codes, and the full secret written ONLY to
%TEMP%\opencode\agentrouter-<id>.json all come from actions/grab_api_key.py.
This shim only forwards argv and pins the script directory on sys.path.
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from actions.grab_api_key import main  # noqa: E402  (sys.path set above)

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))