# PLAN: MangaReader local-first Downloader

Status: **LOCKED — implementation has not started**
Baseline: `main` at `ae450bc` on 2026-09-02. The worktree contains unrelated
Reader-control WIP; this plan does not authorize touching, reverting, building
over, or claiming that work.

This document turns the recorded Comix research and the user's selected product
decisions into the complete implementation contract for MangaReader's
Downloader. It supersedes the discovery-only Downloader notes in
`module/mangareader/TASKLIST.md`. It does not authorize implementation, a
commit, a release, changes to `stealthB`, or live mutation of the real manga
library.

## 1. Goal

Add one local-first Downloader tab to MangaReader. The Downloader browses a
remote provider catalog, lets the user choose one source group and chapters,
runs a persistent download queue, and publishes only complete, validated CBZ
files into the selected Citadel library.

The product boundary is:

```text
remote catalog -> explicit selection -> persistent queue -> staged pages
  -> decode/descramble -> complete validation -> atomic CBZ publication
  -> local MangaReader Library
```

The Downloader is not a remote streaming reader, a Suwayomi clone, or a
background Windows service. The existing local Library and Reader remain the
only reading surfaces.

## 2. Evidence baseline

The canonical evidence is
`.docs/RESEARCH-comix-downloader-2026-09-01.md`. It proves, within the tested
logged-out sample:

- direct Camoufox can render Comix and reach its signed `/api/v1` calls;
- ordinary Chromium/WebView-like execution failed on the secure bundle;
- catalog, search, title, group, chapter, and ordered page-manifest discovery
  are individually feasible;
- Comix chapter number alone is not a safe identity because multiple groups
  publish variants;
- two sampled `5x5`, algorithm-3 scrambled pages were decoded successfully;
- a transient ten-page CBZ fixture passed archive and image validation; and
- the current `stealthB` wrapper failed before navigation and is not a ready
  reusable browser library.

The following remain implementation gates, not proven facts:

- a complete live chapter download;
- retry, recovery, pause, restart resume, and cancellation;
- long-running rate-limit and domain behavior;
- every scramble/encryption variant;
- the final PyHost v2, queue, persistence, and WPF behavior; and
- exact live semantics for public actions not exercised during research, such
  as `I'm Feeling Lucky`.

The live Advanced Filters inspection on 2026-09-02 established the public
catalog surface:

| Filter | Contract |
|---|---|
| sort | 13 provider-defined options |
| content rating | multi-select: Safe, Suggestive, Erotica, Pornographic |
| type | Manga, Manhwa, Manhua, Other |
| genre/format | searchable tag multi-select; AND/OR mode; 31 genres and 9 formats observed |
| demographic | Josei, Seinen, Shoujo, Shounen |
| release status | Releasing, Finished, On hiatus, Discontinued, Not yet released |
| minimum chapter | numeric text input |
| release year | numeric From/To inputs |
| authors/artists | remote lookup fields with no initial list |
| Adjust listing | login-gated; excluded from the logged-out scope |
| Reset / I'm Feeling Lucky | explicit actions, not submenus |

The observed result count is remote and volatile. It must never be hard-coded.
The initial Comix filter state uses the provider-observed defaults: latest
update and Safe + Suggestive (`Safe + 1`).

## 3. Locked product behavior

### 3.1 One tab, two routed screens

MangaReader gains one `Downloader` tab. That tab contains exactly two routed
child screens:

```text
DownloaderView (parent/router)
├── CatalogScreen
└── DownloadListScreen
```

There is no nested tab control, third title-detail screen, modal catalog, or
separate OS window.

`DownloaderView` owns only the current route and module-lifetime services. Each
child owns its own state. The parent may route these typed events only:

```text
CatalogScreen      -> OpenDownloadList
CatalogScreen      -> ChaptersQueued
DownloadListScreen -> BackRequested
Queue              -> QueueSummaryChanged
```

Routing back to Catalog restores its provider, filters, result page, selected
title/group, selected chapters, and scroll position. Queue execution is not
owned by the visible child and continues while Catalog is shown.

### 3.2 Lazy provider activation

- The provider dropdown is backed by a small explicit registry. It contains
  only Comix in the first implementation.
