# CamoProf Implementation Plan

## Goal
Implement the CamoProf citizen screen for Citadel that enables profile management via camoufox browser profiles, with a C#↔Python seamless seam via pyhost protocol v1. This serves as the first citizen proving the modular architecture, with FTF and other citizens to follow.

## Architecture
CamoProf composes existing `setting/Components` only, carries no `module.json` (searcher skips it, Folder Law 4), and deploys payload via `Citizen.targets` `DeploySharedLogic` target to `AppContext.BaseDirectory\sharedLogic\` (sibling of `module\`, never inside it). Python logic lives in `module/sharedLogic/` (generic, no `module.json`). Data vault at `module/credenz/`. Citadel stays fully independent from YUZZENI (copy-only, zero back-references). C# hosts the only seam: `pyhost` stdio NDJSON.

## Tech Stack
- C#/.NET 10 WPF (Citadel shell)
- Python 3.12
- camoufox 0.5.5
- playwright >=1.51,<1.52

## 1. Phase 0 — Foundation ( already done )
- [x] `module/credenz/` — README + `google/profiles/.gitkeep`; `.gitignore` armor (level‑by‑level negations)
- [x] `module/sharedLogic/` — `README.md`, `requirements.txt` (camoufox 0.5.5, playwright>=1.51,<1.52), `pyhost/`, `reference/`, `cs/`
- [x] `pyhost v1` — NDJSON protocol (`ping`, `session.open`, `session.verify`, `session.close`, `shutdown`), `README.md` contract, CITADEL_CREDENZ env, profile name validation, path‑escape guard
- [x] `Citizen.targets` — `DeploySharedLogic` target that syncs (delete‑first, then copy) `requirements.txt` (flat) + `pyhost\**\*.py` to `$(SharedLogicRuntimeRoot)`; no `cs/`, `reference/`, READMEs, venvs, caches deployed
- [x] `.gitignore` — credenz armor, venv, `__pycache__/`, `*.pyc`, `*.pyo`
- [x] `module/camoprof/` — `module.json` (route `camoprof`, order 20), `layout.json`, `CamoprofModule.cs`, `CamoprofView.xaml(.cs)`, `Module.Camoprof.csproj` (imports `..\..\sharedLogic\cs\**\*.cs` one `Compile Include`)

## 2. Phase 1 — CamoProf Screen
### 2.1 RUNTIME section
- Python version detect (≥3.12) / vendored CPython NuGet 3.12.10 fallback
- venv status (created / not created)
- packages status (camoufox + playwright installed / not)
- browser binary cache status (camoufox per‑machine cache hit or miss)
- **[Setup runtime]** button — progress + status line during run
- Status text line beneath buttons

### 2.2 PROFILES section
- **[Refresh]** — filesystem scan of credenz root, repopulate list
- **ListBox** (screen‑local, themed): name | last modified | size
- **Field**: [ new profile name ] — input validated against `^[A-Za-z0-9._-]+$`, `.` and `..` rejected
- **Add & login** — validate name → spawn pyhost → `session.open` (headed) → status line guides user
- **Launch** (on selected row) → `session.open` with that profile
- **Verify login** → `session.verify` → green/red status; on alive, `_session_state.json` written by Python, `state_saved` reports success (artifact, never read back); if browser closed → `BROWSER_GONE`, resync list
- **Delete** (selected row) — confirm dialog → if session open → `session.close` first → validate resolved absolute path stays inside profiles root (post‑`GetFullPath`, traversal rejected) → recursive delete → refresh list
- Status line: *"log in inside the browser window, then press Verify login"*

### 2.3 Behavior
- **List/Delete/Refresh**: pure C# — filesystem scan of credenz profiles root. `SettingTable` has no selection by design and stays untouched — one screen's need is not a shared component (playbook: extracting from one example is guessing). When `ftf` repeats the selectable‑table pattern, extract to `setting/Components` then.
- **Delete flow**: confirm dialog → if a pyhost session is open on the profile, `session.close` first → validate the resolved absolute path stays inside the resolved profiles root (post‑`GetFullPath`, reject traversal) → recursive delete → refresh the list. Python is only invoked when a browser must live.
- **Add & login / Launch**: validate name → spawn pyhost → `session.open` (headed). Status line guides the user.
- **Verify login**: `session.verify` → green/red status; on alive, session state file is written by Python (§3).
- **Lifetime**: view lifetime owns pyhost; navigating away closes the browsers. Documented on the status line.

## 3. Phase 2 — Verification Gates (all in §8 of PLAN)
- **G0**: manual (PowerShell): `'{"id":1,"cmd":"ping"}' | & <runtime>\.venv\Scripts\python.exe <deployed>\sharedLogic\pyhost\pyhost.py` → one JSON line, `ok:true`
- **G1**: screen: run Setup on dev machine → all four runtime rows ✓
- **G1b**: build: `dotnet build module/camoprof/Module.Camoprof.csproj` → deployed `sharedLogic\pyhost\pyhost.py` + `sharedLogic\requirements.txt` exist beside shell output (sibling of `module\`)
- **G2**: screen: Add & login `test-main` → manual login → Verify | `alive:true` IS the gate; `state_saved` diagnostic (contract — the session JSON is an artifact, never read back)
- **G3**: screen: Launch `test-main` → Verify | alive immediately (trust persisted)
- **G4**: screen: Delete `test-main` (confirm) | folder gone; table refreshes
- **G5**: shell: Settings → PROBLEMS | empty; screen hot‑loads/unloads clean

## 4. Runtime Bootstrap (Hybrid Rule — L6)
### 4.1 Detect
- `py -3 --version`, `python --version`, `python3 --version`;
- parse output; reject Microsoft Store alias (`path under `WindowsApps` or store‑launch behavior). Accept ≥3.12.
### 4.2 If none
- Vendor the official CPython **NuGet package 3.12.10** (pinned URL + SHA‑256 verified before extraction) into the runtime root. No embeddable‑zip/get‑pip path in v1. Progress streamed.
### 4.3 Create venv
- `python -m venv <runtimeRoot>/.venv`.
### 4.4 Deps
- `.venv/Scripts/python -m pip install -r requirements.txt`. Progress streamed.
### 4.5 Browser
- `.venv/Scripts/python -m camoufox fetch` — skipped when the per‑machine cache already exists (dev machine cache hit, no 100 MB re‑download). Progress streamed.
### 4.6 Verify
- Spawn pyhost, `ping` must answer.

