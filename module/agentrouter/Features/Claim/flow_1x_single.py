#!/usr/bin/env python3
r"""Thin CLI shim -- the Claim flow lives in flow.py (InlineStrategy).

Kept as its own file because callers (and muscle memory) run:
    python flow_1x_single.py <profileId>

Behaviour is unchanged: the 7 steps, the status file, exit codes, and masking
all come from flow.py, the single copy of the logic. This shim only forwards
argv, and pins the script directory on sys.path so `import _lib` / `import flow`
resolve no matter the working directory.
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from flow import main  # noqa: E402  (sys.path set above)

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