- Opening Downloader performs no remote request and starts no browser.
- Selecting Comix performs no remote request and starts no browser.
- Typing search text or changing a filter performs no remote request.
- `Start`, `Load more`, explicit Author/Artist lookup, title selection,
  explicit refresh, or a queued job are the only network triggers.
- There is no timer, catalog polling, infinite-scroll fetch, provider prewarm,
  or automatic refresh.
- A provider browser/session is created lazily, retained only while an active
  request or job needs it, and released after bounded idle or module shutdown.
  The implementation must not leave an orphan Camoufox process.

### 3.3 Catalog toolbar and Advanced Filters

The Catalog header contains:

1. web-target dropdown;
2. search field;
3. `Advanced Filters` toggle;
4. `Start`;
5. remote/network state; and
6. `Download List (n)` with an active/paused/failed job count badge.

Advanced Filters are provider-owned composition built from shared fields. The
Comix panel owns the field layout and produces one immutable
`ComixBrowseQuery`; it never builds URLs or calls the provider.

Rules:

- `Start` snapshots the current query and replaces the prior result set.
- A later `Start` is latest-request-wins. A stale response may finish cleanup
  but cannot replace the current query, results, count, error, or pagination.
- `Load more` explicitly appends the next page for the current query snapshot.
- `Reset` restores provider defaults and does not fetch.
- Author and Artist fields fetch only on Enter or an explicit Search action;
  they never query on every keystroke.
- Genre/format free typing filters or resolves provider-supported tags. Unknown
  display text is not sent as a provider value without a resolved provider key.
- Minimum chapter and year ranges validate locally. Empty means unset; invalid
  ranges prevent `Start` and show field-local errors.
- `Adjust listing` is omitted while the Downloader remains logged-out.
- `I'm Feeling Lucky` is not implemented until its live request/result contract
  is captured. Its absence may not block normal Browse delivery.

Every loading, empty, no-result, failed, retry, and stale-result state is
explicit. A failed request leaves the last successful result visible with a
non-destructive error and Retry action.

### 3.4 Catalog results and title detail

Catalog results use remote models, never `MangaTitle` or `ChapterInfo` from the
local Library.

```text
RemoteTitleSummary -> provider catalog result
RemoteTitleDetail  -> selected remote title
MangaTitle         -> existing local folder plus CBZ files
```

Results render as a fluid shared card shell with Downloader-specific data and
actions. Selecting a card changes Catalog's internal state from result grid to
title detail. Back returns to the exact prior grid and scroll anchor without a
new fetch.

Title detail owns:

- remote title identity, title, cover, description, and provider metadata;
- one source-group dropdown;
- the chapter list for the selected group;
- local availability/collision state; and
- chapter selection plus `Queue selected`.

Only one group is active. The UI does not expose an all-groups merged list and
never silently deduplicates or combines variants.

### 3.5 Target-library mapping

Downloader publishes only beneath MangaReader's currently configured Library
root. If no valid Library root is available, queueing is disabled with a link
back to Library setup.

A remote title must map to one local title folder before its first job is
queued:

1. an existing source-identity mapping is reused when its folder still exists;
2. otherwise the user receives a suggested sanitized folder name;
3. a same-named existing folder is never claimed automatically; the user must
   explicitly choose that folder or create a distinct one; and
4. the confirmed mapping is persisted outside the manga folder.

Display-title equality alone is never evidence that a remote and local title
are the same.

### 3.6 Persistent Download List

The only entry to `DownloadListScreen` is the `Download List (n)` button on
Catalog. Download List has `Back to Catalog`; it is not a tab.

It displays queued, active, paused, recovering, awaiting-fallback, failed, and
completed jobs with title, chapter, group, page progress, status, warning, and
actions. Supported actions are:

- Pause/Resume;
- Retry;
- resolve source fallback;
- Remove, with confirmation when staged data exists;
- Open folder after publication; and
- Clear completed.

Queue state is persistent. On app restart, every in-flight state becomes
`Paused`; nothing resumes automatically. A visible Resume action is required.
Pausing cancels bounded active work and keeps validated staging files. Removing
a job deletes its staging only after the queue state has been committed.

### 3.7 Retry, failed-page recovery, and source fallback

The remote ordered page manifest is the completeness authority.

First pass:

1. attempt each expected page;
2. after the initial failure, retry that page at most three times with bounded
   backoff;
3. if it still fails, record a warning and add its identity to
   `FailedPageSet`;
