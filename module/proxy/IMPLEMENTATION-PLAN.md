# Citadel Proxy Pool + Consumer Adapters — Implementation Plan

**Status:** refined against the live Citadel checkout and `SCRAPE_PROXY (1).txt`

**Scope:** Proxy pool producer, shared read contract, CamoProf adapter, MangaReader Downloader/Catalog adapters, and focused tests

**Out of scope:** a local HTTP proxy server, Cloudflare bypass, Webshare, scheduled sync, Library/History/Cover Builder traffic, and unrelated shared UI/core changes

## 1. Goal

Deliver one explicit producer/consumer flow:

1. fetches public proxy candidates from the source lists already declared by the legacy script;
2. normalizes, validates, deduplicates, and excludes banned entries;
3. optionally checks TCP reachability with bounded concurrency;
4. atomically publishes the resulting active pool to Citadel LocalAppData;
5. exposes Pool, Sync, and Settings screens with progress and cancellation;
6. lets CamoProf opt in to that pool when launching its Camofox sessions;
7. lets MangaReader Downloader and Catalog opt in independently for their
   browser and provider-owned HTTP traffic.

Proxy remains the sole writer. CamoProf and MangaReader remain read-only
consumers through adapters owned by their respective citizens. There is no DI or
runtime object sharing across citizens.

## 2. Review of the previous plan

The previous plan must not be implemented as written because it does not match either source.

| Previous claim | Actual evidence | Refinement |
| --- | --- | --- |
| Proxy is a shared DI service exported to all citizens | `IModule` only exposes `Route` and `CreateView(Lifetime)`; the Proxy citizen has no service-registration seam | Keep the module standalone. Cross-citizen consumption needs a separate approved contract later |
| MangaReader provider rules belong inside Proxy | No such behavior exists in `SCRAPE_PROXY (1).txt`; it is a list fetcher/validator | Keep provider behavior in MangaReader and add only a read-only consumer adapter there |
| Direct fallback is enabled when proxies fail | This could expose the real IP and is unrelated to pool generation | No fallback behavior is introduced. A future consumer must choose an explicit fail-closed policy |
| Round-robin/random/least-used selection is producer behavior | The source contains no selector or request routing | Proxy only publishes a snapshot; each consumer adapter owns its small independent cursor and in-memory quarantine |
| Webshare and manual lists are source inputs | The source uses ProxyScrape and public GitHub-hosted lists | Preserve the actual source catalog only |
| Python/PyHost is required for pool generation | The useful source behavior uses standard HTTP, parsing, files, and TCP sockets; the current PyHost v1 has sequential request/response and no progress events | Port pool generation to feature-owned C#. Extend the existing PyHost launch payload only where Camofox already exists |
| FTF is the implementation reference | The live FTF citizen is also an empty shell | Use the live CamoProf composition/lifetime pattern and shared Setting components as the structural references |
| `proxy.txt` belongs in the workspace root | Installed citizens and source folders are not durable state locations | Store state under `%LOCALAPPDATA%\Citadel\Proxy` |
| Success means Cloudflare is bypassed | A successful TCP connection only proves the proxy endpoint accepts a connection | Report `Reachable`, not “Cloudflare-safe” or “working for Comix” |
| More than 80% test coverage and real public-proxy integration tests are required | That is broad, flaky, and unrelated to the smallest safe port | Add only focused deterministic tests plus one operator smoke flow |

## 3. Legacy source behavior

Source reviewed: `C:\Users\YUZHA\Downloads\Telegram Desktop\SCRAPE_PROXY (1).txt`.

### Keep

- Fourteen configured endpoints across ProxyScrape and GitHub-hosted lists.
- Fetch each source independently; one failed source does not fail the whole job.
- Accept either `scheme://host:port` or source-default `host:port` input.
- Normalize and deduplicate candidates.
- Exclude entries present in the banned list.
- TCP-connect validation with a configurable timeout.
- Stop after the requested valid target is reached.

### Correct while porting

- Use cancellable async I/O instead of a fixed 150-thread executor.
- Bound validation concurrency so the Citadel UI remains responsive.
- Keep the old active pool if sync is cancelled, returns zero usable entries, or fails before commit.
- Write new state to a staging file and replace the active file atomically.
- Never print authenticated proxy credentials; UI and logs show a masked address.
- Sort the final output deterministically before commit.
- Persist only module-owned data, not the legacy Boterdrop copy.

### Do not port

