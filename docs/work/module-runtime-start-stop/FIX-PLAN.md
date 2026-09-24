# Module Runtime Start/Stop — audit fix plan

Status: Phases 0–4 COMPLETE (A–D closed; real preflight + citizen builds + bump-build-run package green). Finding F (Yuzvid WebView2 dispose on Stop) FIXED + **re-smoke PASS (SOLID)** on 3.0.26 — log proof: 4/4 clean Start→Stop→Start cycles, 0× `0x8007139F`. Phase 5: E1 PASS (cold-boot log); E2 partial (MangaReader opened once); E3–E13 not evidenced in logs (coordinator does not log runtime state transitions; queue empty so E5 preserve-set unseeded). See Phase 5 results below.
Source: post-Gate-7 audit (2026-09-24). Findings A–E from review of Gates 1–7 vs
`PLAN.md`.

## Goal

Close every audit finding so the original plan’s acceptance criteria are honest,
docs match reality, Force Stop has full state coverage, and Gate 7 includes a
runnable manual smoke checklist.

| ID | Severity | Finding |
|----|----------|---------|
| A | MEDIUM | `PauseAndDrainAsync` rewrites `Failed` and decision-required (`AwaitingSourceFallback`) to `Paused`; PLAN L371 requires they stay unchanged |
| B | LOW | Stale “Application-lifetime owner” docs on Downloader service + online process |
| C | LOW | `PLAN.md` still says “not implemented” |
| D | INFO | No test for Force Stop from `Running` + recorded Stop error (only from `Stopping`) |
| E | INFO | Gate 7 live manual smoke never run |

## Non-goals

- No behavior change to user-facing Stop/StopAll/ForceAbort persistence rules
  beyond finding A (user Stop may still park non-Completed rows — that path is
  not the module Stop barrier).
- No Router/Coordinator/Gate architecture changes.
- No new shared UI primitives.
- No Yuzvid-specific tests (out of this fix scope; covered by original plan’s
  “no Yuzvid branch” rule).

---

## Phase 0 — Preconditions

1. Solution builds clean today: `dotnet build "C:/VSCODE/citadel/Citadel.slnx" -v q`
2. Full suite green: `dotnet test "C:/VSCODE/citadel/Citadel.slnx" -v q` → 1047/1047
3. Skill acknowledgement gap: yuzskill required `engineering-quality`,
   `modular-architecture`, `planning-and-delivery`, `verification-and-review`,
   `stack-guidance`, `citadel-project` — files not present under project
   `.agents/skills/` (only `citadel-shared-ui` and `citadel-feature-modularity`
   exist). Execute without those receipts; do not block on missing skill files.
4. Close any running `Citadel.Shell` before Release build (file-lock MSB3026).

**Exit criteria:** clean build + green baseline recorded.

---

## Phase 1 — Finding A: barrier state preservation (MEDIUM)

### Decision (locked)

**Fix code + tests to match PLAN L371.** Do not amend PLAN.

Rationale:

- PLAN is the acceptance contract: *“Preserve `Completed`, `Failed`, and
  decision-required states unchanged.”*
- Current tests pin the opposite for `Failed` with an intentional comment —
  that comment is the bug (it documented a shortcut through `StopMany`).
- `ReconcileOnLoad` already preserves `Completed` / `Failed` / `Paused` /
  `AwaitingSourceFallback` on restart; the Stop barrier must not undo that
  durability contract while the process is still up.
- Losing `AwaitingSourceFallback` on module Stop hides `CanChooseFallback` and
  strands a pending operator decision until something re-enters that state.

**Decision-required set for this barrier** (same as load recovery + plan wording):

| State | On PauseAndDrain |
|-------|------------------|
| `Completed` | unchanged |
| `Failed` | unchanged |
| `AwaitingSourceFallback` | unchanged |
| `Paused` | stays `Paused` (already parked; no rewrite needed beyond idempotence) |
| `Queued` / `ManifestReady` / other runnable | → `Paused` |
| Active (`Downloading`, `Pausing`, etc.) | → `Pausing` then settle → `Paused` |

`StopMany` (user Stop, group Stop, StopAll inside `Dispose`) keeps its existing
“everything non-Completed → Paused/Pausing” rewrite. Only the **module Stop
barrier** (`PauseAndDrainAsync`) gets the preserve set.