4. continue processing the remaining pages instead of blocking the chapter;
   and
5. never mark or publish the chapter as complete while the set is non-empty.

Recovery pass:

1. refresh the same chapter manifest/signed URLs;
2. retry only `FailedPageSet`, again with bounded attempts;
3. allow an alternate CDN/asset route for the same remote chapter to repair an
   individual failed page; and
4. continue to validation only when the set is empty.

Cross-group fallback:

- If same-chapter recovery still fails, the Comix adapter searches other groups
  for an exact title/chapter candidate.
- Download List enters `AwaitingSourceFallback` and shows the candidates and
  reason. The user must confirm the group change.
- A different scanlation group replaces the whole chapter job. It can never
  supply only one page to the originally selected group's archive.
- The replacement gets the alternate group's identity and filename; the old
  staging remains recoverable until replacement succeeds or the user removes
  it.
- If no safe candidate exists or the user declines, the job is Failed with
  staging retained for explicit Retry/Remove.

This policy prevents a visually complete but semantically mixed chapter.

### 3.8 Local output and cover integration

- Pages stream to staging and are never retained as one complete chapter in
  RAM.
- Content format is detected from bytes, not filename or response URL alone.
- Known scramble headers invoke the provider decoder; unknown algorithms fail
  the affected page visibly.
- The final output is one ordinary ZIP-compatible CBZ per remote chapter item.
- Split chapters, extras, notices, and Author's Notes remain independent items.
- Filenames are deterministic, sanitized, naturally sortable, and include the
  selected group. Collision handling never silently creates `(2)`.
- The CBZ contains a small versioned `META-INF/citadel-source.json` entry. No
  loose metadata JSON is written beside manga files.
- MangaReader must continue ignoring non-image entries when loading pages and
  covers; this is a required regression gate.
- Title-cover fetch plus `Bake cover after batch` is an optional post-download
  action that reuses the existing Cover Builder service. It defaults on for a
  newly created title folder and off for a pre-existing folder.
- A cover failure warns but does not invalidate already published chapters.

## 4. Parent/children architecture

```text
MangaReaderView
└── DownloaderView                         parent/router only
    ├── DownloaderContext                  stable child contract
    ├── CatalogScreen                      owns browse/detail state
    │   ├── SourceRegistry                 source + filter contribution
    │   ├── CatalogCoordinator
    │   └── TitleSelectionCoordinator
    └── DownloadListScreen                 owns queue presentation
        └── DownloadQueueCoordinator       module-lifetime application service
            └── DownloadJobRunner
                └── ChapterDownloadPipeline
```

Children never reach into named controls on their parent or one another. The
stable context exposes only:

- route commands;
- read-only queue summary and change events;
- current Library-root/mapping operations;
- source-registry access;
- lifecycle cancellation/dispatcher access; and
- typed `QueueChapters` commands.

The Queue does not reference WPF. Catalog does not mutate Queue collections.
Library does not call Comix. Downloader does not call internal `LibraryView`
fields. Completion crosses the boundary through a typed `ChapterPublished`
event handled by a small Library bridge.

## 5. Feature ownership

| Feature | Owns | Must not own |
|---|---|---|
| Downloader parent | route and child lifetime | filters, jobs, API, files |
| Source registry | explicit provider list/capabilities | runtime discovery/reflection |
| Comix filter panel | input state and validation | URL/API construction |
| Catalog coordinator | query generation, result/detail state, stale-response guard | local CBZ mutation |
| Comix source adapter | Comix routes, signed calls, parsing, normalization | WPF, queue, archive writing |
| Title selection | active group, chapter selection, target mapping request | download execution |
| Queue coordinator | durable job intent and state transitions | network/browser implementation |
| Job runner | concurrency, pause, retry, recovery orchestration | WPF controls |
| Transport | bootstrap/session, HTTP streaming, browser fallback | Comix parsing, CBZ structure |
| Comix page decoder | Comix scramble header/algorithm handling | queue or publication |
| CBZ publisher | completeness check, package validation, atomic commit | provider UI/API |
| Library bridge | root/mapping and post-publication refresh | remote browsing |
| Cover integration | optional existing Cover Builder invocation | second archive writer |

## 6. Provider contracts