- `BOTERDROP_FILE` and the second legacy output.
- `bot_register.py` coupling.
- Unused `asyncio`, `TEST_URL2`, and `_test_proxy_http` paths.
- Legacy `--no-check` publication. Canonical `proxy.txt` is consumed directly by
  adapters and must contain only entries that passed the configured TCP probe.
- The misleading assumption that HTTP 403/503 proves a proxy is healthy for a target provider.
- A permanent background timer or auto-sync service.

## 4. Architecture and ownership

```text
ProxyModule
  -> ProxyView (composition root, owns Lifetime cleanup)
       -> PoolView
       -> SyncView
       -> SettingsView

SyncView -> ProxySyncCoordinator -> ProxySyncService
                                      -> ProxySourceCatalog
                                      -> ProxyReachabilityProbe
                                      -> sharedLogic contracts/stores

PoolView ------------------------------> sharedLogic/ProxyPoolStore
SettingsView --------------------------> sharedLogic/ProxySettingsStore

ProxyPoolStore --atomic publish--> %LOCALAPPDATA%\Citadel\Proxy\proxy.txt
                                         |
                         module/sharedLogic/cs/ProxyPoolContract.cs
                              /                         \
             CamoProf/ProxyPoolAdapter       MangaReader/ProxyPoolAdapter
                       |                        /                    \
        BrowserSessionCoordinator    Downloader lane           Catalog lane
                       |               |          |                  |
                shared PyHost       HTTP       Camofox             Camofox/HTTP
```

Rules:

- `ProxyModule` only exposes the citizen route and passes the shell `Lifetime` to `ProxyView`.
- `ProxyView` is the composition root. It creates shared module-owned instances and hosts child views; it contains no fetch, parse, validation, or file logic.
- Each feature owns its view and feature behavior.
- `sharedLogic` contains only contracts/mechanisms used by more than one Proxy feature and imports no feature namespace.
- `module/sharedLogic/cs/ProxyPoolContract.cs` is the only cross-citizen data
  contract. It is compiled into the three citizens and exposes immutable
  snapshots; it has no module-specific policy and no mutable global service.
- Proxy is the only writer of canonical pool state. Consumer adapters never
  modify `proxy.txt`, `banned-proxies.txt`, or Proxy settings.
- CamoProf and MangaReader do not reference `Module.Proxy.dll` or each other.
- MangaReader owns two independent adapter instances: Downloader and Catalog.
  Their cursor, quarantine, browser process, profile, and session never merge.
- No dependency-injection framework, repository layer, event bus, or background Windows service is added.

## 5. Durable and transient state

Module state root:

```text
%LOCALAPPDATA%\Citadel\Proxy\
├── proxy.txt
├── banned-proxies.txt
└── settings.json
```

### `proxy.txt`

- Compatibility format: one normalized proxy URL per line.
- Accepted schemes are `http`, `https`, `socks4`, and `socks5`.
- Contains the last successfully committed pool.
- Missing file means an empty pool, not a startup failure.
- A sync writes `proxy.txt.staging`, flushes it, then atomically replaces `proxy.txt`.
- Cancelled/failed/empty sync deletes its staging file and leaves `proxy.txt` unchanged.
- If an endpoint contains authentication, compatibility requires the full URL in
  this user-local file. It is plaintext LocalAppData state; it must never be
  committed, copied to logs, or rendered unmasked in the UI.

### `banned-proxies.txt`

- One normalized proxy URL per line.
- A banned entry is excluded before validation and removed from the active pool.
- Duplicate lines collapse on load.
- Writes are atomic.

### `settings.json`

Only these settings are persisted:

| Setting | Default | Guard |
| --- | ---: | --- |
| Target usable entries | 300 | positive, capped to a documented desktop-safe maximum |
| Source request timeout | 15 seconds | bounded |
| TCP connect timeout | 5 seconds | bounded |
| Parallel TCP checks | 32 | bounded; deliberately lower than the legacy fixed 150 threads |

Malformed or unreadable settings fall back to defaults and surface one non-fatal status message.

Transient sync progress, selection, table rows, and cancellation state are not persisted. Shared `UiPreference.Key` values persist only tab/table/presentation choices.

## 6. Canonical pool contract

The adapter contract is file-based because Citadel citizens are isolated and
have no cross-citizen DI service.

### Canonical location

```text
%LOCALAPPDATA%\Citadel\Proxy\proxy.txt
```

This exact path is owned by `ProxyPoolContract.ActivePoolPath`. No consumer
guesses the workspace, executable, or module-install directory.

### Line grammar

```text
scheme://[username:password@]host:port
```

