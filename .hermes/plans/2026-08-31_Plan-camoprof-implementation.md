# CamoProf Implementation Plan

> **Sumber rujukan:** Semua diverifikasi langsung dari repositori sumber. Tidak ada referensi spekulatif.
>
> | Singkatan | Sumber | Lokasi |
> |---|---|---|
> | **YUZZENI** | Monorepo YUZZENI (frozen reference) | `C:\VSCODE\YUZZENI\` |
> | **PLAN** | CamoProf plan utama | `.docs/PLAN-camoprof.md` |
> | **bridge** | Hermes↔Codex mailbox | `.agents/bridge/hermes-codex.md` |
> | **playbook** | Citadel module playbook | `module/README.md` |

---

## Goal

Implement the CamoProf citizen screen for Citadel that enables profile management via camoufox browser profiles, with a C#↔Python seamless seam via pyhost protocol v1. This serves as the first citizen proving the modular architecture, with FTF and other citizens to follow.

**Sumber:** Diskusi LO + codex (bridge H001–H002), PLAN §1, §2.

## Architecture

CamoProf is a registered citizen with `module.json`. It composes existing `setting/Components` and deploys payload via `Citizen.targets` `DeploySharedLogic` target to `AppContext.BaseDirectory\sharedLogic\` (sibling of `module\`, never inside it). Python logic lives in `module/sharedLogic/` (generic, **no `module.json`** — searcher skips it, Folder Law 4). Data vault at `module/credenz/`. Citadel stays fully independent from YUZZENI (copy-only, zero back-references). C# hosts the only seam: `pyhost` stdio NDJSON.

**Sumber:** Diskusi LO (Three-Way Law), PLAN arsitektur, bridge H001 butir 1, playbook Folder Law 4.

## Tech Stack

- C#/.NET 10 WPF (Citadel shell)
- Python 3.12
- camoufox 0.5.5 (pinned by Citadel `requirements.txt`)
- playwright >=1.51,<1.52

**Sumber:** Citadel `module/sharedLogic/requirements.txt` (verified 2026-09-01). YUZZENI does not pin camoufox in top-level requirements; version comes from verified environment.

---

## 1. Phase 0 — Foundation (implemented, verified 2026-09-01)

- [x] `module/credenz/` — README + `google/profiles/.gitkeep`; `.gitignore` armor (level‑by‑level negations)
- [x] `module/sharedLogic/` — `README.md`, `requirements.txt` (camoufox==0.5.5, playwright>=1.51,<1.52), `pyhost/`, `reference/`, `cs/`, `tests/`
- [x] `pyhost v1` — NDJSON protocol (`ping`, `session.open`, `session.verify`, `session.close`, `session.navigate`, `google.inspect`, `google.relogin`, `shutdown`), `README.md` contract, CITADEL_CREDENZ env, profile name validation, path‑escape guard
- [x] `Citizen.targets` — `DeploySharedLogic` target that syncs (delete‑first, then copy) `requirements.txt` (flat) + `pyhost\**\*.py` to `$(SharedLogicRuntimeRoot)`; no `cs/`, `reference/`, READMEs, venvs, caches deployed
- [x] `.gitignore` — credenz armor, venv, `__pycache__/`, `*.pyc`, `*.pyo`
- [x] `module/camoprof/` — **`module.json`** (route `camoprof`, order 20), `layout.json`, `CamoprofModule.cs`, `CamoprofView.xaml(.cs)`, `Module.Camoprof.csproj` (imports `..\sharedLogic\cs\**\*.cs` one `Compile Include`)

**Sumber:** PLAN §5, §6.1–6.3, §6.4. Implementasi diverifikasi 2026-09-01: `dotnet build Module.Camoprof` → 0w/0e, payload terdeploy di sibling `sharedLogic\`. Confirmed: `module.json` exists in `module/camoprof/`; `module/sharedLogic/` has NO `module.json`.

## 2. Phase 1 — CamoProf Screen (superseded by UI refactor, see Current-state reconciliation below)

### 2.1 RUNTIME section

- Python version detect (≥3.12) / vendored CPython NuGet 3.12.10 fallback
- venv status (created / not created)
- packages status (camoufox + playwright installed / not)
- browser binary cache status (camoufox per‑machine cache hit or miss)
- **[Setup runtime]** button — progress + status line during run
- Status text line beneath buttons

### 2.2 PROFILES section (HISTORICAL — superseded by SettingTable-based Launcher)

- **ListBox** (screen‑local, themed): name | last modified | size — **SUPERSEDED by `SettingTable` with proportional columns, virtualization, and shared chrome (see `PLAN-camoprof-ui-refactor.md`, implemented 2026-09-01)**
- Profile creation, launch, verify, delete — now via `Launcher/LauncherView.xaml(.cs)`, `AccountSetupDialog`, `GoogleAccountService`, `BrowserSessionCoordinator`, and `NetworkMonitor`

**Sumber:** PLAN §7 (historical). Current UI: `PLAN-camoprof-ui-refactor.md` + `PLAN-camoprof-account-health.md` + `SHARED-UI-BEHAVIOR.md` (all implemented 2026-09-01).

## 3. Runtime Bootstrap (Hybrid Rule — L6)

### 3.1 Detect

- `py -3 --version`, `python --version`, `python3 --version`;
- parse output; reject Microsoft Store alias (path under `WindowsApps` or store‑launch behavior). Accept ≥3.12.

### 3.2 If none

- Vendor the official CPython **NuGet package 3.12.10** (pinned URL + SHA‑256 verified before extraction) into the runtime root. **No embeddable‑zip/get‑pip path** — runtime is NuGet-only. Progress streamed.

### 3.3 Create venv

- `python -m venv <runtimeRoot>/.venv`.

### 3.4 Deps

- `.venv/Scripts/python -m pip install -r requirements.txt`. Progress streamed.

### 3.5 Browser

- `.venv/Scripts/python -m camoufox fetch` — skipped when the per‑machine cache already exists (dev machine cache hit, no 100 MB re‑download). Progress streamed.

### 3.6 Verify

- Spawn pyhost, `ping` must answer.

**Sumber:** PLAN §6.4. NuGet pin diverifikasi: download `python/3.12.10` dari nuget.org, SHA-256 `0eb85c2d...` dihitung langsung, isi dicek (tools/python.exe, Lib/venv, ensurepip, pip). Embeddable path removed per H002 decision.

## 4. NuGet Pinning

- Pinned URL: `https://www.nuget.org/api/v2/package/python/3.12.10`
- Pinned SHA‑256: `0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8`
- Verified after download, before extraction. Never executes an unverified payload.