### Code changes

**File:** `module/mangareader/Features/Downloader/Queue/DownloadQueueFeature.cs`

1. Stop calling `StopMany(all ids)` from `PauseAndDrainAsync`.
2. Add a private barrier-only rewrite, e.g.
   `ParkRunnableForBarrier()`:
   - Under `_gate` + `Commit`:
     - skip if `State is Completed or Failed or AwaitingSourceFallback`
     - else `State = IsActive(jobId) ? Pausing : Paused` (keep existing
       `Warning` — barrier passes `reason: null` today)
   - Still `_manualDownloadsAfterManifest.ExceptWith` only for jobs actually
     rewritten (or all targets — match current StopMany side effect for
     runnable/active only; do not clear manual marks for preserved Failed rows
     if that mark cannot exist — verify: Failed is not manual-queued; safe to
     ExceptWith all targets as today).
   - Return `CancelActive(targets)` unchanged.
3. Leave final residual `Pausing → Paused` commit as-is (only touches
   `Pausing`; cannot clobber preserved states).
4. Update the method XML doc on `PauseAndDrainAsync` (L516–525): replace
   “every non-Completed job ends Paused (including Failed …)” with explicit
   preserve list matching PLAN L371.
5. **Do not** change `ForceAbort` persistence set unless a later finding
   requires it — force path already rewrites non-Completed/non-Paused with a
   warning by design (PLAN force-stop best-effort persist).

**File:** `module/mangareader/Features/Downloader/DownloaderBackgroundService.cs`

- Doc comment only (finding B in same phase if convenient — see Phase 3).

### Test changes

**File:** `tests/Module.Mangareader.Downloader.Tests/DownloadQueuePauseDrainTests.cs`

1. **Update** `PauseAndDrain_MarksRunnableAsPaused_AndRejectsStartResume`:
   - Seed also `awaiting` job in `AwaitingSourceFallback` with
     `FallbackCandidates` if useful (state alone is enough).
   - After drain:
     - `queued` → `Paused` (keep)
     - `done` → `Completed` (keep)
     - `failed` → **`Failed`** (change from `Paused`)
     - `awaiting` → **`AwaitingSourceFallback`** (new assert)
   - Keep Start/Resume rejection asserts on the paused runnable job.
2. **Add** `PauseAndDrain_PreservesDecisionRequiredStates_AndStillQuiesces`:
   - Seed: one `Failed`, one `AwaitingSourceFallback` (+ candidates), one
     `Queued`.
   - Drain succeeds; no active scheduler work.
   - Failed + awaiting unchanged (state and `FallbackCandidates` intact).
   - Queued → Paused.
   - Start/Resume still rejected while quiescing.
3. **Add** (optional but cheap) `PauseAndDrain_IsIdempotent_WithPreservedStates`:
   - Second drain does not flip Failed → Paused.

**Regression watch:** any other test asserting Failed→Paused after barrier —
grep `PauseAndDrain` + `Failed` before Phase 1 exit.

### Phase 1 exit criteria

- Targeted: `dotnet test ... --filter DownloadQueuePauseDrainTests` green.
- No other Downloader tests regress.
- PLAN L371 satisfied without editing PLAN.

---

## Phase 2 — Finding D: Force Stop from Running + stop error (INFO)

### Gap

`ForceStop_FromRunningWithStopError_ReachesStopped_WithoutAwaitingDrain` in
`ModuleRuntimeHostTests` actually enters `Stopping` first (hangs Stop), then
force-stops — name overclaims. Coordinator allows:

```text
allowed = Stopping || (Running && LastError non-empty)
```

The `Running && LastError` branch has no dedicated test.

### Code changes

None (behavior already correct in `ModuleRuntimeCoordinator.ForceStopAsync`).

### Test changes

**File:** `tests/Citadel.Uia/ModuleRuntimeHostTests.cs` (preferred — host +
coordinator) **or** `ModuleRuntimeCoordinatorTests.cs` if purely coordinator.

1. **Add** `ForceStop_FromRunningWithStopError_WithoutPriorStopping`:
   - Module with `StopRuntimeAsync` that throws `"stop boom"` (reuse
     `OrderProbeModule.FailStop` pattern or a local `FailingStopModule`).
   - `StartAsync` → Running.
   - `StopAsync` throws; assert:
     - state `Running`
     - `LastError` contains `stop boom`
     - runtime lifetime still alive
   - `ForceStopAsync` succeeds without entering `Stopping` first.
   - Assert final `Stopped`, lifetime destroyed, `LastError` cleared by
     `CompleteForceStop` (verify slot API — if LastError is not cleared,
     assert actual behavior and document).