- Supported producer schemes: `http`, `https`, `socks4`, `socks5`.
- Blank lines and `#` comments are ignored.
- Invalid or unsupported lines are skipped with a count, never echoed verbatim.
- The snapshot reader returns normalized immutable endpoints plus the file's
  last-write revision.
- User/password may be read only to construct transport launch options. Display,
  errors, telemetry, and responses use a masked endpoint.

### Read consistency

- Proxy publishes by atomic replacement, so consumers observe either the old
  complete file or the new complete file.
- An adapter reloads the snapshot only when acquiring a **new** HTTP operation
  or browser session and the file revision changed.
- Existing Camofox sessions remain pinned to their selected proxy until closed;
  a pool refresh never mutates a running browser.
- A new snapshot resets that adapter's local cursor and removes quarantine
  entries that no longer exist.

### Consumer selection

- Each adapter uses deterministic round-robin over compatible endpoints.
- A transport/launch failure quarantines that endpoint only in the reporting
  adapter's memory for the current snapshot revision.
- Consumer quarantine never edits Proxy's active or banned files.
- A target transport skips schemes it cannot support. Exact .NET 10 and
  installed Camofox scheme support is verified before implementation; the
  producer still preserves every supported source scheme.
- Proxy mode is opt-in and persisted per lane. Default is `Direct` to preserve
  existing behavior.
- When proxy mode is enabled, missing/empty/incompatible pool state fails closed
  before a request or browser launch. There is no silent direct connection.

The optional C# → PyHost launch shape is shared by all three browser owners:

```json
{
  "proxy": {
    "server": "socks5://host:port",
    "username": "optional",
    "password": "optional"
  }
}
```

No `proxy` property means Direct mode. Authentication is separated from
`server`, validated once by `proxy_launch.py`, and never included in a response.

## 7. Consumer adapter behavior

### CamoProf adapter

The CamoProf Launcher gets a persisted `Use proxy pool` toggle. On a new profile
launch:

1. `BrowserSessionCoordinator` asks its CamoProf-owned adapter for one endpoint.
2. The optional proxy launch object is sent through `PyHost.OpenSessionAsync`.
3. Shared pyhost validates the object and passes it to `AsyncCamoufox` before
   entering the persistent context.
4. The session registry stores only whether a proxy was used and a masked key;
   credentials are never returned.
5. A launch failure quarantines the endpoint. The next explicit launch selects
   the next compatible entry.

Changing the toggle does not mutate a running profile. The next launch uses the
new mode.

### MangaReader adapter

MangaReader creates two independent adapters:

| Lane | Owns |
| --- | --- |
| Downloader | provider browse/search/detail, manual URL probes, Queue page transport, detail/auto-cover traffic, and Downloader Camofox |
| Catalog | catalog sync/detail/cover traffic and Catalog Camofox |

Each lane gets its own persisted `Use proxy pool` toggle. Existing browser
separation remains intact: Downloader continues using `mangareader_downloader`
and Catalog continues using `mangareader_catalog`.

Browser behavior:

- acquire one endpoint only when a new browser session is required;
- include proxy mode in session reuse identity so switching Direct/Proxy closes
  the old session before opening the replacement;
- keep one selected endpoint for the session lifetime;
- on launch failure, close partial state and quarantine the endpoint for the next
  explicit Start.

HTTP behavior:

- provider code continues owning URL, method, headers, cookies, parsing, and
  typed provider errors;
- `ProxyHttpTransport` owns handler/client creation and applies one adapter lease;
- no new automatic replay policy is added. On a proxy transport failure, that
  attempt fails normally and quarantines the endpoint; the next existing
  request/job retry obtains the next endpoint;
- Direct mode uses the existing direct transport explicitly; Proxy mode never
  falls back to it.

Library, History, Reader, and Cover Builder are not changed by this phase.

## 8. Producer end-to-end behavior

### Startup

1. Shell creates `ProxyView` with a `Lifetime`.
2. `ProxyView` composes stores/services/views once.
3. Pool loads the last committed `proxy.txt` immediately.
4. No network request starts merely because the module or a tab opened.

### Sync

1. User presses **Start sync**.
2. The coordinator creates one job cancellation token and disables a second concurrent start.
3. Sources are fetched one at a time, preserving the legacy request shape and avoiding a burst across fourteen endpoints.
4. Each source is normalized and deduplicated; source failures are recorded and processing continues.
5. Banned entries are removed.
6. Candidates are TCP-probed with bounded async concurrency until candidates end or the target is reached.
7. A non-empty successful result is sorted and atomically committed.
8. PoolView refreshes only after the commit event; it never reads a half-written staging file.

