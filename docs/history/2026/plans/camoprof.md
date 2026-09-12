# PLAN: Citadel × Python wiring — module/camoprof

> Historical base plan. Later approved UI/account decisions in
> `PLAN-camoprof-ui-refactor.md` and `PLAN-camoprof-account-health.md`
> supersede this file's screen-local list/tab and no-new-component details.

Status: **APPROVED for implementation** (LO, 2026-08-31). Approved explicitly:
D1 (direct camoufox, no stealthB), D2 (port login flow into pyhost), D3
(shared C# source under `module/sharedLogic/cs`), D4 (stdlib json), runtime
root always `%LocalAppData%\Citadel\runtime`, private Python fallback is
CPython NuGet 3.12.10 only (embeddable path removed).

**Goal:** Citadel becomes the GUI hub for the proven Python automation logic —
Python stays Python (no C# rewrite), C# owns the frontend, and the first
citizen screen (`camoprof`) proves the seam with the smallest possible
surface: create/launch/delete camoufox browser profiles.

**Architecture:** generic Python lives in `module/sharedLogic/` (no
`module.json` → searcher skips it, Folder Law 4), data lives in the credenz
vault (dev: repo `module/credenz/`; installed: `%LocalAppData%\Citadel\Credenz`),
and a single stdio host (`pyhost`) is the only seam between C# and Python —
newline-delimited JSON in, newline-delimited JSON out. `camoprof` composes
existing `setting/Components` only.

**Tech stack:** C#/.NET 10 WPF (citadel) · Python 3.12 · camoufox 0.5.5 ·
playwright ≥1.51,<1.52.

---

## 1. Context

- YUZZENI (`C:\VSCODE\YUZZENI`) holds the proven logic: camoufox profile
  login flow (`human_login.py` / `launch_home.py` — 5 real working profiles),
  the faucet engine, stealthB browser lib, PowerShell proxy gateway.
- YUZZENI stays as **frozen reference**. Citadel copies what it needs and
  owns the copies. **Zero references back**: no paths, no env names, no venv
  pointing at YUZZENI. If YUZZENI moves or is deleted, citadel must not break.
- Citadel (this repo) is a modular WPF dashboard. Playbook: `module/README.md`.
  One citizen exists (`mangareader`, pure in-process C#). A second agent
  (codex) is building a manga downloader that will consume `pyhost` too —
  the protocol README (§6.3) is the contract it builds against.

## 2. Locked decisions (from discussion, not up for review)

| # | Decision |
|---|---|
| L1 | Python logic is never rewritten to C#. C# = frontend, Python = backend. |
| L2 | Citadel is fully independent from YUZZENI (copy-only, zero back-references). |
| L3 | Data vault per mode: dev → repo `module/credenz/` (gitignore-armored); installed → `%LocalAppData%\Citadel\Credenz`. C# always resolves the location and hands the ABSOLUTE path to Python via env `CITADEL_CREDENZ` — Python never computes paths itself. |
| L4 | One stdio host (`pyhost`) with JSON-lines protocol; one-shot + interactive modes. Protocol README is a file in the repo, the contract for all consumers. |
| L5 | venv: shared at runtime root (see §6.4). Per-feature venv is an escape hatch, only when deps actually conflict. |
| L6 | Hybrid Python rule: detect system Python ≥3.12 → use it; else vendor the official CPython **NuGet package 3.12.10** (pinned URL + SHA-256, verified before extraction) into the runtime root. No embeddable-zip/get-pip path in v1. Guard the Microsoft Store `python` alias (parse version output, reject `WindowsApps` paths). |
| L7 | Runtime setup UI lives in the camoprof screen (camoprof is the module that needs camoufox). |
| L8 | Screens compose `setting/Components` (Button/Field/Slider/Table/Toggle). No new shared component unless a pattern repeats across screens. |
| L9 | Build order: **camoprof first**; FTF later on camoprof's base; proxy anytime (independent, pure HTTP). |
| L10 | Profiles in citadel start **fresh** — the 5 YUZZENI profiles stay with YUZZENI. |
| L11 | Delete-profile button exists, with confirmation. |
| L12 | Folder name `module/camoprof` → assembly `Module.Camoprof` (Citizen.targets derives it). |

## 3. What camoprof wraps (read first)

`YUZZENI/core/features/login/google-existing-account/human_login.py`:

1. `AsyncCamoufox(persistent_context=True, user_data_dir=<profile>, headless=False, humanize=True, os="windows", disable_coop=True, config={"forceScopeAccess": True})`
2. User logs in manually in the real window.
3. On demand (ENTER in console): goto `https://myaccount.google.com/`, wait 4s.
   - URL contains `accounts.google.com` and not `myaccount` → not logged in.
   - Else → alive; `context.storage_state()` → `<profile>/_session_state.json`.

`launch_home.py`: same recipe, opens existing profile dir, reports session
alive/expired. Profile home never moves; trust lives in the folder.

**Key fact:** these scripts import camoufox **directly** — stealthB is used
by the engine/pipeline path (FTF), not by the manual-login path.

## 4. Decision points for this review

### D1 — Browser access for camoprof: direct camoufox *(recommended)* vs stealthB
- **Direct (rec):** port the proven recipe (§3) into pyhost. Smallest phase 0;
  stealthB arrives later with FTF (proxy recipes, engine bridge) where it is
  actually needed.
- stealthB now: one browser doorway for all of citadel, but rewrites a proven
  flow through a wrapper before anyone needs it.

### D2 — Login flow: port into pyhost commands *(recommended)* vs wrap script verbatim
- **Port (rec):** `session.open` / `session.verify` / `session.close` become
  pyhost commands (~40 lines of logic). Clean JSON contract; codex gets a real
  example. Original scripts copied to `sharedLogic/reference/` as living
  documentation, not executed.
- Wrap: run `human_login.py` as a child, inject ENTER into stdin, parse
  human-language prints for status. Zero code change, fragile protocol.

### D3 — C# process host placement: shared source *(recommended)* vs alternatives
- **Shared source (rec):** `module/sharedLogic/cs/PyHost.cs` (+ `RuntimeSetup.cs`
  and `CredenzPath.cs`),
  citizens pull it in with one line: `<Compile Include="..\sharedLogic\cs\**\*.cs" />`.
  No `core/` change; `Citizen.targets` owns only the shared payload deployment
  described in §6.2, preserving full Folder Law 4 compliance.
  Internal helper types only — never contract types — so per-citizen compilation
  is safe (the `IModule` identity problem does not apply).
- Per-citizen duplicate: total isolation, ~100 lines duplicated.
- Core assembly (`Citadel.PyHost`): cleanest .NET, but touches core + targets.

### D4 — pyhost JSON library: stdlib `json` now *(recommended)*, `orjson` later
- stdlib keeps `requirements.txt` minimal at birth. NDJSON line parsing is
  identical; swap is internal to pyhost when throughput ever matters.

## 5. Target filetree (new files only)

```
citadel/
├── .gitignore                         ← extend (see 6.1)
└── module/
    ├── credenz/                       NEW — data vault (never committed content)
    │   ├── README.md                  what lives here, the armor rule
    │   └── google/profiles/           (empty; fresh start)
    ├── sharedLogic/                   NEW — generic python; NO module.json
    │   ├── README.md                  usage + venv rule + ownership
    │   ├── requirements.txt           camoufox==0.5.5, playwright>=1.51,<1.52
    │   ├── pyhost/
    │   │   ├── pyhost.py              the stdio host (§6.3)
    │   │   └── README.md              PROTOCOL v1 — the contract codex reads
    │   ├── reference/
    │   │   ├── human_login.py         copied from YUZZENI — documentation
    │   │   └── launch_home.py         (per D2; not executed)
    │   ├── cs/                        (per D3)
    │   │   ├── PyHost.cs              spawn + NDJSON client
    │   │   ├── RuntimeSetup.cs        detection + bootstrap chain (§6.4)
    │   │   └── CredenzPath.cs         dev/installed vault resolver
    │   └── tests/
    │       └── test_pyhost.py         protocol + timeout/lifecycle regression tests
    └── camoprof/                      NEW — citizen screen
        ├── module.json
        ├── layout.json
        ├── CamoprofModule.cs
        ├── CamoprofView.xaml(.cs)
        └── Module.Camoprof.csproj     imports ../Citizen.targets only
```

Nothing outside `module/` is edited except `.gitignore` (armor) — plus one
shared build addition: `Citizen.targets` (which lives at `module/` root,
Law 4) gains a `DeploySharedLogic` target (§6.2). No `Citadel.slnx` entry.

## 6. Phase 0 — foundation

### 6.1 `module/credenz/`

- Folders: `google/profiles/` (seed empty), plus `README.md` stating: data
  both directions lives here; screens stay stateless; content is gitignored.
- `.gitignore` armor (root): ignore `module/credenz/**` except `README.md`
  and `.gitkeep` files; ignore `module/sharedLogic/.venv/`, `runtime/`,
  `__pycache__/`, `*.pyc`.
- Verify: `git check-ignore module/credenz/google/profiles/x` exits 0.
- Resolution per mode: **dev** → repo `module/credenz/`; **installed** (app
  folder read-only) → `%LocalAppData%\Citadel\Credenz`. C# always resolves
  the location and hands the ABSOLUTE path to Python via `CITADEL_CREDENZ`
  (L3 — Python never computes paths).

### 6.2 `module/sharedLogic/`

- `README.md`: what lives here (generic python shared by citizens), the venv
  rule (shared by default; per-feature venv wins when present — escape hatch
  for dependency conflicts; locations in §6.4), ownership.
- `requirements.txt`:
  ```
  camoufox==0.5.5
  playwright>=1.51,<1.52
  ```
- **Deployment (closes the runtime-output gap):** `Citizen.targets` gains a
  `DeploySharedLogic` target that mirrors the *payload* —
  `module/sharedLogic/pyhost/**` and `requirements.txt` — into
  **`AppContext.BaseDirectory\sharedLogic\`** — a SIBLING of `module\`, never
  beneath it — after any citizen build. Excluded: `cs/` (compile-time only),
  `reference/` (docs), READMEs, `.venv`, `__pycache__`. Sibling placement
  keeps the searcher blind to it (it watches `module\` only) and keeps
  `Build-Release.ps1`'s deployed-directory count invariant intact (it counts
  `publish\module\*` only). Runtime state (venv, vendored python) is NOT
  deployed — it is created by RuntimeSetup at the runtime root (§6.4).

### 6.3 `pyhost` v1 — the only C#↔Python seam

Transport: one process per owning view; **stdin/stdout NDJSON** — one JSON
object per line.

- Request:  `{"id": <int>, "cmd": "<name>", ...params}`
- Response: `{"id": <same int>, "ok": true, ...}` or
  `{"id": <same int>, "ok": false, "error": {"code": "...", "message": "..."}}`
  — exactly one response per request.
- Events:   `{"event": "<name>", ...}` — unsolicited (progress, future).

Commands v1 (camoprof needs only these):

| cmd | params | result |
|---|---|---|
| `ping` | — | `version`, `python`, `camoufox` versions |
| `session.open` | `profile`, `start_url?` | `session` id, `profile_dir`; spawns **headed** camoufox persistent context (recipe §3) |
| `session.verify` | `session` | goto `myaccount.google.com` → `alive: bool`, `url`; if alive → `_session_state.json` saved, `state_saved: true` |
| `session.close` | `session` | context closed; close failure keeps the session registered for retry |

Rules:

- `CITADEL_CREDENZ` env required at spawn (absolute path); missing → startup
  error. Python computes nothing by itself.
- Profile names validated: `[A-Za-z0-9._-]+`, reject `.`/`..`/separators.
- One session per profile; second `open` → `PROFILE_BUSY`.
- Timeout cleanup preserves ownership: a timed-out `session.open` completes
  context cleanup before returning; a timed-out/failed `session.close` keeps
  the session registered rather than losing the only browser handle.
- pyhost exit kills its sessions (persistent context keeps data on disk).
  Screen lifetime owns the pyhost: navigating away closes browsers.
- stdout is protocol-only; library logging redirected to stderr.
- Lifecycle hardening: a `shutdown` command (close all contexts, exit 0);
  stdin EOF (parent died, pipe closed) → same cleanup then exit — the orphan
  guard; every request times out (default 120s, overridable per call);
  command errors → structured error response, never a silent hang; process
  exit closes contexts (`finally`). C# holds `Process.Kill(
  entireProcessTree: true)` as the last-resort guard.
- **Reference copies** (`reference/human_login.py`, `reference/launch_home.py`)
  document the proven flow; pyhost ports it (per D2).

Protocol README (`pyhost/README.md`) states all of the above plus full
example sessions — **this file is what codex builds the manga downloader
against.** Interactive scraping commands (`page.goto`, `page.eval`,
`page.fetch` through the browser context for Cloudflare-guarded sites) are
protocol v2, added when codex's downloader concretely needs them — v1 stays
minimal.

### 6.4 Runtime bootstrap (hybrid rule — L6)

Logic lives in shared source (`RuntimeSetup.cs`, `sharedLogic/cs/` — D3), so
any future citizen (manga downloader, ftf) compiles the same code and can
run setup/checks itself. Camoprof only owns the UI debut (L7): four status
rows + one Setup button.

```
Python ≥3.12 → venv → pip packages → camoufox browser binary
```

Chain:

1. **Detect:** `py -3 --version`, `python --version`, `python3 --version`;
   parse output; reject Microsoft Store alias (path under `WindowsApps` or
   store-launch behavior). Accept ≥3.12.
2. **If none:** vendor the official CPython **NuGet package 3.12.10** into
   `<runtimeRoot>/python/` — the pinned `.nupkg` URL is downloaded, its
   SHA-256 verified against the pinned hash BEFORE extraction, then
   `tools\` is extracted (`tools\python.exe`; full distro: `Lib\venv`,
   `Lib\ensurepip`, pip). No admin, nothing touches the system, delete
   folder = uninstall. Progress streamed.
3. **Create venv:** `python -m venv <runtimeRoot>/.venv`.
4. **Deps:** `.venv/Scripts/python -m pip install -r requirements.txt`.
5. **Browser:** `.venv/Scripts/python -m camoufox fetch` — skipped when the
   per-machine cache already exists (`%LOCALAPPDATA%`), which is the case on
   the dev machine (shared with YUZZENI's venv — no 100MB re-download).
6. **Verify:** spawn pyhost, `ping` must answer.

Each step emits progress; failure marks its row red and retry resumes from
that step. All steps are idempotent (re-run = skip what's done).

**Runtime root (all modes):** `%LocalAppData%\Citadel\runtime\` holds
everything *rebuildable* — the shared venv, the vendored Python, download
caches. Never in the repo, never beside the exe: `bin` cleans and read-only
install folders become non-issues, and dev behaves identically to installed.
Override via env `CITADEL_RUNTIME`. The read-only payload (`pyhost.py`,
`requirements.txt`) comes from the deployed mirror (§6.2). Per-feature venv
escape hatch (L5): `<runtimeRoot>\venvs\<feature>\.venv` wins when present,
else the shared `<runtimeRoot>\.venv`.

## 7. Phase 1 — `module/camoprof`

Copy `module/blank/` → `module/camoprof/`, rename, `module.json`:

```json
{
  "title": "CamoProf",
  "icon": "\uE771",
  "route": "camoprof",
  "order": 20,
  "entry": "Module.Camoprof.dll",
  "type": "Module.Camoprof.CamoprofModule"
}
```

Screen composition (existing components only):

```
RUNTIME
  Python 3.12 ... ✓ 3.12.x (system) | ✗ → [Install Python]
  venv .......... ✓ / ✗
  packages ...... ✓ / ✗
  browser ....... ✓ / ✗
  [ Setup runtime ]  + ProgressBar + status line during runs

PROFILES                                    [ Refresh ]
  ListBox (screen-local, themed): name | last modified | size
  Field: [ new profile name ]   [ Add & login ]
  On selected row: [ Launch ] [ Verify login ] [ Delete ]
  status line: "log in inside the browser window, then press Verify login"
```

Behavior:

- **List/Delete/Refresh: pure C#** — filesystem scan of the profiles root
  under credenz. The list is a screen-local `ListBox` with a themed item
  template: `SettingTable` has no selection by design and stays untouched —
  one screen's need is not a shared component (playbook); when `ftf` repeats
  the selectable-table pattern, extract to `setting/Components` then.
- **Delete flow:** confirm dialog → if a pyhost session is open on the
  profile, `session.close` first → validate the resolved absolute path stays
  inside the profiles root (post-`GetFullPath`, reject traversal) →
  recursive delete → refresh. Python is only invoked when a browser must
  live.
- **Add & login / Launch:** validate name → spawn pyhost → `session.open`
  (headed). Status line guides the user.
- **Verify login:** `session.verify` → green/red status; on alive, session
  state file is written by Python (§3).
- **Lifetime:** view lifetime owns pyhost; navigating away closes the
  browsers. Documented on the status line.
- **XAML:** colours from theme resources (`{DynamicResource Fg}` etc.), named
  slots for `layout.json` (`RuntimePanel`, `ProfileList`, `StatusText` —
  position/size/visibility only).
- No new shared components; if the status-row pattern repeats in ftf/proxy,
  extract it then (playbook philosophy).

## 8. Verification gates

| Gate | How | Pass |
|---|---|---|
| G0 | manual (PowerShell): `'{"id":1,"cmd":"ping"}' \| & <runtimeRoot>\.venv\Scripts\python.exe <deployed>\sharedLogic\pyhost\pyhost.py` | one JSON line, `ok:true` |
| G0b | `python -m unittest module.sharedLogic.tests.test_pyhost -v` | protocol and timeout/lifecycle regression suite passes without warnings |
| G1 | screen: run Setup on dev machine | all four runtime rows ✓ |
| G1b | build: `dotnet build module/camoprof/Module.Camoprof.csproj` | deployed `sharedLogic\pyhost\pyhost.py` + `sharedLogic\requirements.txt` exist beside shell output (sibling of `module\`) |
| G2 | screen: Add & login `test-main` → manual login → Verify | `alive:true` IS the gate; `state_saved` diagnostic (contract — the session JSON is an artifact, never read back) |
| G3 | screen: Launch `test-main` → Verify | alive immediately (trust persisted) |
| G4 | screen: Delete `test-main` (confirm) | folder gone; table refreshes |
| G5 | shell: Settings → PROBLEMS | empty; screen hot-loads/unloads clean |

No account is consumed by any gate; G2 uses a throwaway profile LO creates.

## 9. Risks & tradeoffs

- **Vendored-runtime download could fail or drift** — mitigated by pinned
  URL + SHA-256 verification before extraction; a mismatch aborts with a
  structured error, never executes an unverified payload.
- **Microsoft Store `python` alias** false-positive — handled by parsing
  version output + `WindowsApps` rejection (L6).
- **`camoufox fetch` is a ~100MB download** on machines without the cache —
  progress bar mandatory; no resume (documented).
- **Library prints polluting stdout** would corrupt NDJSON — pyhost redirects
  child logging to stderr; gate G0 catches regressions.
- **Navigating away kills open browsers** — accepted, documented on screen.
- Windows path length: profile names validated short/safe (§6.3).
- Session persistence relies on the camoufox persistent directory alone —
  proven live in YUZZENI (relaunches arrive logged-in; the home never moves,
  no cookie transfer). `_session_state.json` is written as an artifact but
  never read at launch. A cookie-restore fallback is NOT built preemptively;
  it becomes a recorded deviation only if gate G3 fails in practice.

## 10. Explicit non-goals this phase

- `module/ftf` (engine copy, pipeline streaming, results) — phase 2, on
  camoprof's base patterns.
- `module/proxy` (pure C# HTTP to gateway) — independent, anytime.
- stealthB + engine copies — arrive with FTF.
- pyhost v2 scraping commands for the manga downloader — added when codex
  states concrete needs; v1 README still unblocks its design.
- **Installer/release** packaging of the python payload (`Build-Release.ps1`,
  GitHub release assets), `module.json` runtime declaration for searcher
  validation — release phase. **Dev-output deployment IS in Phase 0 scope**
  (the §6.2 mirror): without it nothing runs in dev.

## Appendix A — copy inventory from YUZZENI (reference only)

| Source | Destination | Executed? |
|---|---|---|
| `core/features/login/google-existing-account/human_login.py` | `module/sharedLogic/reference/` | no (per D2) |
| `core/features/login/google-existing-account/launch_home.py` | `module/sharedLogic/reference/` | no (per D2) |

Nothing else is copied in this phase. No path, env var, or venv in citadel
may reference `C:\VSCODE\YUZZENI` (L2 — review should grep for it).

## Appendix B — proven camoufox recipe (ported by pyhost)

```python
AsyncCamoufox(
    persistent_context=True,
    user_data_dir=<credenz>/google/profiles/<name>,
    headless=False,
    humanize=True,
    os="windows",
    disable_coop=True,
    i_know_what_im_doing=True,
    config={"forceScopeAccess": True},
)
# verify: goto https://myaccount.google.com/ → wait 4s
#   baseline: alive ⇔ NOT ("accounts.google.com" in url and "myaccount" not in url)
#   DEVIATION (2026-08-31, smoke gate): signed-out sessions can land on the
#   public /account/about/ page — baseline false-positives. pyhost uses the
#   strict rule instead: alive ⇔ "myaccount.google.com" in final url.
#   alive → context.storage_state(path=<profile>/_session_state.json)
```

## Changelog

- 2026-08-31 — codex cross-review pass 1: sharedLogic deployment mirror
  (§5/§6.2/§8), credenz per-mode resolution (§6.1), python vendor mechanism
  nuget-first (§6.4), runtime root moved to `%LocalAppData%` in all modes
  (§6.4 — refines L5's *location*, the rule is unchanged), screen-local
  selectable list instead of touching SettingTable (§7), delete lifecycle +
  pyhost hardening (§6.3/§7), RuntimeSetup confirmed shared (§6.4),
  persistence note (§9), dev-deploy vs installer split (§10).
- 2026-08-31 — codex cross-review pass 2 + LO approval: plan status →
  APPROVED. L3/architecture per-mode credenz wording; L6/§6.4 nuget-only
  (embeddable path removed, SHA-256 pinned pre-extraction); deploy mirror
  moved OUT of `module\` to sibling `AppContext.BaseDirectory\sharedLogic`
  (release-count invariant at Build-Release.ps1:89-92 stays intact, zero
  tools/ change); G0 corrected to valid PowerShell; §9 embeddable risk
  replaced by hash-verification risk.