The first implementation uses an explicit registry with one `IMangaSource`.
It is an application seam, not a plugin framework. One
`MangaSourceRegistration` pairs the screen-blind source adapter with its
provider-specific filter contribution. Adding a provider adds its cohesive
provider files plus one registry entry; it does not edit Downloader parent or
Catalog composition.

The source adapter contract remains UI-free:

```csharp
public interface IMangaSource
{
    string Id { get; }
    string DisplayName { get; }
    MangaSourceCapabilities Capabilities { get; }

    Task<RemoteCatalogPage> BrowseAsync(
        RemoteBrowseRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
        RemoteLookupKind kind,
        string query,
        CancellationToken cancellationToken);

    Task<RemoteTitleDetail> GetTitleAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
        RemoteTitleIdentity title,
        RemoteGroupIdentity group,
        CancellationToken cancellationToken);

    Task<RemoteChapterManifest> GetManifestAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken);
}
```

Provider-neutral models contain normalized identity and display data only.
Comix query keys, API payloads, cipher bootstrap, group fields, and scramble
headers remain in `Downloader/Sources/Comix/`. `ComixFilterPanel` is colocated
there because its fields are also provider-specific, but it talks to Catalog
only through the generic filter-contribution contract and cannot call the
adapter directly.

Remote identity is at least:

```text
provider ID + title HID/internal ID + chapter remote ID + group ID
```

Chapter number is display/matching metadata, never the sole key.

## 7. Browser, PyHost v2, and transport contract

### 7.1 Browser backend

Direct Camoufox is the first `IBrowserBackend` because it passed the tested
Comix flow. `stealthB` is not copied or referenced. A later repaired and tested
`stealthB` may implement the same backend without changing Catalog, Queue, or
the provider contract.

Browser responsibilities are deliberately narrow:

- open the provider page/runtime needed for secure bootstrap;
- expose signed API/session data to the adapter;
- perform bounded browser-context fetch fallback when native transport cannot;
  and
- close cleanly on idle, cancellation, EOF, and process shutdown.

It never renders a Citadel streaming reader or owns downloaded chapter state.

### 7.2 PyHost v2

Extend the existing single C#↔Python seam; citizens never import Python files.
All v1 CamoProf commands and semantics remain backward compatible.

Required v2 capabilities:

- request IDs with exactly one terminal response;
- typed progress events associated with a request/job ID;
- active-request cancellation while another request is running;
- per-browser/session locking for mutating operations;
- disconnected-browser cleanup and orphan-safe shutdown;
- `browser.open`, `page.goto`, `page.evaluate_json`,
  `page.fetch_to_file`, `browser.close`, and `request.cancel`;
- bounded response sizes and timeouts;
- URL validation; and
- destination-path containment for browser downloads.

`page.fetch_to_file` streams directly to an allowed staging destination and
returns status, response headers, byte count, detected content evidence, and
hash. Page bytes are never base64-encoded into NDJSON.

Startup readiness is split:

- v1 account commands still require absolute `CITADEL_CREDENZ`;
- Downloader browser commands require absolute `CITADEL_BROWSER_ROOT` and
  `CITADEL_DOWNLOAD_ROOT`;
- browser profiles live under
  `%LocalAppData%\Citadel\MangaReader\browser\<provider>` and never under a
  CamoProf Google profile; and
- `ping` reports protocol/capability readiness separately.

### 7.3 Hybrid transport

The browser negotiates the secure/signed session. C# then uses bounded native
HTTP streaming as the primary page path, including the required Referer and
session headers. Browser-context fetch is the fallback for DNS, TLS route,
cookie, fingerprint, or provider rejection that the browser can satisfy.

No code changes Windows DNS, the hosts file, certificate trust, or system proxy.
A network-class failure is not treated as an absent chapter. Credentials,
cookies, signed material, and full URLs containing sensitive query values are
not logged.

## 8. Download pipeline

```text
Resolve immutable manifest
  -> create/recover job staging
  -> stream expected pages
  -> validate bytes and decode
  -> provider transform/descramble
  -> first-pass FailedPageSet
  -> failed-only recovery
  -> optional whole-chapter source fallback
  -> completeness validation
  -> build same-volume temporary CBZ
  -> reopen and validate every image entry
  -> atomic publication
  -> queue/index commit
  -> Library refresh
  -> optional cover bake
```

### 8.1 Job states