Progress reports phase, sources completed/total, candidate count, tested count, reachable count, failed count, and elapsed time. It never includes proxy credentials.

### Stop

- **Stop** requests cancellation and returns control to the UI immediately.
- Outstanding source/probe operations observe the cancellation token.
- Cancellation is idempotent.
- The previous committed pool remains visible and usable.
- Navigating away destroys the supplied `Lifetime`, cancels the active job, detaches handlers, and disposes the module-owned `HttpClient`.

### Pool actions

- **Reload** rereads the last committed pool from disk.
- **Ban selected** writes the normalized entries to `banned-proxies.txt`, removes them from `proxy.txt`, and refreshes the table.
- No automatic “dead” mutation occurs outside an explicit sync or user action.

## 9. UI composition

Rename the placeholder internal tabs:

| Tab | Responsibility |
| --- | --- |
| Pool | Current committed proxy list and explicit ban/reload actions |
| Sync | Start/Stop, progress, source summary, and the last operation result |
| Settings | Target, timeouts, parallelism, Save/Reset |

Reuse only existing shared controls:

- `SettingTabs` for the three tabs.
- `SettingViewport Mode="Contained"` where the table/progress surface owns scrolling.
- `SettingActionCard` for compact command/status areas.
- `SettingTable` for the pool, with `UiPreference.Key="proxy.pool.table"`.
- `SettingField`, `SettingButton`, and `SettingToggle` for input/actions.
- Existing shared ComboBox styles if a bounded selector is needed.

Do not modify `setting/Components` or copy shared styles into the module. The pool table uses the existing resizable columns, centered headers, compact rows, virtualization, and persisted width/order/sort behavior.

Consumer UI adds only existing `SettingToggle` controls:

| Surface | Preference key | Default |
| --- | --- | --- |
| CamoProf Launcher | `camoprof.launcher.use-proxy-pool` | Off/Direct |
| MangaReader Downloader | `mangareader.downloader.use-proxy-pool` | Off/Direct |
| MangaReader Catalog | `mangareader.catalog.use-proxy-pool` | Off/Direct |

Each toggle states the active mode and shows a concise pool-empty error on the
owning screen. Toggling alone does not start a request, launch a browser, or
restart an existing session.

## 10. File inventory and boundaries

### Existing files to modify

| File | Job | Boundary |
| --- | --- | --- |
| `module/proxy/ProxyModule.cs` | Pass the shell-owned `Lifetime` into the view | No feature construction or service logic |
| `module/proxy/ProxyView.xaml` | Host Pool, Sync, and Settings tabs with `ContentControl` | No feature-specific controls inside the composition root |
| `module/proxy/ProxyView.xaml.cs` | Construct module-owned stores/services/views and dispose them through `Lifetime` | No parsing, HTTP, TCP, or persistence logic |
| `module/proxy/layout.json` | Rename editable visibility slots to `PoolPanel`, `SyncPanel`, `SettingsPanel` | Only names that exist as XAML `x:Name` values |
| `module/proxy/Module.Proxy.csproj` | Compile-link only `module/sharedLogic/cs/ProxyPoolContract.cs` | No Python plugin, package, or another citizen reference |

### New cross-citizen read contract

| File | Job | Boundary |
| --- | --- | --- |
| `module/sharedLogic/cs/ProxyPoolContract.cs` | Own canonical path, line grammar, immutable endpoint/snapshot types, masking, and read/parse behavior | Read-only; no selection, quarantine, sync, HTTP handler, UI, or module policy |

### New Proxy-owned shared mechanisms

| File | Job | Boundary |
| --- | --- | --- |
| `module/proxy/sharedLogic/AtomicTextFile.cs` | Small atomic UTF-8 text replacement helper used by both stores | No proxy parsing or UI knowledge |
| `module/proxy/sharedLogic/ProxyPoolStore.cs` | Load/atomically commit active and banned lists through `ProxyPoolContract`; enforce staging semantics | No HTTP/TCP work and no WPF types |
| `module/proxy/sharedLogic/ProxySettings.cs` | Typed settings defaults and bounds | No persistence or UI types |
| `module/proxy/sharedLogic/ProxySettingsStore.cs` | Load/save/reset `settings.json` atomically | No proxy pool or network work |

### New Pool feature

