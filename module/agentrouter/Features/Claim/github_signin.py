#!/usr/bin/env python3
r"""Thin CLI shim -- the GitHub sign-in step lives in actions/github_signin.py.

Kept as its own file because callers run, and flow.py's SubprocessStrategy
spawns from %TEMP%\opencode, exactly:
    python github_signin.py [--probe|--click] <profileId>

Behaviour is unchanged: probe vs click mode, the status file
(github_signin_<id>.status.json), the screenshots and exit codes all come from
actions/github_signin.py, the single copy of the logic. This shim only forwards
argv (flags included) and pins the script directory on sys.path.
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from actions.github_signin import main  # noqa: E402  (sys.path set above)

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))