```text
Queued
Resolving
Downloading
Recovering
AwaitingSourceFallback
Decoding
Validating
Publishing
Paused
Failed
Completed
```

Only `Completed` has a published, verified final file. A crash or cancellation
in any earlier state cannot leave a final-path CBZ that appears complete.

### 8.2 Concurrency and cancellation

- Initial default: one active chapter and two concurrently streamed pages.
- Each page has bounded timeout, initial attempt, and at most three retries per
  pass.
- Job cancellation is cooperative first; process-tree termination is the last
  orphan-safety escalation.
- Pause waits for or cancels current bounded writes, validates completed staging
  files, commits queue state, then reports Paused.
- Manual Resume revalidates staging before reuse.
- Queue order is stable; retrying one job cannot silently reorder other jobs.

### 8.3 Staging and resume

```text
%LocalAppData%\Citadel\MangaReader\downloads\jobs\<job-id>\
├── job.json
├── manifest.json
└── pages\
```

These are runtime files, not manga-library sidecars. Every staged page records
ordinal, remote page identity, expected/observed size when available, content
hash, detected format, transform state, and validation state. Resume reuses a
page only when its record and bytes validate against the current immutable job
manifest.

If a refreshed provider manifest changes identity/order, the job does not mix
old and new manifests. It becomes a visible conflict requiring a restart or a
new job.

### 8.4 Atomic publication

The publisher writes a unique `.partial.<guid>.cbz` in the final target folder
so final rename stays on the same volume. Before commit it verifies:

- every expected page exists exactly once and in manifest order;
- every image opens and decodes;
- no zero-byte or HTML/error payload is present;
- the internal source manifest matches the job identity/page count;
- ZIP integrity; and
- final filename collision policy.

New files are atomically renamed into place. Replacing an existing file uses
the existing archive lock/replacement policy and retains only the latest backup.
Different source identity at the same target path is a conflict, not an
overwrite and not an automatic `(2)` suffix.

## 9. Persistence and update safety

All mutable Downloader state lives under LocalAppData so app updates cannot
remove it:

```text
%LocalAppData%\Citadel\MangaReader\
├── browser\
│   └── comix\
└── downloads\
    ├── queue.json
    ├── source-index.json
    └── jobs\<job-id>\...
```

`queue.json` stores durable jobs and their last committed state.
`source-index.json` stores confirmed remote-title-to-local-folder mappings and
published remote chapter identities. Both use bounded fail-soft reads, schema
versions, normalized-path process-wide gates, unique same-folder temporary
files, atomic replacement, and cleanup in `finally`.

Missing/corrupt queue data cannot delete manga files. Unknown versions are
preserved and reported rather than overwritten. A queue-save failure blocks a
state transition that would otherwise make on-disk staging ambiguous, while a
nonessential index-save failure leaves a published CBZ intact and raises a
repairable warning.

The CBZ source manifest schema v1 contains only provenance needed for identity
and recovery:

```json
{
  "version": 1,
  "provider": "comix",
  "titleId": "dy88",
  "chapterId": "remote-id",
  "chapterNumber": "143",
  "groupId": "9897",
  "groupName": "Official",
  "pageCount": 199,
  "manifestHash": "sha256:..."
}
```

Browse results, active result page, filters, and scroll position are retained
only while the Downloader module instance lives. They are not persisted in v1.

## 10. Shared-component boundary

Reuse the existing:

- `SettingButton`;
- `SettingField` and `SettingPasswordField` behavior where applicable;
- `SettingToggle`;
- `SettingTable` and `SettingTableActions`;
- `SettingScrollViewerStyle` and auto-fading shared scrollbar;
- `SettingComboBoxStyle` for single-select provider/group/sort fields;
- `SettingActionCard`; and
- theme/viewport resources.

Add only missing general primitives with behavior pairs and shared UIA tests:

```text
setting/Components/
├── MultiSelect.xaml(.cs)       checklist dropdown + selected summary
└── TagPicker.xaml(.cs)         searchable resolved tags + selected chips
```

The shared primitives know nothing about Comix, genres, ratings, authors,
artists, providers, or remote requests. Numeric fields use `SettingField` with
feature-owned validation. Author/Artist lookup presentation stays inside
Downloader until a second real consumer proves a general shared lookup
component.

