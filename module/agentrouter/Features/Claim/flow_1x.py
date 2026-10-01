#!/usr/bin/env python3
r"""Thin CLI shim -- the Claim flow lives in flow.py (SubprocessStrategy).

Kept as its own file because callers (and muscle memory) run:
    python flow_1x.py <profileId>

Behaviour is unchanged: the 7 steps spawn the same step scripts from
%TEMP%\opencode with the Citadel venv python, the status file is still
flow_1x_<profileId>.status.json, and exit codes / masking all come from
flow.py, the single copy of the logic. This shim only forwards argv and pins
the default strategy to "subprocess".
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from flow import main  # noqa: E402  (sys.path set above)

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:], default_strategy="subprocess"))