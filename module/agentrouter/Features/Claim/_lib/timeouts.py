#!/usr/bin/env python3
"""Canonical wait values -- ONLY the ones that were identical in every script.

Per-script values that differ on purpose stay local so the refactor cannot
change timing:

  flow.py InlineStrategy     (flow_1x_single.py): T_CHIP_S=15, T_POPUP_S=30,
                             SETTLE_S=2.5, TOTAL_BUDGET_S=600
  flow.py SubprocessStrategy (flow_1x.py):        SUB_T_CHIP_S=10,
                             STEP_TIMEOUT_S=300, SUB_TOTAL_BUDGET_S=1500
  actions/grab_pat_quit.py   : T_CHIP_S=30, TOTAL_BUDGET_S=300
  actions/quit_session.py    : polls every 0.5s (this module polls at 1.0s)
  find_checkin.py            : its own budget (recon, unchanged)

Unify those only with the owner's sign-off.

    goto 60s | networkidle 15s (best-effort) | menu item 10s | route 15s | fill 20s
"""

from __future__ import annotations

T_GOTO = 60_000       # page.goto(..., wait_until="domcontentloaded")
T_IDLE = 15_000       # page.wait_for_load_state("networkidle") -- best effort
T_MENU_MS = 10_000    # click a menu item / tab
T_ROUTE_MS = 15_000   # page.wait_for_url after a menu route change
T_FILL_S = 20         # wait for a generated value to appear in an input
