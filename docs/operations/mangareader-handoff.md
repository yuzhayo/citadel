# MangaReader handoff

Snapshot: 2026-09-23, branch `main`, commit `ab0f48a`, version/tag `v3.0.17`.

This document is the operational handoff for the MangaReader citizen. Source is
authoritative; historical plans under `docs/history/` and the unchecked
Downloader phases in `module/mangareader/TASKLIST.md` are not reliable progress
trackers anymore because the corresponding implementation already exists.

## Entry point and visible product

- Citizen metadata: `module/mangareader/module.json`.
- Entry type: `Module.Mangareader.MangaReaderModule`.
- Route: `manga-reader`; sidebar order: `10`.
- Composition root: `module/mangareader/MangaReaderModule.cs`.
- Main view: `module/mangareader/MangaReaderView.xaml(.cs)`.
- Visible top-level tabs are **Library**, **History**, **Downloader**, and
  **Queue**.
- Cover Builder is a hidden tab opened from Library title detail. It is not a
  top-level navigation tab.
- The current Downloader embeds its own catalog/detail workflow. There is no
  separate top-level Catalog tab.

## Ownership and lifecycle

`MangaReaderModule` implements both `IModule` and `IResidentModule`.

1. `AttachApplicationLifetime` creates one `DownloaderBackgroundService`.
2. That service owns the source registry, browser/PyHost client, HTTP transport,
   proxy pool adapter, queue, source index, lister, cover fetcher, and online
   process.
3. `CreateView` borrows the existing `DownloaderContext`; navigation recreates
   presentation, not the background runtime.
4. Queue startup restores persisted in-flight work as **Paused**, so application
   startup does not automatically generate provider traffic.
5. Application-lifetime disposal order is online process -> queue -> browser ->
   HTTP transport -> cover client.

Consequences:

- Closing/hiding the Citadel window or navigating to another citizen must not
  silently stop active MangaReader work.
- Presentation code must unsubscribe/dispose its own handlers, but must not
  dispose the borrowed queue/provider runtime.
- At this snapshot MangaReader runtime is eagerly attached at module
  registration. The proposed module Start/Stop behavior is **not implemented**.

### Future Start/Stop contract requested by the operator

Keep the MangaReader route and sidebar item visible. A stopped module should
show a lightweight stopped view with a Start button; it must not disappear from
navigation.

- **Start:** construct/attach MangaReader runtime and show the normal view.
- **Stop:** explicitly pause and persist running queue jobs, then dispose the
  MangaReader-owned runtime.
- **Navigate away:** hide/detach presentation only; do not stop MangaReader.
- **Start after Stop:** recreate runtime and restore persisted jobs as Paused.

Do not implement this by unregistering the route, unloading the DLL, or tying
runtime lifetime to tab navigation.

## Feature map

| Area | Owner | Responsibility |
|---|---|---|
| Composition | `MangaReaderModule`, `MangaReaderView`, `MangaReaderHandoffs` | Runtime construction, screen wiring, cross-feature translation only |
| Library | `Library/` | Scan local title folders/CBZ files, title detail, chapter selection, grouping, library root |
| History | `History/` | Recent/pinned reading history and reopen flow |
| Reader | `Reader/` | Standalone reader window and modular reader features |
| Cover Builder | `CoverBuilder/` | Select/fetch/bake a cover into the earliest chapter archive safely |
| Downloader catalog | `Features/Downloader/Catalog/` | Current visible remote browse/search/detail/chapter selection UI |
| Manual URL | `Features/Downloader/ManualUrl/` | Probe supported title/chapter URLs and hand them to the queue |
| Queue | `Features/Downloader/Queue/` | Durable job state, scheduling, staging, retries, resume, CBZ publication |
| Source adapters | `Features/Downloader/Sources/` | Provider routes, parsing, normalization, manifests |
| Lister/index | `Features/Downloader/Lister/`, `DownloadSourceIndex.cs` | Map remote identities to local folders/files and report local availability |
| Auto cover | `Features/Downloader/AutoCover/` | Fetch/store `cover.png` for mapped local titles |
| Shared domain | `shareLogic/` | Archive helpers, source contracts, proxy adapter/transport, neutral models |
| RAR runtime | `Features/Rar/` | Bundled RAR executable/license payload used by archive support |