**Sumber:** Verifikasi langsung: download nuget package, hitung SHA-256, ekstrak dan cek isi (tools/python.exe, Lib/venv, ensurepip, pip).

## 5. Cross‑Process Setup Lock

- Static `SemaphoreSlim` replaced by a lock file at `<runtimeRoot>\.setup.lock` opened `FileShare.None`; second caller (same or other process, other citizen assembly) gets `IOException` → "another setup is already running". The OS releases it if the holder dies. PID+timestamp written into the lock for diagnosis.

**Sumber:** Codex audit H004 butir 6 — rekomendasi cross-process lock via file system, bukan static SemaphoreSlim. Implementasi di `RuntimeSetup.cs`.

## 6. Deviation — Verify Heuristic (recorded, deliberate)

Baseline verify heuristic ("not on signin path" = alive) **false‑positives**: Google lands signed‑out sessions on the public `google.com/account/about/` page. Observed live during the smoke. pyhost now uses the strict rule — alive ⇔ final URL on `myaccount.google.com`. Recorded in `pyhost/README.md`, plan Appendix B, and a code comment.

**Sumber:** Smoke test live (2026-08-31). Google signed-out session mendarat di `/account/about/`, bukan signin. Dicatat di `pyhost/README.md`, PLAN Appendix B.

## 7. Explicit Non‑Goals This Phase