2. **Rename or clarify** existing host test comment: it covers preempt from
   `Stopping`, not the Running+error path (avoid two tests claiming the same
   name).
3. Keep `ForceStop_FromHealthyRunning_IsRejected` as-is.

### Phase 2 exit criteria

- New test green.
- UIA suite green.

---

## Phase 3 — Findings B + C: documentation (LOW)

### B — stale lifetime wording

| File | Line (approx) | Change |
|------|----------------|--------|
| `module/mangareader/Features/Downloader/DownloaderBackgroundService.cs` | 13–15 | “Application-lifetime owner…” → “Runtime-lifetime owner of Downloader work. Constructed in `MangaReaderModule.StartRuntimeAsync`; disposed when the shell destroys the runtime lifetime. Routed views borrow its context…” |
| `module/mangareader/Features/Downloader/Catalog/DownloaderOnlineProcess.cs` | 4–6 | Same: “Runtime-lifetime owner of Downloader browse execution…” (created inside the service ctor → runtime lifetime) |

No behavior change.

### C — PLAN status

**File:** `docs/work/module-runtime-start-stop/PLAN.md` line 3:

```text
Status: implemented (Gates 1–7). Audit fixes A–E tracked in FIX-PLAN.md.
```

Optional one-line under Stop section (L371 area) only if the fix needs a
cross-ref; prefer not to duplicate the preserve list.

Also update FIX-PLAN status lines as phases complete:

```text
Status: plan ready → Phase 1 in progress → … → complete (all findings closed).
```

### Phase 3 exit criteria

- Grep `Application-lifetime` under `module/mangareader` → zero false owners.
- PLAN status not “not implemented”.

---

## Phase 4 — full validation (real workflow only)

Real gates (same as `tools/Commit-Push-Release.ps1` preflight and
`.github/workflows/ci.yml`). Do not invent alternate build commands.

Run from `C:/VSCODE/citadel`:

```powershell
# 1. Focused (Debug is fine for local iteration)
dotnet test "C:/VSCODE/citadel/Citadel.slnx" -v q --filter "DownloadQueuePauseDrainTests"
dotnet test "C:/VSCODE/citadel/Citadel.slnx" -v q --filter "ForceStop"

# 2. Real preflight — Commit-Push-Release.ps1 / ci.yml
dotnet restore Citadel.slnx
dotnet test Citadel.slnx --configuration Release --no-restore --nologo

# 3. Real citizen compile (same list as Commit-Push-Release.ps1)
dotnet build module/ftf/Module.FTF.csproj -v q --nologo
dotnet build module/proxy/Module.Proxy.csproj -v q --nologo
dotnet build module/blank/Module.Blank.csproj -v q --nologo
dotnet build module/camoprof/Module.Camoprof.csproj -v q --nologo
dotnet build module/mangareader/Module.Mangareader.csproj -v q --nologo

# 4. Real Release package (must bump first — see note)
#    tools/Build-Release.ps1 alone FAILS when version.props == latest v* tag
#    (Velopack: equal/greater release). Real local path:
#    bump-build-run.bat → Bump-Build-Run.ps1 (patch bump) → Build-Release.ps1
#    → artifacts/publish/win-x64 + Releases/Yuzhayo.Citadel-win-Setup.exe
```

Do **not** use `dotnet build -c Release` on the solution as the release gate —
that is not what CI or Commit-Push-Release runs.

**Pass bar:**

- 0 failed tests on `dotnet test --configuration Release`
- All 5 citizen projects compile
- `bump-build-run.bat` exits 0 and emits Setup.exe + full nupkg + launches Shell
- Known flaky (`ManifestSchedulerPrefetchesEightAndManualStartBypassesTheLimit`,
  `ForceAbort_SetsTerminal_CancelsWithoutWait_AndSurvivesDispose`) —
  re-run that class isolated; do not “fix” unless reproducible

### Phase 4 results (2026-09-24)