| File | Job | Boundary |
| --- | --- | --- |
| `module/proxy/Features/Pool/PoolView.xaml` | Shared table and Pool action layout | No business logic |
| `module/proxy/Features/Pool/PoolView.xaml.cs` | Bind rows and forward Reload/Ban user actions to the store | No direct path construction or network calls |

### New Sync feature

| File | Job | Boundary |
| --- | --- | --- |
| `module/proxy/Features/Sync/ProxySourceCatalog.cs` | Define the fourteen legacy source names, URLs, and default schemes | No HTTP calls and no user secrets |
| `module/proxy/Features/Sync/ProxyReachabilityProbe.cs` | Perform one cancellable TCP connect and return typed reachability evidence | No retries, storage, or UI state |
| `module/proxy/Features/Sync/ProxySyncService.cs` | Fetch sources, normalize/dedupe, exclude banned entries, run bounded probes, and return a result | Does not commit files and does not touch WPF |
| `module/proxy/Features/Sync/ProxySyncCoordinator.cs` | Enforce one active job, own cancellation/progress, commit successful results, and notify Pool | No parsing and no control manipulation |
| `module/proxy/Features/Sync/SyncView.xaml` | Start/Stop action card, progress, and source/result summary | No network or persistence logic |
| `module/proxy/Features/Sync/SyncView.xaml.cs` | Translate UI events to coordinator commands and render coordinator state | No direct HTTP/TCP/file access |

### New Settings feature

| File | Job | Boundary |
| --- | --- | --- |
| `module/proxy/Features/Settings/SettingsView.xaml` | Shared setting fields/toggle and Save/Reset actions | No persistence logic |
| `module/proxy/Features/Settings/SettingsView.xaml.cs` | Validate entered values and call the settings store | No proxy sync execution |

### Existing shared Camofox bridge to modify

| File | Job | Boundary |
| --- | --- | --- |
| `module/sharedLogic/cs/PyHost.cs` | Add an optional typed proxy launch payload to `OpenSessionAsync` | Transport only; no pool reading or selection |
| `module/sharedLogic/pyhost/proxy_launch.py` | Validate the optional payload and produce the exact installed Camofox launch option | No pool/file access; never logs or returns credentials |
| `module/sharedLogic/pyhost/pyhost.py` | Apply the validated optional proxy before shared CamoProf `AsyncCamoufox` launch | Existing session/profile lifecycle remains unchanged |
| `module/sharedLogic/tests/test_pyhost.py` | Cover direct launch, proxied launch, invalid payload, and credential non-echo | No live network or browser required |

### CamoProf consumer adapter

| File | Job | Boundary |
| --- | --- | --- |
| `module/camoprof/sharedLogic/ProxyPoolAdapter.cs` | Read canonical snapshots, own CamoProf cursor/quarantine/mode, and return one launch lease | Read-only toward Proxy state; no browser or UI logic |
| `module/camoprof/sharedLogic/BrowserSessionCoordinator.cs` | Acquire a lease only for a new session, pass it to PyHost, and report launch failure to the adapter | Does not parse/read pool files itself |
| `module/camoprof/CamoprofView.xaml.cs` | Construct one adapter and provide it to the coordinator/Launcher | Composition only |
| `module/camoprof/Launcher/LauncherView.xaml` | Add the persisted `Use proxy pool` toggle with existing shared UI | No custom style or network behavior |
| `module/camoprof/Launcher/LauncherView.xaml.cs` | Forward the persisted mode to the adapter and display typed pool errors | Does not select or parse proxies |
| `tests/Module.Camoprof.Tests/Module.Camoprof.Tests.csproj` | Link the new adapter into the existing focused test assembly | No new test framework/package |
| `tests/Module.Camoprof.Tests/ProxyPoolAdapterTests.cs` | Snapshot reload, round-robin, quarantine, empty pool, and masked evidence | Filesystem seam uses a temporary root |
| `tests/Module.Camoprof.Tests/BrowserSessionCoordinatorTests.cs` | Optional proxy payload, direct mode preservation, and failed-launch quarantine | Fake PyHost delegate; no real Camofox |

### MangaReader consumer adapters

