# Implementation Plan: CamoProf Google Enrollment (type-once credential capture)

## Overview

Replace the two-step account pairing ritual (login in browser → Detect → retype password in `AccountSetupDialog`) with type-once enrollment: user logs into Google normally in the headed browser; the password they typed is captured once by an armed page listener, crosses the seam exactly once, and is DPAPI-encrypted by `GoogleCredentialStore`. Full approved plan lives in the session plan file; this is the repo-local execution record.

## Architecture Decisions

1. **Arm before navigate.** Enrollment page is created, listener installed, `Armed` confirmed — *then* the page navigates to Google login. Launcher opens new-profile sessions on a neutral URL and never navigates to Google itself for enrollment.
2. **Event-driven capture.** `page.expose_function` + init script with capture-phase `input` listener; JS checks exact hostname `accounts.google.com` and audited field `input[name="Passwd"][type="password"]` (fallback `input[type="password"]` only on accounts.google.com). Python re-validates page URL on receipt. Listener only on the enrollment page.
3. **Cleanup follows session lifecycle.** All session-removal paths funnel through `_drop_session` (pyhost.py:178) — enrollment teardown hooks there. No path retains the secret after its session dies.
4. **Dedicated enrollment page** in the resident context (`ctx.new_page()`); resident page untouched; teardown closes it (+ clean replacement if last page).
5. **Waiting happens between commands.** Four fast commands; 10-minute lifetime enforced by internal deadline checked in `status`, expiring to `Expired` with full disarm.
6. **Password crosses the seam once.** Only `GoogleEnrollmentService` calls `finish`; it DPAPI-saves *before* returning a non-secret `GoogleEnrollmentResult`. UI-visible types carry no password property. Secret lifetime: single Python variable, overwritten on retype, dropped on consume/teardown; never logged, never persisted (no absolute zero-memory claim).
7. **Wrong account is terminal refusal** — `WRONG_ACCOUNT`, no credential overwritten.
8. **`ActiveWithoutPassword` is not success.** `Complete` + `has_password: false` → outcome produced *without calling `finish`*, straight to cancel/teardown; dialog closes non-success with honest message; row stays Unlinked; nothing saved.
9. **Dialog owns cancellation.** `Loaded` → `EnrollAsync`; one CTS; Cancel and window Close both cancel + await cleanup; `_closed` guard blocks post-close UI access; LauncherView.Dispose flows through the same token.
10. **One entry contract** — `GoogleEnrollmentFeature`. No new interface hierarchy; test seams are delegate/fake collaborators. Enrollment folder under `Providers/Google/` per operator spec.

## Task List

Tracked in `tasks/todo.md`.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| `expose_function` vs Camoufox hardening | Capture never fires | Plain DOM API; fake-page tests first; live smoke before commit 2 is "done" |
| Google DOM change | Missed capture → stays Armed | Audited selector; honest failure mode |
| Coordinator gate vs 500 ms polls | UI latency | Status is fast; measure, relax if needed |
| Missed teardown path | Secret held too long | Single-point `_drop_session` hook, tested per path |
| WPF re-entrancy mid-enrollment | Crash / dead-window callback | Dialog CTS + `_closed` guard + Launcher dispose cancel; tested |
| New test project into `Citadel.slnx` | Build breakage | Follow `Module.Mangareader.*.Tests` pattern exactly |

## Open Questions

None — plan approved 2026-09-03 after operator/codex audit (4 lifecycle contracts incorporated).
