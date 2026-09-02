# CamoProf Google Enrollment — todo

## Phase 1 — Commit 1: provider boundary refactor (behavior-identical)

- [x] Task 1: Extract Google helpers + inspect/relogin into `module/sharedLogic/pyhost/providers/google/`; pyhost.py keeps thin delegates; `providers/__init__.py` owns `PyhostError`/`log`; test loader gets sys.path fix
  - Acceptance: existing `test_pyhost.py` passes **unchanged** (only the sys.path line added); no behavior change
  - Verification: venv python unittest suite green (23/23); Release build deploys `sharedLogic\pyhost\providers\`; deployed script-mode ping/shutdown smoke clean; `git diff --check`
- [ ] Task 2: Commit 1 `refactor: extract Google inspect/relogin from pyhost.py into providers/google`

### Checkpoint: Phase 1
- [ ] Python suite green with unchanged tests
- [ ] Build clean, deployed payload includes providers tree
- [ ] Commit landed

## Phase 2 — Commit 2: enrollment feature

- [ ] Task 3: Python enrollment state machine + four commands (`google.enrollment.start/status/finish/cancel`) with arm-before-navigate order and `_drop_session` lifecycle hook
- [ ] Task 4: Python enrollment tests (origin/field validation, disarmed stores nothing, retype keeps last, challenge waits, wrong account, passkey `has_password:false`, finish one-shot, cleanup per session-death path, no plaintext in responses, armed-before-navigate call order)
- [ ] Task 5: pyhost README v1 update (commands, states, error codes, ordering + plaintext contract)
- [ ] Task 6: C# `PyHost` typed methods + `BrowserSessionCoordinator` routing
- [ ] Task 7: Enrollment feature (`GoogleEnrollmentPolicy/Result/Service/Feature`) with has_password branch, DPAPI save inside service, cancellation
- [ ] Task 8: `GoogleEnrollmentDialog` + LauncherView integration (single `RunEnrollmentAsync`, neutral start URL, dispose cancels)
- [ ] Task 9: `tests/Module.Camoprof.Tests` project + slnx registration + policy/service/UI-contract tests
- [ ] Task 10: Cleanup — delete `AccountSetupDialog` + `DetectAsync`, supersede no-capture rule in `.docs/PLAN-camoprof-account-health.md`
- [ ] Task 11: Full gates — `dotnet test Citadel.slnx -c Release` green, python suite green, `git diff --check`, secret scan of staged diff → Commit 2 `feat: add Google enrollment with one-shot credential capture`

### Checkpoint: Phase 2
- [ ] All C# + Python tests green
- [ ] UI-visible types carry no password property (reflection test)
- [ ] Old dialog/detect path fully removed

## Phase 3 — Live smoke (operator-driven; not claimed PASS until run)

- [ ] Add Profile → login in browser → dialog auto-closes → row Active
- [ ] Restart app → Check Google → auto-relog without prompt
- [ ] Cancel mid-enrollment → row Unlinked, capture disarmed
- [ ] Close browser mid-enrollment → status reflects it, no secret retained
- [ ] No password in stderr logs or credenz files except `password.dat`