The parent view is a composition root. Provider logic belongs in its provider
folder; queue rules belong in Queue; reusable UI primitives belong in
`setting/Components`, not in the parent.

## Catalog naming: do not mix these paths

There are currently three catalog-shaped code areas:

1. `Features/Downloader/Catalog/` is the **active Downloader screen** wired by
   `DownloaderView` and `DownloaderContext`.
2. `Features/Catalog/` is a separate PyHost-backed Catalog host.
3. `Features/CatalogMirror/` is an SQLite offline mirror/sync/query stack used by
   the separate Catalog host and covered by tests.

The latter two are not constructed by `MangaReaderModule`, `MangaReaderView`, or
`DownloaderBackgroundService` in this snapshot. Do not diagnose a visible
Downloader Catalog bug by editing CatalogMirror unless the composition root is
first changed deliberately.

The module project still deploys both Python packages:

- `mangareader_downloader` for Downloader.
- `mangareader_catalog` for the separate Catalog runtime.

These packages are designed for separate browser/session hosts.

## Provider contract and registrations

The neutral contract is `shareLogic/Sources/MangaSourceContracts.cs`.
`IMangaSource` exposes capabilities plus browse, lookup, title, groups, chapters,
and manifest operations. `MangaSourceRegistry` is an explicit closed list; there
is no reflection or filesystem plugin discovery.

| Provider | Current transport/flow | Important notes |
|---|---|---|
| Comix | Downloader PyHost page-context calls to captured `/api/v1/*` routes | Search + advanced filters; author/artist lookup; encrypted responses are decoded in the page client; chapter pages are cached per title |
| Cucumber Manga | Direct HTTP/optional proxy transport + HTML parsing | Search + advanced filters; one source group; no remote lookup kinds |
| Drake Scans | JSON catalog/genre API plus Next.js RSC title/chapter/page payloads | Search + advanced filters; genre lookup; chapter listing can span provider pages |
| AsuraScans | Direct JSON API for catalog/title; title HTML/Astro data for chapters with chapter-sitemap fallback; HTML page manifest | Search, no advanced filters; sitemap result is cached for the adapter instance |
| ThunderScans | WordPress sitemap for title discovery; title HTML for detail and chapters; chapter HTML for pages | Search, no advanced filters; chapter list is parsed directly from `#chapterlist li[data-num]` |
| Dynamic manual | Auxiliary source, reachable through Manual URL probing | Not shown as a normal catalog registration |

### ThunderScans invariant fixed in v3.0.17

- Parse chapter rows locally from the returned title-page element; do not issue
  per-chapter requests merely to build the table.
- Keep display number and route identity separate. Example: display `06` may
  normalize to `6`, while the manifest route must remain the original `...-06/`.
- Legacy routes without `-chapter-` are valid and must not be filtered out.
- Chapter sitemap fallback was removed from the normal chapter-list path.

### AsuraScans is not ThunderScans

They share `IMangaSource`, UI, queue, and proxy transport boundaries, but do not
share parsing logic or endpoint flow. A Thunder parser change must not be copied
into Asura without captured Asura evidence.

## Downloader request boundaries

- Opening the Downloader tab, changing a filter, or navigating presentation must
  not start a provider request.
- Search/Start and explicit continuation actions own remote requests.
- Detail requests must use the selected provider's real contract; do not infer a
  route from another provider or multiply requests to compensate for a parser
  defect.
- Measure browser bootstrap, provider/API latency, parsing/rendering, and cover
  loading separately before changing concurrency or adding caching.
- Do not bypass provider challenges or access controls.

## Proxy behavior