The Catalog grid may extract the visual frame of `MangaTitleCard` only if both
local and remote consumers can use a screen-blind data/presentation contract.
Local and remote models and actions remain separate; no adapter may fill a
local `MangaTitle` with remote placeholders.

## 11. Target file tree

A folder is introduced only when a feature owns multiple cohesive files. No
folder is created merely to contain one file.

```text
module/mangareader/
├── MangaReaderView.xaml(.cs)                 add one Downloader tab only
├── Downloader/
│   ├── DownloaderView.xaml(.cs)              parent/router
│   ├── DownloaderContract.cs                 context, route, typed events
│   ├── Catalog/
│   │   ├── CatalogScreen.xaml(.cs)
│   │   ├── CatalogCoordinator.cs
│   │   ├── CatalogState.cs
│   │   └── TitleSelectionCoordinator.cs
│   ├── Queue/
│   │   ├── DownloadListScreen.xaml(.cs)
│   │   ├── DownloadQueueCoordinator.cs
│   │   ├── DownloadJob.cs
│   │   ├── DownloadJobRunner.cs
│   │   └── DownloadQueueStore.cs
│   ├── Pipeline/
│   │   ├── ChapterDownloadPipeline.cs
│   │   ├── PageTransport.cs
│   │   ├── PageRecoveryPolicy.cs
│   │   ├── DownloadIdentity.cs
│   │   ├── DownloadSourceIndex.cs
│   │   └── CbzChapterPublisher.cs
│   └── Sources/
│       ├── IMangaSource.cs
│       ├── MangaSourceRegistry.cs
│       ├── MangaSourceModels.cs
│       └── Comix/
│           ├── ComixSourceAdapter.cs
│           ├── ComixBrowseQuery.cs
│           ├── ComixContracts.cs
│           ├── ComixFilterPanel.xaml(.cs)
│           └── ComixPageDecoder.cs
├── shareLogic/
│   └── Archive/                              extend only generic publication seams
└── Library/                                  typed refresh bridge, no remote logic

module/sharedLogic/
├── cs/
│   └── PyHost.cs                             v1-compatible v2 client/event/cancel support
└── pyhost/
    ├── pyhost.py                             protocol dispatcher/lifecycle
    ├── browser_runtime.py                    generic Camoufox backend
    ├── README.md                             canonical v1+v2 protocol
    └── tests/
        └── test_pyhost.py

setting/Components/
├── MultiSelect.xaml(.cs)
└── TagPicker.xaml(.cs)

tests/
├── Module.Mangareader.Downloader.Tests/      linked pure feature sources
└── Citadel.Uia/                              shared controls + live WPF contracts
```

The citizen project remains outside `Citadel.slnx`; pure linked-source tests may
be added to the solution like the existing Archive and Library test projects.
No new third-party archive or UI dependency is required.

## 12. Implementation sequence

### Phase 0 — clean baseline and live contract capture

1. Start only after the unrelated Reader-control WIP is committed, removed, or
   explicitly assigned; never overwrite it.
2. Freeze exact Comix sort labels/query keys, filter tag IDs, page contract, and
   public defaults as recorded fixtures without copying the extension runtime.
3. Capture `I'm Feeling Lucky` only if it is explicitly retained; otherwise
   leave it deferred.
4. Add characterization tests proving current local CBZ loading ignores a
   non-image `META-INF` entry and current Library refresh remains unchanged.
5. Use disposable library/job roots. Never mutate `D:\[ MANGA ]` during tests.

### Phase 1 — pure domain and source contracts

1. Implement normalized remote identities/models, immutable browse query, job
   state machine, recovery policy, and explicit source registry.
2. Implement Comix filter validation/serialization against captured fixtures.
3. Implement deterministic target mapping, filename, source-index, and collision
   policies as pure tests before network or WPF code.

### Phase 2 — PyHost v2 and generic browser backend

1. Characterize every v1 command and CamoProf caller before modifying transport.
2. Add v2 event dispatch, cancellation, browser/session registry, locking,
   destination containment, cleanup, and direct Camoufox backend.
3. Preserve v1 request/response/error/lifecycle behavior exactly.
4. Validate no orphan browser remains after shutdown, EOF, timeout, or parent
   termination.

### Phase 3 — Comix adapter and catalog data path

1. Implement lazy bootstrap and signed Browse/search/detail/group/chapter/page
   calls.
