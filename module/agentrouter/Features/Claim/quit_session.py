#!/usr/bin/env python3
r"""Thin CLI shim -- the logout step lives in actions/quit_session.py.

Kept as its own file because callers run, and flow.py's SubprocessStrategy
spawns from %TEMP%\opencode, exactly:
    python quit_session.py <profileId>

Behaviour is unchanged: the same status file (quit_<id>.status.json), the same
screenshots, exit codes, and 180s budget all come from actions/quit_session.py,
the single copy of the logic. This shim only forwards argv and pins the script
directory on sys.path so `import actions` resolves from any cwd.
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from actions.quit_session import main  # noqa: E402  (sys.path set above)

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))