| Check | Result | Evidence |
|-------|--------|----------|
| Release preflight | PASS | `dotnet restore` + `dotnet test Citadel.slnx --configuration Release --no-restore --nologo` → **1049/1049, 0 failed** |
| 5 citizens (preflight list) | PASS | ftf, proxy, blank, camoprof, mangareader → 0/0 each |
| Release package | PASS | `bump-build-run.bat` → version **3.0.25**, Setup.exe + full nupkg, Shell **PID 12660** |

Notes:
- Bare `Build-Release.ps1` at 3.0.24 correctly failed Velopack (tag v3.0.24 equal) — expected; real path always bumps first via `bump-build-run.bat`.
- Pre-existing Yuzvid CS8602 ×4 (LocalProxyServer) — out of FIX-PLAN scope; not in Commit-Push-Release citizen list.
- Focused filters earlier: DownloadQueuePauseDrainTests 6/6, ForceStop 3/3, Citadel.Uia host 10/10.

### Phase 4 exit criteria

| Check | Target | Real command | Result |
|-------|--------|--------------|--------|
| Full test | all green | `dotnet test Citadel.slnx --configuration Release --no-restore --nologo` | PASS 1049/1049 |
| Citizens | 5/5 compile | `dotnet build module/*/Module.*.csproj` (same as preflight) | PASS |
| Release package | Setup.exe + nupkg | `bump-build-run.bat` → `Build-Release.ps1` | PASS 3.0.25 (Finding F fix rebuilt as **3.0.26**) |

---

## Phase 5 — Finding E: live manual smoke (INFO)

### Smoke findings (new during Phase 5)

| ID | Severity | Finding | Status |
|----|----------|---------|--------|
| F | HIGH | Yuzvid Start → Stop → Start: **Browser engine failed to initialize** (`0x8007139F` — group/resource not in correct state). Root cause: `YuzvidBrowserController.Dispose` only shut down popups + local proxy; the main WPF `WebView2` control was never disposed, so `msedgewebview2` kept `WebView2ProfileV2` locked. Second `CreateView` → `CreateAsync` same UDF → invalid state. PLAN L579 already required dispose on explicit Stop. | **PASS (SOLID)** — Fixed in 3.0.26. Log proof from `popup-trace.log`: pre-fix storm 19:48–19:50 = 16× `failed: 0x8007139F`; post-fix (shell started 19:56:53 from `artifacts\publish\win-x64`) = **4/4 init cycles reach `ready`, 0 failed** (19:56:59, 19:57:06, 19:59:27, 19:59:35), each with working nav (`nav-done ok=True` on igay69.com) + popups/redirect-block. No `failed to create` for yuzvid in shell `log.txt`. |

Automated tests cannot prove WPF/tray/dialog wiring. Run once on a real shell
after Phase 4. Record PASS/FAIL per row in this file (or a sibling
`SMOKE-RESULTS.md`).

### Phase 5 results (2026-09-24, log-derived)

