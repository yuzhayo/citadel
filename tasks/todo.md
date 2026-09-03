# CamoProf Google Enrollment — todo

## Phase 1 — Commit 1: provider boundary refactor (behavior-identical)

- [x] Task 1: Extract Google helpers + inspect/relogin into `module/sharedLogic/pyhost/providers/google/`; pyhost.py keeps thin delegates; `providers/__init__.py` owns `PyhostError`/`log`; test loader gets sys.path fix
  - Acceptance: existing `test_pyhost.py` passes **unchanged** (only the sys.path line added); no behavior change
  - Verification: venv python unittest suite green (23/23); Release build deploys `sharedLogic\pyhost\providers\`; deployed script-mode ping/shutdown smoke clean; `git diff --check`
- [x] Task 2: Commit 1 `refactor: extract Google inspect/relogin from pyhost.py into providers/google` — `da0779b`

### Checkpoint: Phase 1
- [x] Python suite green with unchanged tests
- [x] Build clean, deployed payload includes providers tree
- [x] Commit landed

## Phase 2 — Commit 2: enrollment feature

- [x] Task 3: Python enrollment state machine + four commands (`google.enrollment.start/status/finish/cancel`) with arm-before-navigate order and `_drop_session` lifecycle hook
- [x] Task 4: Python enrollment tests (origin/field validation, disarmed stores nothing, retype keeps last, challenge waits, wrong account, passkey `has_password:false`, finish one-shot, cleanup per session-death path, no plaintext in responses, armed-before-navigate call order) — 17 new tests, suite 40/40
- [x] Task 5: pyhost README v1 update (commands, states, error codes, ordering + plaintext contract)
- [x] Task 6: C# `PyHost` typed methods + `BrowserSessionCoordinator` routing
- [x] Task 7: Enrollment feature (`GoogleEnrollmentPolicy/Result/Service/Feature`) with has_password branch, DPAPI save inside service, cancellation
- [x] Task 8: `GoogleEnrollmentDialog` + LauncherView integration (single `RunEnrollmentAsync`, neutral start URL, dispose cancels)
- [x] Task 9: `tests/Module.Camoprof.Tests` project + slnx registration + policy/service/UI-contract tests — 49 tests green
- [x] Task 10: Cleanup — delete `AccountSetupDialog` + `DetectAsync`, supersede no-capture rule in `.docs/PLAN-camoprof-account-health.md`
- [x] Task 11: Full gates — `dotnet test Citadel.slnx -c Release` green (547), python suite green (40), `git diff --check`, secret scan of staged diff → Commit 2 `feat: add Google enrollment with one-shot credential capture` — `d4f689d`

## Phase 2b — Post-implementation audit fixes (2026-09-03)

- [x] Fix 1 (High): `google.enrollment.start` returned only after `page.goto` completed; under the sequential v1 loop a mid-navigation `cancel` queued behind it (dialog close could hang up to 45 s). Start now returns immediately once armed — navigation runs as an enrollment-owned task; teardown cancels and awaits it before closing the page; non-browser navigation failure surfaces as terminal state `failed`.
- [x] Fix 2 (Medium): emptying the password field used to keep the previously captured value — a stale/partial candidate could become the stored relog credential after a passkey login. The JS listener now forwards empty values and the Python callback clears the candidate (and reverts `password_observed` → `armed`).
- [x] Regression tests: `test_start_returns_while_navigation_pending`, `test_cancel_during_pending_navigation_is_immediate`, `test_navigation_failure_ends_enrollment_failed`, `test_clearing_field_clears_captured_candidate` (Python 44/44); `failed` state mapping (C# policy tests).
- [x] Doc sync: csproj dialog claim reworded to "code-reviewed; live smoke pending"; README + plan.md updated for non-blocking start, `failed` state, empty-clear behavior.
- [x] Fix 3 (self-cancel, second audit round): when the browser dies *from within* `_navigate_later`, that task itself calls `_drop_session` — `disarm_for_session` was cancelling the running task, so `CancelledError` landed mid-`__aexit__` of a genuinely-async context close, `_drop_session` deliberately doesn't swallow it, and the dead session stayed registered (phantom running / `PROFILE_BUSY`). Guard: `task is not asyncio.current_task()`. Regression test `test_browser_gone_during_navigation_drops_session` (yielding context manager; verified RED against the unguarded code — CancelledError in `__aexit__`, session `s1` retained — then GREEN with the guard).
- [x] Fix 4 (double window, live-smoke finding): Add Profile showed two Camofox windows — `session.open`'s neutral initial page plus the enrollment page (`ctx.new_page()` is a new window when headed). `start` now closes the resident page once the enrollment page is armed, so exactly one window is visible; teardown and the navigation-failure path restore a clean page and repair `sess["page"]` so later navigate/inspect/relogin hit a live page. C# opens the transient session page at `about:blank` (no google.com flash). Regression tests: `test_start_leaves_exactly_one_visible_window`, `test_navigation_failure_restores_resident_page`.
- [x] Fix 5 (context death on last-page close, found by the real-browser live smoke): in Playwright/Camoufox, closing a context's LAST page kills the context — the browser exits. With the resident page closed for single-window, the enrollment page became the last page, so teardown killed the browser and the "open a replacement if last" logic could never run (you cannot create a page in a dead context). Teardown now creates the replacement page BEFORE closing the enrollment page (`_close_enrollment_page`). The test fakes now emulate context death (`_FakeEnrollmentContext.dead`), so this class of bug can't pass the suite again. Verified live: `tools/enrollment_live_smoke.py` drives real Camoufox through open → start → status → cancel → **navigate on the repaired page** → close → shutdown, all PASS; fresh Citadel.Shell launch/stop smoke clean, no orphan processes.

### Checkpoint: Phase 2
- [x] All C# + Python tests green (547 C# across 7 projects; 40 Python)
- [x] UI-visible types carry no password property (reflection test)
- [x] Old dialog/detect path fully removed

## Phase 3 — Mandatory Add Profile ownership refactor

The checked Phase 2b double-window items are historical workaround evidence,
not final architecture. They are superseded by `tasks/plan.md` and may not be
used to claim the Add Profile feature complete.

- [x] Task 12: Add RED tests for the current closed `sess["page"]` invariant violation, Launcher double ownership, and manual-window-close stale session.
- [x] Task 13: Extract generic command registry and opaque session/page lease into pyhost core; preserve existing non-enrollment behavior.
- [x] Task 14: Move Add Profile Python semantics under `module/camoprof/Features/AddProfile/PyHost/` and register `camoprof.add_profile.*` commands.
- [x] Task 15: Claim and reuse the resident primary page during enrollment; route every terminal path through the shared lease.
- [x] Task 16: Make `AddProfileFeature` the sole Launcher-facing entry point; remove direct Add Profile calls to `_sessions.OpenAsync` and other lifecycle internals.
- [x] Task 17: Route existing-profile credential repair through the same public feature contract without exposing enrollment internals.
- [x] Task 18: Delete old `google.enrollment.*` routing, `took_over_window`, host backlink, `_restore_resident_page`, redundant session opening, and workaround-specific tests/comments.
- [x] Task 19: Add architecture guards for dependency direction, private session registry, unique command namespace, one page owner, and no Launcher lifecycle bypass.

### Checkpoint: Phase 3

- [x] Python and CamoProf focused suites pass.
- [x] Ordinary Launch, GitHub, inspect, relogin, close, shutdown, and EOF behavior remain green.
- [x] Search confirms shared core contains no Add Profile/Google semantics.
- [x] Search confirms Add Profile never accesses the mutable session registry.
- [x] Registered sessions always expose one live primary page owner.

## Phase 4 — Live smoke (operator-driven; not claimed PASS until run)

- [ ] Add Profile → login in browser → dialog auto-closes → row Active
- [ ] Restart app → Check Google → auto-relog without prompt
- [ ] Cancel mid-enrollment → row Unlinked, capture disarmed
- [ ] Close browser mid-enrollment → status reflects it, no secret retained
- [ ] No password in stderr logs or credenz files except `password.dat`
- [ ] Disposable-profile harness verifies the actual OS window count is exactly one while enrollment is active
- [ ] No orphan pyhost/Camoufox process, stale session, or `PROFILE_BUSY` remains after every terminal path
