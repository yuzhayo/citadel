#!/usr/bin/env python3
r"""Thin CLI shim -- the PAT capture lives in actions/grab_pat.py.

Kept as its own file because callers run, and flow.py's SubprocessStrategy
spawns from %TEMP%\opencode, exactly:
    python grab_pat.py <profileId>

Behaviour is unchanged: the "pat already stored -> skip generation untouched"
rule, the status file (pat_<id>.status.json), the masked stdout and the exit
codes all come from actions/grab_pat.py, the single copy of the logic. This
shim only forwards argv and pins the script directory on sys.path.
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from actions.grab_pat import main  # noqa: E402  (sys.path set above)

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))