- `module/ftf` (engine copy, pipeline streaming, results) — phase 2, on camoprof's base patterns.
- `module/proxy` (pure C# HTTP to gateway) — independent, anytime.
- stealthB + engine copies — arrive with FTF.
- pyhost v2 scraping commands for the manga downloader — added when codex states concrete needs; v1 README still unblocks its design.
- **Installer/release** packaging of the python payload (`Build‑Release.ps1`, GitHub release assets), `module.json` runtime declaration for searcher validation — release phase. **Dev‑output deployment IS in Phase 0 scope** (the §6.2 mirror): without it nothing runs in dev.

**Sumber:** PLAN §10, diskusi LO (build order: camoprof dulu, FTF/proxy kemudian).

## 8. Risks & Tradeoffs (updated 2026-09-01)

- ~~**Embeddable Python pip bootstrap is quirky**~~ — REMOVED. Runtime is NuGet CPython 3.12.10 only (H002 decision).
- **Microsoft Store `python` alias** false‑positive — handled by parsing version output + `WindowsApps` rejection (L6).
- **`camoufox fetch` is a ~100MB download** on machines without the cache — progress bar mandatory; no resume (documented).
- **Library prints polluting stdout** would corrupt NDJSON — pyhost redirects child logging to stderr; gate G0 catches regressions.
- **Navigating away kills open browsers** — accepted, documented on screen.
- **Windows path length**: profile names validated short/safe (§6.3).

**Sumber:** PLAN §9, pengalaman smoke test (camoufox fetch 100MB, Microsoft Store alias, stdout polusi). Embeddable risk removed per H002.

## 9. Changelog (H002 + H003 + H004)

- 2026‑08‑31 — codex cross‑review pass 1: sharedLogic deployment mirror (§5/§6.2/§8), credenz per‑mode resolution (§6.1), python vendor mechanism nuget‑first (§6.4), runtime root moved to `%LocalAppData%` in all modes (§6.4 — refines L5's *location*, the rule is unchanged), screen‑local selectable list instead of touching SettingTable (§7), delete lifecycle + pyhost hardening (§6.3/§7), RuntimeSetup confirmed shared (§6.4), persistence note (§9), dev‑deploy vs installer split (§10).
- 2026‑08‑31 — codex cross‑review pass 2 + LO approval: plan status → APPROVED. L3/architecture per‑mode credenz wording; L6/§6.4 nuget‑only (embeddable path removed, SHA‑256 pinned pre‑extraction); deploy mirror moved OUT of `module\` to sibling `AppContext.BaseDirectory\sharedLogic` (release‑count invariant at Build‑Release.ps1:89‑92 stays intact, zero tools/ change); G0 corrected to valid PowerShell; §9 embeddable risk replaced by hash‑verification risk.
- 2026‑08‑31 — H004 audit remediasi: 8 temuan diperbaiki (deadlock, dispose, registry timing, verify split, add flow, cross-process lock, transactional python, state contract). Test 11/11 OK. Deployed payload verifikasi re-synced.

**Sumber:** Bridge H002 (koreksi codex), H003 (hasil implementasi), H004 (audit remediasi).

---

## Current-state reconciliation — 2026-09-01

### Historical state (Phase 0, 2026-08-31)

Phase 0 shipped pyhost v1 with five commands (`ping`, `session.open`, `session.verify`, `session.close`, `shutdown`), screen-local ListBox for profile management, and no shared `SettingTable` integration. Test evidence: 302/302 Core+UI+UIA pass, 11/11 pyhost protocol pass, G0/G1/G1b automated gates pass. **G2/G3 real Google login and G4/G5 visual gates were left for LO** (H003 note). Documented in bridge H003 + H004.

### Current confirmed state (2026-09-01)

Verified from live workspace inspection:

#### UI refactor (implemented)
- **SettingTabs** (`setting/Components/Tabs.xaml(.cs)`) — shared left-aligned rounded tabs with normal/hover/selected/focus states, thin outline in normal state
- **SettingTable** (`setting/Components/Table.xaml(.cs)`) — proportional star columns, virtualization, scrolling, themed headers/cells/selection
- **SettingActionCard**, **SettingTableActions**, **SettingDialog**, **SettingPasswordField** — shared composable UI controls
- CamoProf reorganized into three internal tabs: **Launcher** (daily profile ops), **Editor** (blank, reserved), **Runtime** (Python/venv/packages/browser setup)
- Launcher uses two separate cards: toolbar (Add Profile, headed/headless toggle, network status, Refresh) + profile table (Profile/Google/GitHub/Action columns)
- **Account pairing**: email detected from active Google account (not user-entered label), password via `SettingPasswordField` + DPAPI-backed `GoogleCredentialStore`, generated profile IDs (not arbitrary names)
- Status: **IMPLEMENTED** per `PLAN-camoprof-ui-refactor.md` (approved 2026-09-01), visual gates pass

#### Account health + network guard (implemented)
- **NetworkMonitor** (`module/camoprof/Network/`) — lightweight HTTP/DNS probes, rolling sample window, Stable/Degraded/Offline/Recovering states
- **GoogleAccountService** (`module/camoprof/Providers/Google/`) — guarded relog (one attempt only), email detection, challenge handling, credential rejection without retry loops
- **BrowserSessionCoordinator** (`module/camoprof/sharedLogic/`) — pyhost lifecycle ownership, serialized browser mutations, cleanup ladder
- Check Google button: network preflight → Google reachability → inspect profile → classify (Active/SignedOut/Wrong account/Action required/Offline/Degraded/Provider unavailable)
- Only `SignedOut` + `Stable network` + `Google reachable` can trigger one headed relog attempt
- Status: **IMPLEMENTED** per `PLAN-camoprof-account-health.md` (approved 2026-09-01), automated + visual gates pass (LO-linked-account smoke pending)

#### Pyhost protocol expansion
- **session.navigate** (line 65, `pyhost/README.md`) — navigate existing session to arbitrary URL, wait for load
- **google.inspect** (line 111) — detect active Google email, return account state without modifying session
- **google.relogin** (line 130) — automated email/password fill, stop at challenges, verify resulting email matches stored email, refuse headless
- `session.verify` remains a backward-compatible adapter over `google.inspect`
- Pyhost tests: **23/23 pass** with resource warnings treated as errors (up from 11/11 in H004)
- Citadel test suite: **309/309 pass** (108 Core + 14 UI + 187 UIA, up from 302/302)

#### Live file inventory (2026-09-01)
```
module/camoprof/
├── CamoprofModule.cs, CamoprofView.xaml(.cs)
├── module.json, layout.json, Module.Camoprof.csproj
├── Launcher/
│   ├── LauncherView.xaml(.cs), LauncherProfileRow.cs
│   └── AccountSetupDialog.xaml(.cs)
├── Network/
│   ├── NetworkMonitor.cs, NetworkPolicy.cs, NetworkProbe.cs, NetworkState.cs
├── Providers/Google/
│   ├── GoogleAccountService.cs, GoogleAccountState.cs
│   ├── GoogleAccountRecord.cs, GoogleCredentialStore.cs
├── Runtime/
│   └── RuntimeView.xaml(.cs)
└── sharedLogic/
    ├── BrowserSessionCoordinator.cs, ProfileCatalog.cs, ProfileEntry.cs

module/sharedLogic/ (NO module.json — searcher skips it)
├── cs/
│   ├── PyHost.cs, RuntimeSetup.cs, CredenzPath.cs
├── pyhost/
│   ├── pyhost.py, README.md
├── reference/
│   ├── human_login.py, launch_home.py
├── tests/
│   └── test_pyhost.py
├── requirements.txt, README.md

setting/Components/ (shared UI)
├── Tabs.xaml(.cs), Table.xaml(.cs), ActionCard.xaml(.cs)
├── TableActions.xaml(.cs), Dialog.xaml(.cs), PasswordField.xaml(.cs)
├── Button.xaml(.cs), Field.xaml(.cs), Toggle.xaml(.cs), Slider.xaml(.cs)
```

Verified: `Module.Camoprof.csproj` line 10 contains `<Compile Include="..\sharedLogic\cs\**\*.cs" />` (correct backslash escaping for MSBuild).

### Provisional downloader direction (NOT approved for implementation)

Per `RESEARCH-comix-downloader-2026-09-01.md` (discovery only, read-only probe):
- Plain Chromium/WebView hits repeatable secure-bundle error (blank body, JS exception)
- Direct Camoufox succeeds (full page, API 200, catalog/search/reader/image download/descramble all pass)
- Existing `stealthB` wrapper failed before navigation (`AttributeError: 'AsyncCamoufox' object has no attribute 'new_page'` — lifecycle defect, not Camoufox failure)
- Two-sample CBZ packaging proof (10 pages, integrity pass), descramble algorithm-3 decode pass
- DNS mismatch evidence (system resolver → broken route, Cloudflare/Google DNS → working IPs)

**Deep dive dev-browser relevance** (see §10 below): dev-browser provides lifecycle reference for **named persistent browser registry**, **ensure/relaunch**, **per-browser locking**, **disconnected-browser cleanup**, **idle reaper**, **typed request/response**, **daemon shutdown** — all patterns pyhost v2 downloader would need. Backend must stay **Camoufox**, not Chromium (Comix evidence proves this). QuickJS sandbox, CDP assumptions, Node.js specifics are NOT portable.

**Authorization boundary**: research record does NOT authorize downloader implementation, `stealthB` fixes, or pyhost v2 scraping commands. Manga downloader remains deferred per `module/mangareader/TASKLIST.md` line 144–173 (discovery note, not approved).

---

## 10. Deep dive: dev-browser as lifecycle reference (C:\dev-browser\, commit 73fe10f)

Inspected files: `daemon/src/browser-manager.ts`, `lock.ts`, `execute-request.ts`, `idle-browser-reaper.ts`, `protocol.ts`, `daemon.ts`.

### 10.1 Named persistent browser/profile registry

**Pattern** (browser-manager.ts:72–93, 95–128):
- `Map<string, BrowserEntry>` keyed by browser name (e.g. `"default"`, `"project-alpha"`)
- Each entry carries: `type` (launched/connected), `browser`, `context`, `pages: Map<string, Page>`, `profileDir`, `headless`, `ignoreHTTPSErrors`
- `ensureBrowser(name, options)` checks existing entry: if options match (headless/ignoreHTTPSErrors/connected), reuse; else `stopBrowser(name)` then `launchBrowser()`
- Persistent profile at `~/.dev-browser/browsers/<name>/chromium-profile` (line 379)
- Page registry within browser: `getPage(browser, pageName)` returns existing or creates new page

**Relevance to pyhost v2**:
- pyhost currently tracks sessions as `Map<sid, session>` (one-shot ID per `session.open`)
- Downloader needs **named profile registry** (e.g. `"comix-main"`, `"backup-session"`) to resume interrupted downloads or switch sources without re-login
- `ensure` pattern: reuse existing headed session when manga UI requests it; close+reopen when switching headless/headed mode

**Not portable**:
- Chromium backend (`playwright.chromium.launchPersistentContext`) — pyhost must use `AsyncCamoufox(persistent_context=True, user_data_dir=...)`
- Node.js `path.join`, `os.homedir` — Python equivalent: `pathlib.Path`, `os.environ['LOCALAPPDATA']`

### 10.2 Ensure/relaunch behavior

**Pattern** (browser-manager.ts:95–128):
```typescript
async ensureBrowser(name, options) {
  const existing = this.browsers.get(name);
  const needsRelaunch =
    existing.type !== "launched" ||
    !existing.browser.isConnected() ||
    (options.headless !== undefined && existing.headless !== requestedHeadless) ||
    (options.ignoreHTTPSErrors !== undefined && existing.ignoreHTTPSErrors !== requestedIgnoreHTTPSErrors);
  if (!needsRelaunch) return existing;
  await this.stopBrowser(name);
  return this.launchBrowser(name, requestedHeadless, requestedIgnoreHTTPSErrors, options);
}
```
- Relaunch trigger: disconnected, type mismatch, or option drift
- Clean stop before relaunch (no leaked process)

**Relevance to pyhost v2**:
- Manga UI user switches "Download headless" toggle mid-session → pyhost must detect option drift, close existing browser, relaunch with new mode
- Network hiccup → `browser.isConnected()` false → auto-relaunch instead of silent failure

**Implementation note**:
- pyhost already has `_drop_session(sid)` cleanup ladder (H004 fix #3)
- Extend to: `ensure_browser(name, headless, profile_dir)` → check registry → compare options → relaunch if drift

### 10.3 Request deadline and cancellation

**Pattern** (execute-request.ts:58–80, lock.ts:7–24, 42–50):
- Every request carries `deadline: number` (absolute timestamp) and `signal: AbortSignal`
- `RequestSession` constructor sets timeout, attaches disconnect listener, aborts on either trigger
- Lock system: `waitForTurn(previous, signal)` races previous promise vs abort signal; `signal.addEventListener("abort", onAbort, {once: true})`
- Cancellation propagates: `this.#controller.abort()` → all pending ops throw `abortReason(signal)`

**Relevance to pyhost v2**:
- Long manga chapter download (199 pages, 15+ minutes) must honor cancellation when user closes CamoProf or stops download
- Python equivalent: `asyncio.timeout()`, `asyncio.Task.cancel()`, `async with timeout(300): ...`
- pyhost protocol needs `cancel` command or timeout field per request

**Implementation note**:
- Current pyhost has no mid-request cancellation (only startup timeout)
- Add: `{"id":7,"cmd":"cancel","target_id":5}` → abort running `download_chapter` task, cleanup temp files, return `CANCELLED`

### 10.4 Per-browser locking

**Pattern** (lock.ts:26–61, execute-request.ts:29):
```typescript
const withLock = createKeyedLock<K>();  // Map<K, Promise<void>>
await withLock(browserName, async () => { ... }, {signal});
```
- Keyed lock: multiple requests to **same browser** serialize; requests to **different browsers** run parallel
- Lock released in `finally`, cleaned up when tail promise resolves
- Prevents: two scripts racing to navigate/click/type in same browser session

**Relevance to pyhost v2**:
- Two manga titles downloading from same Comix session → must serialize (one navigates away, the other loses context)
- CamoProf Google check + manga download share `"main"` profile → serialize via lock
- Python equivalent: `asyncio.Lock` per browser name, stored in `Map<str, asyncio.Lock>`

**Implementation note**:
```python
self.browser_locks = {}  # str -> asyncio.Lock
async def with_browser_lock(self, name, coro):
    lock = self.browser_locks.setdefault(name, asyncio.Lock())
    async with lock:
        return await coro
```

### 10.5 Disconnected-browser cleanup

**Pattern** (browser-manager.ts:331–349, 420, 468):
```typescript
async stopBrowser(name) {
  const entry = this.browsers.get(name);
  if (!entry) return;
  this.browsers.delete(name);
  entry.pages.clear();
  try {
    if (entry.type === "launched") {
      await this.closeLaunchedBrowser(entry);
    } else {
      await entry.browser.close();
    }
  } catch { /* best effort */ }
}

this.attachBrowserLifecycle(entry);  // line 420
entry.browser.on("disconnected", () => { ... });  // line 468
```
- On disconnect event, remove from registry (stale entry never blocks future `ensureBrowser`)
- `stopAll()` during daemon shutdown: `Promise.allSettled` → no hang on one zombie browser

**Relevance to pyhost v2**:
- Camoufox process killed externally (Task Manager, OOM) → pyhost must detect disconnect, drop session, log event, not block future opens
- Python equivalent: `browser.on("disconnected", lambda: self._handle_disconnect(name))`
- Playwright Python: `browser.on("disconnected", callback)`

**Implementation note**:
- Extend `session.open` response: return `"browser_pid": <int>` so C# can track external kill
- Add disconnect handler in `__aenter__`: `ctx.browser.on("disconnected", lambda: asyncio.create_task(self._cleanup_disconnected(sid)))`

### 10.6 Active-request tracking

**Pattern** (idle-browser-reaper.ts:52–64):
```typescript
requestStarted(browserName) {
  const state = this.#getOrCreateActivity(browserName);
  state.activeRequests += 1;
  state.lastActivityAt = this.#now();
}
requestFinished(browserName) {
  state.activeRequests = Math.max(0, state.activeRequests - 1);
  state.lastActivityAt = this.#now();
}
```
- Every `execute` call increments counter before running script, decrements after
- Used by idle reaper (§10.7) to distinguish "busy browser" vs "zombie browser"

**Relevance to pyhost v2**:
- Manga UI shows "2 active downloads" → pyhost tracks `active_requests` per browser
- Prevent idle-close while download running: `if active_requests > 0: skip reaper`

**Implementation note**:
```python
self.browser_activity = {}  # name -> {"active": int, "last_at": float}
async def cmd_download_chapter(self, msg):
    name = self._get_browser_name(msg)
    self.browser_activity.setdefault(name, {})["active"] += 1
    try:
        ...
    finally:
        self.browser_activity[name]["active"] -= 1
        self.browser_activity[name]["last_at"] = time.time()
```

### 10.7 Idle browser reaper

**Pattern** (idle-browser-reaper.ts:39–69, 98–155):
- Configurable `idleTimeoutMs` (default 0 = disabled)
- After each request finish, schedule next deadline check
- When `activeRequests == 0` AND `idleSince > timeout`, call `stopBrowser(name)`
- Never reap a browser with active requests (even if last activity was long ago — long download counts as "active")

**Relevance to pyhost v2**:
- User leaves CamoProf open overnight, no activity → auto-close browsers after 30min idle, keep pyhost alive
- Manga download running 2 hours → never reap (active request)
- Config: `CITADEL_BROWSER_IDLE_TIMEOUT_MS` env var or pyhost command `{"cmd":"configure","idle_timeout_ms":1800000}`

**Implementation note**:
```python
async def _idle_reaper_loop(self):
    while not self.shutting_down:
        await asyncio.sleep(60)  # check every minute
        now = time.time()
        for name, state in list(self.browser_activity.items()):
            if state["active"] == 0 and (now - state["last_at"]) > self.idle_timeout:
                await self._stop_browser(name)
```

### 10.8 Typed request/response protocol

**Pattern** (protocol.ts:1–139):
- Zod schemas: `ExecuteRequestSchema`, `BrowserStopRequestSchema`, `StatusRequestSchema`, `InstallRequestSchema`, `StopRequestSchema`
- Discriminated union on `type` field
- Response types: `stdout`, `stderr`, `complete`, `error`, `result`
- Each request has unique `id: string`, responses echo it
- Streaming: `{type:"stdout", id, data}` → `{type:"complete", id, success:true}`

**Relevance to pyhost v2**:
- pyhost currently: one NDJSON line per request → one NDJSON response
- Manga download needs **streaming progress**: `{type:"progress", id, chapter_id, page:5, total:199}`
- Error classification: `NETWORK_FAILURE`, `SOURCE_UNAVAILABLE`, `DECODE_FAILED`, `STORAGE_FULL`

**Implementation note**:
```python
# Streaming download:
async def cmd_download_chapter(self, msg):
    chapter_id = msg["chapter_id"]
    pages = await self._fetch_page_list(chapter_id)
    for i, page_url in enumerate(pages):
        await self._send(msg["id"], {"type":"progress", "page":i+1, "total":len(pages)})
        await self._download_page(page_url, i)
    await self._send(msg["id"], {"type":"complete", "cbz_path": ...})
```

### 10.9 Daemon ownership and shutdown

**Pattern** (daemon.ts:379–412, 545–560):
```typescript
async function shutdown(exitCode = 0) {
  if (shuttingDown) return shuttingDown;
  shuttingDown = (async () => {
    server?.close();
    await drainAllClients();
    await manager.stopAll();
    await unlinkSocketIfExists();
    process.exit(exitCode);
  })();
  return shuttingDown;
}
registerShutdownHandlers();  // SIGINT/SIGTERM → shutdown(0), uncaughtException → shutdown(1)
```
- Single shutdown invocation (idempotent via `shuttingDown` flag)
- Drain clients → stop browsers → cleanup socket → exit
- Graceful: `stop` request returns `{stopping:true}` before shutdown starts

**Relevance to pyhost v2**:
- Citadel closes CamoProf → `{"cmd":"shutdown"}` → pyhost drains active downloads (wait up to 30s), closes browsers, exits 0
- Unhandled exception → log error, attempt browser cleanup, exit 1
- Python equivalent: `signal.signal(signal.SIGTERM, lambda s,f: asyncio.create_task(shutdown()))`

**Implementation note** (already exists in pyhost.py, verified 2026-09-01):
```python
async def cmd_shutdown(self, _msg):
    self.shutting_down = True
    for sid in list(self.sessions.keys()):
        await self._drop_session(sid)
    return {"stopping": True}
```
- Extend: add `await asyncio.wait_for(drain_active_downloads(), timeout=30)`

### 10.10 Relevance summary for pyhost v2 Manga Downloader

| dev-browser pattern | Portable to pyhost v2 | Backend constraint |
|---|---|---|
| Named browser registry | ✅ Yes — `Map<str, BrowserEntry>` | Camoufox, not Chromium |
| Ensure/relaunch | ✅ Yes — option drift detection, clean stop before relaunch | Camoufox `AsyncCamoufox(...)` |
| Request deadline + cancellation | ✅ Yes — `asyncio.timeout()`, abort signal | Python `asyncio.Task.cancel()` |
| Per-browser locking | ✅ Yes — `asyncio.Lock` per browser name | Language-agnostic |
| Disconnected cleanup | ✅ Yes — `browser.on("disconnected", ...)` | Playwright Python API |
| Active-request tracking | ✅ Yes — counter per browser | Language-agnostic |
| Idle browser reaper | ✅ Yes — background `asyncio` loop | Language-agnostic |
| Typed request/response | ✅ Yes — NDJSON + streaming progress | Python `TypedDict` + `json.dumps` |
| Daemon shutdown | ✅ Yes — drain + cleanup ladder | Already in pyhost.py |
| Playwright Chromium backend | ❌ NOT portable — Comix needs Camoufox | Must use `AsyncCamoufox` |
| CDP `connectOverCDP` | ❌ NOT portable — Camoufox uses Playwright adapter | Not needed (persistent context sufficient) |
| QuickJS WASM sandbox | ❌ NOT portable — Node.js security model | Not relevant (pyhost runs native Python) |
| Unix socket daemon | ❌ NOT portable — Windows uses named pipes differently | pyhost uses stdin/stdout, not socket |

**Comix evidence tie-in** (`RESEARCH-comix-downloader-2026-09-01.md`):
- Plain Chromium: secure-bundle JS error, blank body (lines 120–126)
- Direct Camoufox: full success (lines 127–128, 191–243)
- **Conclusion**: any pyhost v2 downloader backend **MUST use Camoufox**, not Playwright Chromium. dev-browser lifecycle patterns are portable; browser engine is not.

---

## 11. Open decisions (not resolved by this reconciliation)

1. **Manga downloader authorization** — research record exists, provisional seam documented, but **no approval to implement** pyhost v2 scraping commands, Comix adapter, or native CBZ writer
2. **stealthB fixes** — wrapper lifecycle defect documented (`AttributeError: 'AsyncCamoufox' object has no attribute 'new_page'`), but **no approval to fix** or integrate into Citadel
3. **Idle timeout default** — dev-browser uses 0 (disabled); should pyhost v2 default to 30min, 0, or user-configured?
4. **Mid-request cancellation protocol** — needs design: new `cancel` command, timeout per request, or SIGINT propagation?
5. **Streaming progress format** — `{type:"progress", ...}` vs separate `download.status` command?

---

*Plan approved for implementation on 2026‑08‑31. UI refactor + account health implemented and verified 2026‑09‑01. No core/, setting/ (except new shared components), release tooling, or approved architecture changes were made. Worktree carries only the files listed above plus LO-owned `module/mangareader/TASKLIST.md` modification (Comix research note appended).*

**Reconciliation completed 2026-09-01 — current state verified from live workspace.**