2. Normalize remote data without leaking Comix DTOs outside the adapter.
3. Implement Enter/Search-only author/artist lookup and explicit pagination.
4. Add recorded-contract fixtures and an opt-in logged-out live smoke; regular
   tests never depend on live Comix.

### Phase 4 — queue, staging, decoder, and publisher

1. Implement atomic queue/index stores and restart-to-Paused recovery.
2. Implement native streaming plus browser fallback, size/time bounds, byte
   format detection, hashes, and staging validation.
3. Implement header-driven Comix decode with synthetic `5x5` round-trip and
   captured algorithm-3 fixtures; unknown variants fail visibly.
4. Implement first-pass continuation, failed-only recovery, source-fallback
   state, completeness validation, and atomic CBZ publication.

### Phase 5 — missing shared controls

1. Implement `SettingMultiSelect` and `SettingTagPicker` with keyboard support,
   visible focus, empty/disabled/error states, fluid sizing, shared styling,
   cleanup, and UIA contracts.
2. Do not add provider-specific options or remote behavior to shared controls.
3. Reuse all existing shared fields/buttons/tables/scrollbars instead of local
   templates.

### Phase 6 — two-screen WPF integration

1. Add the one Downloader tab and small parent router.
2. Build Catalog screen, provider/filter composition, card grid, internal title
   detail state, one-group chapter selection, and queue badge.
3. Build Download List screen and exact Back-to-Catalog state restoration.
4. Prove opening/selecting/editing remains network-idle until an explicit
   action.
5. Keep Catalog responsive while queue events update through immutable
   summaries.

### Phase 7 — Library, fallback, cover, and cleanup

1. Complete explicit title-folder mapping and Library refresh bridge.
2. Complete cross-group candidate presentation and whole-chapter replacement.
3. Reuse Cover Builder for optional post-batch cover bake.
4. Delete superseded helpers, duplicate templates, compatibility wrappers, and
   temporary fixtures; do not retain a second download/archive path.
5. Update canonical docs/tasklist only after each gate passes.

### Phase 8 — full validation and handoff

1. Run pure Downloader, Archive, Library, PyHost v1/v2, shared component, and
   full solution tests with bounded parallelism.
2. Build MangaReader Debug and Release directly and verify isolated deployment.
3. Run live WPF Catalog/Download List QA at minimum, normal, and maximized
   window sizes.
4. Run one opt-in complete logged-out Comix chapter through a disposable local
   library, including app restart/pause/resume and final Reader open.
5. Do not commit, bump a version, build an installer, or publish a release
   unless separately requested.

## 13. Validation matrix

### 13.1 Automated gates

| Gate | Pass condition |
|---|---|
| explicit network triggers | open/provider/filter typing cause zero calls; Start/Load more/lookup/title/job cause expected calls only |
| query contract | all captured Comix defaults/options/IDs serialize exactly; invalid ranges never call provider |
| stale request | an older Browse/detail response cannot commit after a newer request |
| source identity | same chapter number across groups remains distinct |
| mapping/collision | same title text never auto-claims a folder; different identity never overwrites or becomes `(2)` |
| queue persistence | atomic round-trip, concurrent instances, corruption fallback, and in-flight-to-Paused restart pass |
| retry policy | first pass continues; only failed pages recover; bounds are enforced |
| source fallback | same-source page fallback is allowed; cross-group page mixing is impossible; whole-chapter confirmation is required |
| decoder | normal images, captured algorithm 3, synthetic round-trip, bad headers, unknown algorithm, and corrupt output pass/fail correctly |
| resume | only hash/manifest-valid staged pages are reused |
| publication | expected count/order/decode/ZIP/source manifest validated; no partial final path on any injected failure |
| Reader regression | non-image metadata is ignored; final CBZ opens; cover still selects the first supported image |
| PyHost v1 | all existing CamoProf commands and error/lifecycle contracts remain green |
| PyHost v2 | events, cancellation, path escape, response size, locking, timeout, EOF, disconnect, and orphan cleanup pass |
| shared controls | keyboard, focus, selection, chips, fluid layout, unload/cleanup, and UIA behavior pass |
| full regression | current `Citadel.slnx` suite passes after integration |
| citizen builds | MangaReader Debug/Release build and deploy with zero warnings/errors and no private shared Citadel DLLs |
| hygiene | `git diff --check`; no runtime profiles, pages, CBZ fixtures, secrets, caches, or queue data tracked |

