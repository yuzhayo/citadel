#!/usr/bin/env python3
r"""Thin CLI shim -- the PAT-then-Quit chain lives in actions/grab_pat_quit.py.

Kept as its own file because callers run exactly:
    python grab_pat_quit.py <profileId>

Behaviour is unchanged: the status file (pat_quit_<id>.status.json), the
screenshots, the masked stdout, the skip-when-PAT-present rule and the exit
codes all come from actions/grab_pat_quit.py, the single copy of the logic.
This shim only forwards argv and pins the script directory on sys.path.
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from actions.grab_pat_quit import main  # noqa: E402  (sys.path set above)

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))