Sources: `%LocalAppData%\Citadel\Yuzvid\popup-trace.log`, `%AppData%\Citadel\log.txt`,
`%LocalAppData%\Citadel\MangaReader\downloads\queue.json`. Shell path under test:
`artifacts\publish\win-x64\` (3.0.26), started 19:56:53 and again 19:59:17.

| # | Result | Evidence |
|---|--------|----------|
| F | **PASS (SOLID)** | 4/4 `[init] ready` after 19:56, 0× `0x8007139F`; pre-fix contrast 16 failures 19:48–19:50 |
| E1 | **PASS** | Cold boot 19:56:53: blank/ftf/camoprof/mangareader/proxy/yuzvid all `registered`; no `failed to create`; no heavy-runtime auto-start errors |
| E2 | **PARTIAL** | MangaReader view opened 19:57:15/19:57:24 (`Layout` slot warnings only — pre-existing, not runtime errors). “Runtime stopped” / Start button text not logged |
| E3 | **UNVERIFIABLE (log)** | Coordinator does not log Start→Running transitions to `log.txt` |
| E4 | **UNVERIFIABLE (log)** | No second-downloader / navigate-away markers in log |
| E5 | **UNVERIFIABLE (live)** | `queue.json` = `{Jobs: []}` last write 19:57:29 — no Failed/Awaiting rows seeded to prove preserve set live. Finding A covered by unit tests 6/6 (`DownloadQueuePauseDrainTests`) |
| E6 | **UNVERIFIABLE (log)** | No Force Stop / confirm-dialog lines in `log.txt` (path not logged or not exercised this session) |
| E7 | **UNVERIFIABLE (log)** | Same as E6 |
| E8 | **PARTIAL** | App restarted 19:57→19:59 (process exit implied by second `App starting` on same publish path); no explicit tray-Exit log line between those entries |
| E9 | **UNVERIFIABLE (log)** | Settings-host shutdown path not distinguishable from E8 in log |
| E10 | **UNVERIFIABLE** | No module-folder delete / Removal pending exercised this session |
| E11 | **UNVERIFIABLE** | Same as E10 |
| E12 | **UNVERIFIABLE** | Same as E10 |
| E13 | **NOT RUN** (optional) | Session end / logoff not performed |

Notes:
- Shell log does **not** emit Coordinator state names (`Stopping`/`Stopped`/`ForceStop`/`RemovalPending`) — UI-only rows cannot be proven from `log.txt` alone without operator click-through or adding structured log lines.
- E5 live proof requires seeding at least one `Failed` + one `AwaitingSourceFallback` job before Stop; current queue is empty.
- Finding F re-smoke is complete and does not depend on E3–E13.

### Environment (real workflow)

- Local run: `bump-build-run.bat` → `tools/Bump-Build-Run.ps1` →
  kills Shell, bumps `version.props`, `Build-Release.ps1`, launches
  `artifacts\publish\win-x64\Citadel.Shell.exe`
- No debugger attached; normal user session
- Watcher-enabled (at least one discovered citizen, ideally MangaReader)
- Ship path (after smoke): `commit-push-release.bat "message"` →
  preflight → commit → ci.yml → release.yml (do not run until explicitly requested)

### Checklist

| # | Step | Expected |
|---|------|----------|
| E1 | Cold boot | Sidebar shows citizens; none auto-starts heavy runtime (Manga Downloader not running until Start) |
| E2 | Open MangaReader (stopped) | Lightweight host: “Runtime stopped” + **Start manga-reader** |
| E3 | Start | Runtime running; normal view mounts; header shows **Stop manga-reader** |
| E4 | Navigate away → back | Same runtime; no second Downloader; still Running |
| E5 | Stop from header or host | Stopping… → Stopped; queue jobs resumable Paused; **Failed jobs still Failed**; **Awaiting source still Awaiting source** (Finding A live proof) |
| E6 | Stop again after failed Stop (if reproducible) or Force path | Force stop button + confirm dialog copy about Resume/recovery |
| E7 | Force Stop confirm | Reaches Stopped without long drain hang; unfinished jobs Paused + “Force stopped” warning where applicable |
| E8 | Tray Exit with healthy runtimes | All stop; app exits; no hard kill |
| E9 | Settings-host shutdown path | Same as E8 (one `RequestShutdownAsync`) |
| E10 | Delete module source folder while Running | Sidebar item remains until release; host shows Removal pending + **Retry removal** (or release succeeds and item disappears only after stop) |
| E11 | Rediscover module during Removal pending | After successful release, newest descriptor registers (Option A); no duplicate route |
| E12 | Retry removal after artificial stop failure | Clears pending or re-arms error; no crash |
| E13 | Session end / logoff (optional) | No false async drain promise; next boot recovers queue as Paused |

### Phase 5 exit criteria

- All rows PASS, or failures filed as new findings with repro steps.
- Finding F re-smoke: **DONE** — PASS (SOLID), log-proven (results table above).
- Finding E (Gate 7 live smoke): still open until E3–E7 + E10–E12 have
  operator-observed or log-observable proof (E13 optional).
- Update FIX-PLAN `Status: complete` only when A–E closed.
- **Current gate:** E open. Remaining rows need operator click-through
  (seed Failed/Awaiting jobs → Stop → verify states; exercise Force Stop;
  exercise Removal pending) **or** Coordinator structured state logging
  so the next session can be scored from `log.txt` alone.

---

## Execution order and dependencies

```text
Phase 0 baseline
    │
    ▼
Phase 1 (A) ── code + DownloadQueuePauseDrainTests
    │
    ▼
Phase 2 (D) ── Force Stop Running+error test (independent of A; can parallel after 0)
    │
    ▼
Phase 3 (B+C) ── docs (can parallel with 1–2)
    │
    ▼