### 13.2 Live WPF gates

| State | Required evidence |
|---|---|
| first open | Downloader shows no loading/network/browser activity |
| Advanced Filters | every public field is usable, fluid, keyboard accessible, and Reset performs no fetch |
| Start/pagination | explicit request, loading/error/empty/result states, volatile count, and Load more behave correctly |
| result/detail/back | fluid cards; detail replaces Catalog content; Back restores result and scroll without refetch |
| group/chapter | one group active; source variants never merge; local status is accurate |
| two-screen routing | Download List opens only by button; Back restores Catalog; no new tab/window |
| queue | progress remains responsive; pause/retry/remove/clear/open-folder actions reflect durable state |
| restart | active job reappears Paused and does not resume before user action |
| failed pages | warning appears while remaining pages continue; recovery touches failed pages only |
| fallback | alternate group requires confirmation and restarts the whole chapter |
| final publication | complete CBZ appears atomically, Library refreshes, Reader opens it, and no partial file is visible |
| cover | new/existing-folder defaults differ correctly; cover failure does not invalidate chapters |
| viewport | normal/maximized/minimum sizes avoid outer-scroll ownership conflicts and use shared auto-fading scrollbars |

Live Comix results are volatile and site changes are possible. A historical
fixture PASS cannot be reported as a current live PASS.

## 14. In-scope failure modes

| Risk | Required disposition |
|---|---|
| opening Downloader consumes resources | provider/browser activation is lazy and explicit |
| filter change races Browse | immutable query generation plus latest-request-wins commit |
| provider DTO leaks into app | adapter normalizes at its boundary |
| title name maps to wrong local folder | identity mapping requires first-use confirmation |
| one broken page blocks all later pages | FailedPageSet records it and first pass continues |
| partial chapter appears complete | final path exists only after full validation and atomic commit |
| fallback mixes translations | cross-group recovery always replaces the whole chapter |
| signed URL expires during resume | refresh same-source manifest, then validate identity before reuse |
| browser download escapes staging | canonical allowed-root containment in C# and Python |
| PyHost v2 breaks CamoProf | v1 characterization and regression gate before integration |
| browser remains after app exit | EOF/finally/graceful close/process-tree escalation contract |
| source site changes filter/API | Comix contract/version remains inside its adapter and fixtures fail visibly |
| shared UI gains Comix logic | shared controls accept generic items/state only |
| Catalog state is lost on queue view | separate child state retained by parent route host |
| update removes profiles/queue | all mutable state remains under LocalAppData |

## 15. Done definition

Done means the two routed child screens work through the boundaries above;
opening and editing remain network-idle until explicit actions; the complete
public logged-out Comix Browse/filter/detail/group path works; queue state
survives restart without auto-resuming; page failures continue then recover
only failed pages; a cross-group fallback can never mix individual pages; every
published CBZ is complete, validated, provenance-tagged, atomically committed,
visible in the local Library, and readable by the existing Reader; all shared
UI is reused or promoted without provider logic; PyHost v1 remains compatible;
and automated plus available live WPF gates pass before any PASS claim.

## 16. Explicit non-goals

- remote/streaming reading inside Citadel;
- auto-fetch on open, provider selection, filter edits, typing, or scroll;
- a separate Downloader module, nested Downloader tabs, modal catalog, or
  additional OS window;
- simultaneous merged group browsing or page-level mixing across groups;
- login-only Adjust listing behavior;
- implementing unverified `I'm Feeling Lucky` behavior without a live contract;
- importing Suwayomi, the APK, or extension code wholesale;
- modifying or depending directly on the current `stealthB` wrapper;
- changing machine DNS, hosts, certificate trust, or proxy settings;
- a Windows service, tray queue daemon, or downloads continuing after Citadel
  exits;
- loose JSON metadata inside manga title folders;
- silent filename `(2)` collision handling;
- adding a third-party UI or archive dependency; and
- commit, version bump, installer build, or release publication.

There are no remaining open product decisions in this Downloader plan. Any
change to screen count, trigger policy, source/group fallback, identity,
persistence, output integrity, browser backend, or local-folder mapping must be
reviewed as a plan change before implementation.