| File | Job | Boundary |
| --- | --- | --- |
| `module/mangareader/shareLogic/ProxyPoolAdapter.cs` | Read canonical snapshots and own one lane's cursor/quarantine/mode | One instance per Downloader/Catalog lane; no UI/browser/provider policy |
| `module/mangareader/shareLogic/ProxyHttpTransport.cs` | Create/cache proxy-aware handlers and send one provider-owned request using a lane lease | No URL/header/parser/retry policy and no silent direct fallback |
| `module/mangareader/MangaReaderView.xaml.cs` | Construct separate Downloader and Catalog adapters/transports and pass them through existing contexts | Composition only; never selects a proxy |
| `module/mangareader/Features/Downloader/DownloaderContract.cs` | Expose the Downloader adapter/transport contract to Downloader-owned screens | No selection logic |
| `module/mangareader/Features/Catalog/CatalogContext.cs` | Expose the Catalog adapter/transport contract to Catalog-owned screens | No selection logic |
| `module/mangareader/Features/Downloader/DownloaderPyHostClient.cs` | Pin an optional lease to a new Downloader browser session and include proxy mode in reuse identity | No canonical file access |
| `module/mangareader/Features/Catalog/Runtime/CatalogBrowserClient.cs` | Pin an optional lease to a new Catalog browser session and include proxy mode in reuse identity | Remains independent from Downloader session state |
| `module/mangareader/Features/Downloader/mangareader_downloader/browser.py` | Validate/apply optional proxy through shared `proxy_launch` before Downloader Camofox launch | Existing profile/session cleanup remains intact |
| `module/mangareader/Features/Catalog/mangareader_catalog/browser.py` | Validate/apply optional proxy through shared `proxy_launch` before Catalog Camofox launch | No Downloader imports or shared session |
| `module/mangareader/Features/Downloader/Catalog/CatalogScreen.xaml` | Add Downloader's persisted proxy-mode toggle | Existing controls/styles only |
| `module/mangareader/Features/Downloader/Catalog/CatalogScreen.xaml.cs` | Forward mode and use Downloader transport for detail/cover fetches | No pool parsing/selection |
| `module/mangareader/Features/CatalogMirror/CatalogMirrorView.xaml` | Add Catalog's persisted proxy-mode toggle | Existing controls/styles only |
| `module/mangareader/Features/CatalogMirror/CatalogMirrorView.xaml.cs` | Forward Catalog mode and surface typed pool errors | No pool parsing/selection |
| `module/mangareader/Features/Downloader/Sources/MangaSourceRegistry.cs` | Inject the one Downloader HTTP transport into HTTP-backed registrations | Provider list and capabilities remain unchanged |
| `module/mangareader/Features/Downloader/Sources/CucumberManga/CucumberMangaSource.cs` | Send existing provider-owned requests through the injected transport | Parser, filters, headers, and contracts unchanged |
| `module/mangareader/Features/Downloader/Sources/DrakeScans/DrakeScansSource.cs` | Send existing provider-owned requests through the injected transport | Parser, filters, headers, and contracts unchanged |
| `module/mangareader/Features/Downloader/ManualUrl/DynamicManualSource.cs` | Send existing manual probes through the injected transport | Probe ordering/parsers unchanged |
| `module/mangareader/Features/Downloader/Queue/PageTransport.cs` | Use the Downloader transport for direct page attempts; retain existing browser fallback | Existing Queue retry/concurrency and partial-CBZ behavior unchanged |
| `module/mangareader/Features/Downloader/Queue/DownloadQueueFeature.cs` | Pass the existing Downloader transport when creating `PageTransport` | No proxy selection logic |
| `module/mangareader/Features/CatalogMirror/CatalogMirrorCoverCache.cs` | Use Catalog transport for remote Catalog covers | Cache path/database behavior unchanged |
| `tests/Module.Mangareader.Downloader.Tests/ProxyPoolAdapterTests.cs` | Independent lane state, reload, empty pool, quarantine, and no cross-lane lock |
| `tests/Module.Mangareader.Downloader.Tests/ProxyHttpTransportTests.cs` | Direct mode, proxy mode, fail-closed empty pool, and no implicit replay |
| `tests/Module.Mangareader.Downloader.Tests/test_browser_transport.py` | Downloader/Catalog launch payload and credential non-echo |
| `tests/Module.Mangareader.Downloader.Tests/CatalogNetworkTriggerTests.cs` | Toggling/opening a tab does not create a request or browser session |
| `tests/Module.Mangareader.Downloader.Tests/Architecture/LevelBGuardTests.cs` | Adapters stay in `shareLogic`; Downloader and Catalog do not import each other's internals |

### Proxy producer tests to add