Phase 4 ── real preflight (Release test + 5 citizens) + bump-build-run.bat
    │        [DONE 2026-09-24: 1049/1049, citizens OK, 3.0.25 packaged]
    ▼
Phase 5 (E) ── operator smoke E1–E13 on running shell (PID from bump-build-run)
    │        [Finding F PASS 2026-09-24; E1 PASS; E2/E8 partial;
    │         E3–E7/E9–E12 UNVERIFIABLE from logs; E13 not run — E still open]
    ▼
(optional, only if operator asks) commit-push-release.bat
```

Recommended serial order for one operator: **0 → 1 → 2 → 3 → 4 → 5**.

---

## File change map

| File | Phases | Change type |
|------|--------|-------------|
| `module/mangareader/Features/Downloader/Queue/DownloadQueueFeature.cs` | 1 | barrier rewrite + doc |
| `tests/Module.Mangareader.Downloader.Tests/DownloadQueuePauseDrainTests.cs` | 1 | update + 1–2 new tests |
| `tests/Citadel.Uia/ModuleRuntimeHostTests.cs` (or CoordinatorTests) | 2 | +1 Force Stop test; clarify old name/comment |
| `module/mangareader/Features/Downloader/DownloaderBackgroundService.cs` | 3 | doc only |
| `module/mangareader/Features/Downloader/Catalog/DownloaderOnlineProcess.cs` | 3 | doc only |
| `docs/work/module-runtime-start-stop/PLAN.md` | 3 | status line |
| `module/yuzvid/Features/Browser/YuzvidBrowserView.xaml.cs` | 5/F | dispose main WebView2 + 0x8007139F retry |
| `module/yuzvid/Features/Browser/YuzvidBrowserController.cs` | 5/F | Dispose → full view Shutdown |
| `docs/work/module-runtime-start-stop/FIX-PLAN.md` | all | status + smoke results |
| *(no production Shell/Coordinator/Router edits)* | — | — |

---

## Risk register

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Other tests assume Failed→Paused after drain | Low | Grep before Phase 1 exit; fix only barrier tests |
| User-facing Stop tests break if StopMany is touched | — | **Do not touch StopMany** for A |
| ForceStop LastError not cleared → new test flakes on assert | Medium | Assert actual slot behavior; adjust expected clear/not-clear |
| Flaky downloader scheduler test | Known | Isolated re-run; not in this change set |
| Manual smoke blocked by file locks / running shell | Low | Close Shell before Release; smoke on fresh process |
| yuzskill missing skill files block work | Certain | Documented in Phase 0; proceed without those acks |

---

## Definition of done

- [x] A: barrier preserves `Completed`, `Failed`, `AwaitingSourceFallback`; runnable still parks; tests updated
- [x] D: Force Stop from `Running`+`LastError` covered by a green test
- [x] B: no false “Application-lifetime” owners under mangareader Downloader
- [x] C: PLAN status = implemented; this FIX-PLAN status tracks A–E
- [x] Phase 4: real preflight green (Release test 1049/1049 + 5 citizens) + `bump-build-run.bat` → 3.0.25 Setup.exe (Finding F fix rebuilt as **3.0.26**)
- [x] Finding F code fix: main WebView2 disposed on Stop + 0x8007139F retry; Module.Yuzvid 0E; yuzvid arch 3/3; rebuilt 3.0.26
- [x] Finding F re-smoke: **PASS (SOLID)** — 4/4 Start→Stop→Start cycles `ready`, 0× `0x8007139F` on 3.0.26 (popup-trace 19:56:59–19:59:35)
- [~] Phase 5: E1 PASS (cold-boot log); E2/E8 partial; E3–E7, E9–E12 **UNVERIFIABLE from logs** (coordinator state not logged; E5 queue empty; E10–E12 removal path not exercised); E13 not run. **Finding E (Gate 7 live smoke) remains open** until operator click-through seeds E5 jobs and exercises Force Stop + Removal pending — or Coordinator gains structured state logging.

---

## Suggested commit split (if/when committing)

1. `fix(mangareader): PauseAndDrain preserves Failed and decision states` (A)
2. `test(shell): Force Stop from Running with stop error` (D)
3. `docs: runtime-lifetime wording + plan status + fix plan` (B, C, E checklist)

No commit until explicitly requested.