- Adapter: `shareLogic/ProxyPoolAdapter.cs`.
- HTTP transport: `shareLogic/ProxyHttpTransport.cs`.
- Browser owner: `Features/Downloader/DownloaderPyHostClient.cs`.
- Reservations shared inside this citizen: `ProxyLeaseRegistry`.
- Pool data comes from the Proxy citizen contract; MangaReader does not own the
  pool file.
- Direct mode performs direct requests.
- Proxy mode chooses compatible, health-ordered candidates and reserves them so
  simultaneous MangaReader operations do not reuse the same endpoint blindly.
- Browser session open and HTTP GET paths have bounded failover attempts.
- The active browser proxy is shown unmasked by `ActiveProxyDisplay`.
- **Change proxy** ends/rotates the browser session and skips the current endpoint
  when an alternative is available. It does not mutate an already completed
  response or retroactively change the proxy of an existing request.
- Queue transfers can use independent proxy sessions and only fall back to shared
  transport after independent candidates are exhausted.

## Queue and publication pipeline

The queue is durable and grouped by title. Current UI supports Start/Resume,
Stop All, Remove selected, and per-title/per-chapter Resume, Stop, and Remove.
Published CBZ files are retained when queue/staging entries are removed.

High-level flow:

1. Catalog/Manual URL/Update Checker creates immutable remote identities.
2. Queue validates the library target and persists the job before executing it.
3. Provider manifest resolves the ordered remote pages.
4. `ChapterDownloadPipeline` downloads into a job-specific staging directory and
   journals pages in `job.json`; valid staged pages are reusable on resume.
5. Page transport validates/decodes content and records failure state.
6. `CbzChapterPublisher` writes a temporary archive, embeds
   `META-INF/citadel-source.json` (and incomplete metadata when applicable), then
   atomically moves the completed archive into the library.
7. `DownloadSourceIndex` records the confirmed remote/local mapping.

Never publish directly over the destination while pages are still downloading.
Do not let an unreadable/newer queue schema be interpreted as an empty queue and
overwritten.

## Reader

`ReaderWindow.xaml.cs` is intended to remain a thin composition root.
`ReaderDefaultFeatureCatalog` is the single registration point for:

- chapter loading and chapter navigation;
- overlay, drawer, chrome, and toast;
- fullscreen and pin;
- auto scroll and manual scroll;
- zoom and dim;
- reset.

Auto-scroll speed and manual wheel-scroll speed are separate preferences. The
manual-scroll feature must alter user-driven scrolling only; it must not reuse or
mutate the auto-scroll running state.

The Reader contracts/hubs live in `Reader/ReaderCore/`. New ordinary Reader
features should be registered through `ReaderDefaultFeatureCatalog`, communicate
through the command/activity hubs, and avoid adding feature-specific branches to
`ReaderWindow`.

`Reader/REFACTOR-SPEC.md` is a historical implementation spec. Confirm live code
before treating any of its phases as pending.

## Library, History, Cover Builder, and updates

- Library root is shared through `LibraryRootContext` and persisted separately.
- Library scanner treats local CBZ/archive contents as the source of truth.
- History stores at most 15 ordinary recent entries; pinned entries survive
  clear/retention.
- Cover Builder operates on a chosen local title and performs a safe archive
  replacement only after the replacement is complete.
- Update Checker uses confirmed provider/title/group bindings. It must not guess
  a provider identity from only a display title.
- `MangaReaderHandoffs.cs` is the only composition-level home for translations
  spanning Library, Downloader, CatalogMirror, Queue, and shared confirmation UI.

## Persistence and generated state

Default persistent state is outside the deployment folder under
`%LocalAppData%\Citadel\MangaReader`:

| State | Default path |
|---|---|
| Library root | `library-path.txt` |
| Library groups | `library-groups.json` |
| Reading history | `history.json` |
| Reader preferences | `reader-preferences.json` |
| Update bindings | `update-bindings.json` |
| Downloader root | `downloads\` |
| Queue | `downloads\queue.json` |
| Remote/local source index | `downloads\source-index.json` |
| Job staging | `downloads\jobs\<job-id>\` |

CatalogMirror storage is rooted by an injected `CatalogMirrorPaths`; it contains
`catalog.db` and hashed `enrichment/<identity>/metadata.json` plus `cover.cache`.
Because the current composition root does not construct CatalogMirror, do not
assume a production root without first wiring/locating its owner.

`bin/`, `obj/`, packaged runtimes, staging pages, browser profiles, and release
artifacts are generated state, not source. Do not commit them.

## Shared UI contract

MangaReader already consumes the shared setting components, including
`SettingTabs`, `SettingButton`, `SettingField`, `SettingViewport`, `SettingTable`,
`SettingTableActions`, `SettingActionCard`, `SettingToggle`, `SettingDialog`, and
`SettingDrawer`.

Before creating a new control, search `setting/Components` and reuse or extend the
shared primitive. Feature-specific composition remains inside MangaReader; a
generic reusable primitive belongs to `setting/Components`.

## Tests and proportional verification

Test projects:

- `tests/Module.Mangareader.Archive.Tests`
- `tests/Module.Mangareader.Downloader.Tests`
- `tests/Module.Mangareader.History.Tests`
- `tests/Module.Mangareader.Library.Tests`
- `tests/Module.Mangareader.Reader.Tests`

Downloader tests include provider contracts/fixtures, queue persistence/resume,
CBZ publication, proxy behavior, CatalogMirror, Manual URL, and the Level B
architecture guard.

Use the narrowest proof that matches the change, for example:

```powershell
dotnet test tests/Module.Mangareader.Downloader.Tests/Module.Mangareader.Downloader.Tests.csproj --filter AsuraThunderSourceTests
dotnet test tests/Module.Mangareader.Reader.Tests/Module.Mangareader.Reader.Tests.csproj
dotnet build module/mangareader/Module.Mangareader.csproj -c Release
```

Tests/builds prove code behavior and compilation, not rendered WPF behavior or a
live provider contract. UI changes require a visible smoke check; provider changes
require a current, authorized live capture or a recorded fixture matching it.

## Known handoff risks

1. `module/mangareader/TASKLIST.md` still labels Downloader phases 0-8 as not
   started even though Downloader is implemented. Treat that section as stale
   until it is reconciled deliberately.
2. `docs/architecture/flows-mangareader.md` is a useful architectural snapshot,
   but it predates some current Library/History/Catalog and provider work.
3. `Features/Catalog` and `Features/CatalogMirror` are compiled/tested but not
   wired into the active MangaReader composition root.
4. Provider HTML/API/RSC contracts are time-dependent. A parser failure should
   first be compared with the provider's current response, not patched with extra
   requests or cross-provider assumptions.
5. MangaReader is resident and currently eager-started. Any future Start/Stop
   work must preserve paused queue state and keep navigation visible.

## Safe continuation checklist

1. Read root `AGENTS.md`, the relevant Citadel skills, this handoff, and the live
   target owner.
2. Check `git status`, `version.props`, branch, and current tag before editing.
3. Identify the active code path; especially distinguish Downloader Catalog from
   the unwired Catalog/CatalogMirror stack.
4. Capture or use an existing provider response fixture before changing parsing.
5. Keep display normalization separate from remote identity/route values.
6. Preserve explicit network triggers and durable queue-before-execution rules.
7. Reuse shared UI components and keep the parent composition-only.
8. Run only the targeted tests/build needed for the touched scope.
9. Do not bump, commit, push, or release unless explicitly requested.

## Related maintained/reference documents

- General project handoff: `docs/operations/handoff.md`.
- MangaReader flow analysis: `docs/architecture/flows-mangareader.md`.
- Reader historical refactor spec: `module/mangareader/Reader/REFACTOR-SPEC.md`.
- MangaReader task record: `module/mangareader/TASKLIST.md` (contains stale
  Downloader progress markers; source wins).
- Historical Downloader plan/research:
  `docs/history/2026/plans/mangareader-downloader.md` and
  `docs/history/2026/research/comix-downloader.md`.