| File | Coverage |
| --- | --- |
| `tests/Module.Proxy.Tests/Module.Proxy.Tests.csproj` | Compile-links the Proxy pure C# files and shared pool contract into the existing xUnit pattern; no new package |
| `tests/Module.Proxy.Tests/ProxyPoolContractTests.cs` | Supported schemes, source-default scheme, auth masking, malformed lines, normalization, dedupe |
| `tests/Module.Proxy.Tests/ProxyPoolStoreTests.cs` | Missing files, banned exclusion, atomic replace, and preservation of the previous pool on aborted commit |
| `tests/Module.Proxy.Tests/ProxySyncServiceTests.cs` | Partial source failure, target stop, validation, and bounded cancellation using fake HTTP/TCP seams |
| `tests/Module.Proxy.Tests/ProxyLevelBGuardTests.cs` | Parent remains thin; Pool/Sync/Settings stay inside their feature boundaries |

### Files explicitly unchanged

- `module/proxy/module.json`: identity and route are already correct.
- `module/ftf/**`: not an implementation reference and not edited.
- `core/**` and `setting/**`: no new core service, shared UI component, style,
  template, or dependency.
- MangaReader Library, History, Reader, and Cover Builder features: no proxy
  routing in this plan.

## 11. Error contract

The feature surfaces typed operation outcomes rather than treating every condition as an exception:

| Condition | Result |
| --- | --- |
| One source times out/fails | Record source failure; continue remaining sources |
| Every source fails | Sync fails; keep old pool |
| Candidate line malformed | Count as skipped; do not abort source |
| Candidate already banned | Count as banned/skipped |
| TCP timeout/refusal | Mark candidate unreachable for this run only |
| Target reached | Cancel remaining probes internally and complete successfully |
| User presses Stop | Cancel job; keep old pool; status `Cancelled` |
| Result contains zero publishable entries | Do not replace active pool; status `No usable proxies` |
| State file malformed/unreadable | Show one actionable error; do not silently overwrite it during load |
| Atomic commit fails | Keep old pool and report the exact file operation error without credentials |
| Consumer is in Direct mode | Preserve the current transport exactly; do not read the pool |
| Proxy mode enabled but pool missing/empty | Fail `PROXY_POOL_EMPTY` before any outbound request/launch |
| Pool has no scheme compatible with the target transport | Fail `PROXY_POOL_INCOMPATIBLE`; never switch to Direct |
| Proxy browser launch fails | Close partial session, quarantine endpoint in that adapter, surface original typed launch error |
| Proxy HTTP transport fails | Quarantine endpoint for that lane/revision and return the failure; do not replay or fall back implicitly |
| Pool is atomically replaced while a browser runs | Keep the running session pinned; reload only on its next new session |
| Authenticated proxy is used | Credentials enter only the handler/launch payload and never appear in status, response, or logs |

## 12. Implementation order

### Slice 0 — Baseline contract

- Record the canonical path, grammar, masking, and read-only snapshot contract in
  `ProxyPoolContract.cs`.
- Verify the installed .NET 10 and Camofox proxy schemes before fixing adapter
  compatibility lists.
- Preserve Direct as the behavior of every consumer while adapters are absent or
  disabled.

### Slice 1 — Proxy data and persistence

- Implement atomic text writes, endpoint parsing through the shared contract,
  pool store, and settings model/store.
- Verify parser and persistence tests.

### Slice 2 — Sync engine

- Implement the source catalog, source fetch, bounded TCP probe, result model inside `ProxySyncService`, and coordinator cancellation/commit behavior.
- Verify only Sync service tests.

### Slice 3 — Pool and Sync UI

- Build Pool and Sync child views using shared Setting components.
- Wire progress without replacing the pool collection on every progress tick.
- Refresh Pool only after a successful commit or an explicit Pool action.

### Slice 4 — Settings UI and shell composition

- Build Settings child view.
- Rename tabs/layout slots.
- Pass `Lifetime` through `ProxyModule` and register one cleanup path in `ProxyView`.

### Slice 5 — Prove the producer boundary

- Build Proxy and run its focused tests.
- Live smoke Start → Stop → Start and atomic pool publication.
- Do not start consumer integration until `proxy.txt` is observed at the exact
  LocalAppData path with only complete normalized lines.

### Slice 6 — Shared Camofox payload

- Add the read contract to the shared C# sources already compiled by CamoProf and
  MangaReader.
- Add optional proxy launch payload validation/application to existing PyHost and
  the two MangaReader browser plugins.
- A missing payload must remain byte-for-byte equivalent in behavior to today's
  Direct launch path.

### Slice 7 — CamoProf adapter

- Add CamoProf's read-only adapter and Launcher toggle.
- Pass one selected lease through `BrowserSessionCoordinator` only when opening a
  new session.
- Verify Direct, Proxy, pool-empty, failed launch, and credential non-echo paths.

