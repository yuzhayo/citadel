# CamoProf Google Enrollment — todo

## Phase 1 — Commit 1: provider boundary refactor (behavior-identical)

- [x] Task 1: Extract Google helpers + inspect/relogin into `module/sharedLogic/pyhost/providers/google/`; pyhost.py keeps thin delegates; `providers/__init__.py` owns `PyhostError`/`log`; test loader gets sys.path fix
  - Acceptance: existing `test_pyhost.py` passes **unchanged** (only the sys.path line added); no behavior change
  - Verification: venv python unittest suite green (23/23); Release build deploys `sharedLogic\pyhost\providers\`; deployed script-mode ping/shutdown smoke clean; `git diff --check`
- [x] Task 2: Commit 1 `refactor: extract Google inspect/relogin from pyhost.py into providers/google` — `da0779b`

### Checkpoint: Phase 1
- [ ] Python suite green with unchanged tests
- [ ] Build clean, deployed payload includes providers tree
- [ ] Commit landed

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

### Checkpoint: Phase 2
- [x] All C# + Python tests green (547 C# across 7 projects; 40 Python)
- [x] UI-visible types carry no password property (reflection test)
- [x] Old dialog/detect path fully removed

## Phase 3 — Live smoke (operator-driven; not claimed PASS until run)

- [ ] Add Profile → login in browser → dialog auto-closes → row Active
- [ ] Restart app → Check Google → auto-relog without prompt
- [ ] Cancel mid-enrollment → row Unlinked, capture disarmed
- [ ] Close browser mid-enrollment → status reflects it, no secret retained
- [ ] No password in stderr logs or credenz files except `password.dat`
