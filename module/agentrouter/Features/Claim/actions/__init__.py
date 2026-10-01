#!/usr/bin/env python3
"""Agentrouter Claim ACTIONS -- the state-changing steps, one module each.

Every module here was a standalone top-level script; the top-level file of the
same name is now a thin CLI shim that forwards argv to ``actions.<name>.main``.
The layer sits between ``_lib`` (shared primitives) and ``flow.py`` (the Claim
flow with its pluggable strategy):

    _lib/      shared primitives -- paths, ids, logging, status, table, creds,
               timeouts, browser helpers (launch / free_lock / chip probe)
    actions/   one module per step:
                 quit_session.py    log out: chip -> Quit -> verify /login
                 github_signin.py   GitHub sign-in (--probe read-only / --click)
                 grab_api_key.py    API Token menu -> Copy icon -> save api_key
                 grab_pat.py        Personal Settings -> Generate Token -> pat
                 grab_pat_quit.py   PAT then Quit, both in one browser
    flow.py    the Claim flow: InlineStrategy (one browser, inline steps) and
               SubprocessStrategy (one script + browser per step)

Each module pins the Claim root (its parent directory) on ``sys.path``, so
``_lib`` and ``open_agentrouter`` resolve from any cwd -- including when the
file is copied into %TEMP%\\opencode for the subprocess flow.
"""

from __future__ import annotations

STEP_MODULES = (
    "quit_session",
    "github_signin",
    "grab_api_key",
    "grab_pat",
    "grab_pat_quit",
)