### Slice 8 — MangaReader browser adapters

- Construct independent Downloader and Catalog adapter instances.
- Add independent persisted toggles.
- Pass optional leases to each browser client/plugin without merging profiles,
  PyHost processes, session registries, Start/Stop state, or idle cleanup.
- Verify mode changes replace only the owning lane's session.

### Slice 9 — MangaReader HTTP adapter

- Route only Downloader/Catalog-owned HTTP paths listed in the file inventory
  through their owning `ProxyHttpTransport`.
- Remove production use of static direct clients from those paths while keeping
  current provider request/parsing behavior and existing test seams.
- Keep Queue's existing attempt/concurrency policy; proxy transport does not add
  another retry loop.
- Confirm Library, History, Reader, and Cover Builder remain untouched.

### Slice 10 — Targeted verification

Run only:

```powershell
dotnet test tests/Module.Proxy.Tests/Module.Proxy.Tests.csproj -c Release
dotnet test tests/Module.Camoprof.Tests/Module.Camoprof.Tests.csproj -c Release
dotnet test tests/Module.Mangareader.Downloader.Tests/Module.Mangareader.Downloader.Tests.csproj -c Release
python -m unittest module.sharedLogic.tests.test_pyhost -v
python tests/Module.Mangareader.Downloader.Tests/test_browser_transport.py
dotnet build module/proxy/Module.Proxy.csproj -c Release
dotnet build module/camoprof/Module.Camoprof.csproj -c Release
dotnet build module/mangareader/Module.Mangareader.csproj -c Release
git diff --check
```

Then perform one operator-visible smoke:

1. Open Proxy; verify opening the module sends no network request.
2. Start sync; verify progress changes and UI remains responsive.
3. Stop mid-run; verify the prior pool remains unchanged.
4. Start again; allow a small target to commit.
5. Verify Pool reload, table sort/resize persistence, Ban selected, and tab navigation.
6. Close/navigate away during sync; verify the job is cancelled and no worker/process remains.
7. Enable CamoProf proxy mode; launch one headed and one headless profile action,
   then close them and verify no session/process is orphaned.
8. Enable only MangaReader Downloader proxy mode; verify Downloader works while
   Catalog stays Direct and retains its separate browser process.
9. Enable only MangaReader Catalog proxy mode; verify Catalog works while
   Downloader state remains unchanged.
10. With each consumer in Proxy mode, temporarily use an empty pool and verify it
    fails closed without any direct request.
11. Refresh the pool while a browser is open; verify the browser stays pinned and
    the next newly opened session reads the new snapshot.

No full Citadel suite, release, commit, or push belongs to this implementation step unless separately requested.

## 13. Acceptance criteria

- Proxy appears in the existing sidebar and opens three working tabs: Pool, Sync, Settings.
- Opening the module or changing tabs never starts a fetch.
- Sync reproduces the useful legacy flow: fetch, normalize, dedupe, banned exclusion, optional TCP validation, target stop, save.
- Source failures are isolated and visible.
- Start → Stop → Start works without stale job state.
- Stop/navigation cancellation never replaces the last committed pool.
- Active pool writes are atomic and survive application updates under LocalAppData.
- Authenticated proxy credentials are not exposed in UI status or logs.
- UI uses existing shared components and remains responsive while syncing.
- Targeted tests/build pass, and live smoke remains explicitly operator-verified.
- The canonical consumer input is exactly
  `%LOCALAPPDATA%\Citadel\Proxy\proxy.txt`; no adapter reads a workspace/module
  copy.
- CamoProf can opt in and passes one proxy to Camofox before context creation.
- MangaReader Downloader and Catalog can opt in independently for their browser
  and owned HTTP traffic.
- A running Camofox session never changes proxy in place.
- Direct remains the default and preserves current behavior.
- Proxy mode with an unavailable pool fails closed and never exposes the direct
  connection as a fallback.
- Pool selection/quarantine is independent across CamoProf, Downloader, and
  Catalog.
- Existing provider parsers, browser profiles, Queue concurrency/retry, and
  partial-CBZ behavior remain unchanged.

## 14. Deferred work

These are intentionally excluded from the first end-to-end delivery:

- automatic scheduled pool refresh;
- provider-specific health scoring or Cloudflare/challenge classification;
- writing consumer failures back into Proxy's banned list;
- one global cursor or lock shared by all citizens;
- live migration of an already-running browser to another proxy;
- proxying Library, History, Reader, or Cover Builder traffic;
- making Proxy mode the default;
- adding a local listening proxy/gateway process.