## 5. NuGet Pinning
- Pinned URL: `https://www.nuget.org/api/v2/package/python/3.12.10`
- Pinned SHA‑256: `0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8`
- Verified after download, before extraction. Never executes an unverified payload.

## 6. Cross‑Process Setup Lock
- Static `SemaphoreSlim` replaced by a lock file at `<runtimeRoot>\.setup.lock` opened `FileShare.None`; second caller (same or other process, other citizen assembly) gets `IOException` → "another setup is already running". The OS releases it if the holder dies. PID+timestamp written into the lock for diagnosis.

## 7. Deviation — Verify Heuristic (recorded, deliberate)
Baseline verify heuristic ("not on signin path" = alive) **false‑positives**: Google lands signed‑out sessions on the public `google.com/account/about/` page. Observed live during the smoke. pyhost now uses the strict rule — alive ⇔ final URL on `myaccount.google.com`. Recorded in `pyhost/README.md`, plan Appendix B, and a code comment.

## 8. Explicit Non‑Goals This Phase
- `module/ftf` (engine copy, pipeline streaming, results) — phase 2, on camoprof's base patterns.
- `module/proxy` (pure C# HTTP to gateway) — independent, anytime.
- stealthB + engine copies — arrive with FTF.
- pyhost v2 scraping commands for the manga downloader — added when codex states concrete needs; v1 README still unblocks its design.
- **Installer/release** packaging of the python payload (`Build‑Release.ps1`, GitHub release assets), `module.json` runtime declaration for searcher validation — release phase. **Dev‑output deployment IS in Phase 0 scope** (the §6.2 mirror): without it nothing runs in dev.

## 9. Risks & Tradeoffs
- **Embeddable Python pip bootstrap is quirky** (no pip, `._pth` dance). Mitigation: hybrid rule — embeddable only when no system Python; get‑pip scripted once, tested as part of G1 on a clean VM later.
- **Microsoft Store `python` alias** false‑positive — handled by parsing version output + `WindowsApps` rejection (L6).
- **`camoufox fetch` is a ~100MB download** on machines without the cache — progress bar mandatory; no resume (documented).
- **Library prints polluting stdout** would corrupt NDJSON — pyhost redirects child logging to stderr; gate G0 catches regressions.
- **Navigating away kills open browsers** — accepted, documented on screen.
- **Windows path length**: profile names validated short/safe (§6.3).

## 10. Verification (run and report actual evidence)
1. `git diff --check`.
2. Existing full Citadel test suite.
3. Build `Module.Camoprof` directly.
4. Confirm deployed files exist at:
   `<shell output>\sharedLogic\requirements.txt`
   `<shell output>\sharedLogic\pyhost\pyhost.py`
5. Run the corrected G0 NDJSON ping.
6. Run Runtime Setup and verify all four runtime stages.
7. Open Citadel and visually verify:
   - CamoProf appears as a citizen.
   - screen loads without Settings → PROBLEMS entries.
   - profile ListBox selection and buttons behave correctly.
   - navigating away shuts down its host cleanly.
8. Exercise a temporary profile without claiming Google login success. Manual account login gates G2/G3 must be left for the user if credentials are required.
9. Confirm existing release counting is not disturbed by sharedLogic deployment.

## 11. Changelog (H002 + H003)
- 2026‑08‑31 — codex cross‑review pass 1: sharedLogic deployment mirror (§5/§6.2/§8), credenz per‑mode resolution (§6.1), python vendor mechanism nuget‑first (§6.4), runtime root moved to `%LocalAppData%` in all modes (§6.4 — refines L5's *location*, the rule is unchanged), screen‑local selectable list instead of touching SettingTable (§7), delete lifecycle + pyhost hardening (§6.3/§7), RuntimeSetup confirmed shared (§6.4), persistence note (§9), dev‑deploy vs installer split (§10).
- 2026‑08‑31 — codex cross‑review pass 2 + LO approval: plan status → APPROVED. L3/architecture per‑mode credenz wording; L6/§6.4 nuget‑only (embeddable path removed, SHA‑256 pinned pre‑extraction); deploy mirror moved OUT of `module\` to sibling `AppContext.BaseDirectory\sharedLogic` (release‑count invariant at Build‑Release.ps1:89‑92 stays intact, zero tools/ change); G0 corrected to valid PowerShell; §9 embeddable risk replaced by hash‑verification risk.

---
*Plan approved for implementation on 2026‑08‑31. No core/, setting/, release tooling, or approved architecture changes were made. Worktree carries only the files listed above plus the user‑owned `module/mangareader/TASKLIST.md` modification (untouched).*