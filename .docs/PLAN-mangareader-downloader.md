# PLAN: MangaReader local-first Downloader

Status: **IMPLEMENTATION-READY PLAN — all in-scope product, ownership,
integration, persistence and UI decisions are locked; this document is not
implementation approval**
Historical baseline: `main` at `ae450bc` on 2026-09-02.
Yuzskill reconciliation: 2026-09-05, inspected clean HEAD `b882b64`, version
`2.0.3`; no Downloader source was found. The ownership/shared-UI refactor and
interactive `SettingTable` sorting are committed. Recheck HEAD, status and
actual integration contracts before execution; historical paths are not a
substitute for live inspection.

This document turns the recorded Comix research and the user's selected product
decisions into the complete implementation contract for MangaReader's
Downloader. It supersedes the discovery-only Downloader notes in
`module/mangareader/TASKLIST.md`. It does not authorize implementation, a
commit, a release, changes to `stealthB`, or live mutation of the real manga
library.

## 0. Agent execution contract

Activate Yuzskill for the actual repository through `skills_begin`, read every
required skill completely, acknowledge its revision, then check `skills_status`.
If MCP is unavailable, follow the collection's file fallback at
`C:\Users\YUZHA\Yuzskill\AGENTS.md`. Relevant workflows: modular-architecture,
engineering-quality, planning-and-delivery, architecture-and-contracts,
shared-ui, verification-and-review, stack-guidance and citadel-project.
Read local `AGENTS.md`, `module/README.md`, and `.docs/SHARED-UI-BEHAVIOR.md`.
Changing protected agent configuration is not a prerequisite to reading skills
or executing already authorized source work; do not bypass its protection.

No product or architecture question remains for the executor. Routine coding
choices must follow the nearest live Citadel owner/example and the contracts in
this document without asking for repeated approval. The executor must not add a
dependency, shared primitive, framework, screen, service, persistence authority,
protocol generation, or alternate flow beyond those named here. A materially
changed repository contract or incompatible live provider contract blocks only
the affected phase and must be reported with evidence; it does not authorize a
redesign. Do not create another plan, TODO branch, or optional implementation.

This refinement preserves the product in section 3. It corrects implementation
directions that could create duplicate ownership or unnecessary infrastructure:
Queue is independent of both screens; feature protocol/decoding stays local;
PyHost receives only the bounded-response change; UI uses existing primitives
through feature-local composites; and phases verify coherent
increments rather than scaffold every listed file first.

Use this document for execution checkpoints, not a second parallel plan. After
compaction, reload active skills, this plan/checkpoint, git diff and the current
owner/callers before editing. Preserve unrelated WIP. Coordinate only actual
file/contract overlaps with the ownership-refactor plan; do not silently revert
its changes or require P0-P6 completion when the slice is independent.

For unfamiliar/version-sensitive APIs, use Context7 to resolve the actual
library then query the needed topic: Microsoft .NET Desktop Guide for WPF
resources/Dispatcher, .NET for streaming/cancellation, Playwright Python for
browser operations, and Camoufox if its version documentation is available.
Verify against local types/source. At this refinement the repo declares
`net10.0-windows`, SDK `10.0.400`, `camoufox==0.5.5`, and
`playwright>=1.51,<1.52`; these are pins, not proof of installed versions.
Do not upgrade to match a current snippet, substitute Node Playwright examples
for Python signatures, or assume Context7 contains version-specific Camoufox
docs. Use official versioned source when necessary. No library research, Comix
live test, or dependency upgrade was performed by this plan refinement.

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

The canonical historical evidence is
`.docs/RESEARCH-comix-downloader-2026-09-01.md`. It records, within the tested
logged-out sample (not a current live guarantee):

- direct Camoufox can render Comix and reach its signed `/api/v1` calls;
- ordinary Chromium/WebView-like execution failed on the secure bundle;
- catalog, search, title, group, chapter, and ordered page-manifest discovery
  are individually feasible;
- Comix chapter number alone is not a safe identity because multiple groups
  publish variants;
- two sampled `5x5`, algorithm-3 scrambled pages were decoded successfully;
- a transient ten-page CBZ fixture passed archive and image validation; and
- the then-tested `stealthB` wrapper failed before navigation; this does not
  establish its current state or make it a Downloader dependency.

The following remain implementation gates, not proven facts:

- a complete live chapter download;
- retry, recovery, pause, restart resume, and cancellation;
- long-running rate-limit and domain behavior;
- every scramble/encryption variant;
- the required transport capabilities, queue, persistence, and WPF behavior; and
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

The 2026-09-06 live Camoufox pass captured the current page-client requests,
not merely the labels rendered by the site. The following browse serialization
is therefore locked as provider evidence:

| Filter | Captured Comix request contract |
|---|---|
| search | `keyword=<text>` |
| type | repeated `types[]=manga|manhwa|manhua|other` |
| content rating | repeated `content_rating[]=safe|suggestive|erotica|pornographic` |
| release status | repeated `statuses[]=releasing|finished|on_hiatus|discontinued|not_yet_released` |
| demographic | repeated `demographics[]=<provider option id>` |
| genre and format | repeated `genres_in[]=<provider option id>` plus `genres_mode=and|or` |
| minimum chapter | `min_chap=<number>` |
| release year | `year_from=<number>` and/or `year_to=<number>` |
| author | lookup `tags/search?type=author&q=<text>&limit=20`, resolve through `tags/by-ids?type=author&ids=<id>`, then repeated `authors[]=<id>` |
| artist | lookup `tags/search?type=artist&q=<text>&limit=20`, resolve through `tags/by-ids?type=artist&ids=<id>`, then repeated `artists[]=<id>` |
| paging | `page=<number>&limit=28` |

The 13 captured sort choices serialize as follows:

| UI choice | Request |
|---|---|
| Best match | `order[relevance]=desc` |
| Latest update | `order[chapter_updated_at]=desc` |
| Recently added | `order[created_at]=desc` |
| Title (A-Z) / Title (Z-A) | `order[title]=asc|desc` |
| Year (newest) / Year (oldest) | `order[year]=desc|asc` |
| Highest rated | `order[score]=desc` |
| Most viewed 7/30/90 days | `order[views_7d|views_30d|views_90d]=desc` |
| Most viewed all time | `order[views_total]=desc` |
| Most followed | `order[follows_total]=desc` |

Provider IDs, rather than translated display labels, are serialized for
demographics, genres, formats, authors and artists. `Manga`, `Manhwa`, and
`Manhua` are types. `Pornographic` is a content rating. `Adult`, `Hentai`,
`Mature`, and `Smut` are genre/tag choices. `Uncensored` is not a captured
standalone filter and must not be invented; it can only be ordinary keyword
text unless the provider later exposes a real option.

### 2.1 Current-state reconciliation and locked preflight contracts

Live source inspection at HEAD `54b5a06` established four integration gaps.
They are resolved here as bounded implementation contracts, not left for an
executor to turn into new frameworks:

1. **Library root:** Library owns one module-lifetime `LibraryRootContext`.
   It uses the existing `LibraryPathStore`; Downloader consumes its normalized
   snapshot and change notification. No screen-control access, preference-file
   reread, or second root owner is allowed.
2. **Cover input:** Cover Builder owns one bake operation accepting a typed
   local-or-remote cover source. Remote fetch-to-Citadel-storage is an internal
   prerequisite of that operation. Both its existing View and Downloader call
   the same feature contract; neither duplicates fetch-before-bake policy.
3. **Shared transport:** retain the current inline, one-at-a-time PyHost v1
   request/response loop. Add only a bounded response envelope. Do not add
   `request.cancel`, a reader/worker queue, event stream, parallel dispatcher,
   backend framework, or v2 rewrite. C# caller cancellation discards its pending
   result; Python work ends through its existing bounded command timeout.
4. **Citizen wiring:** MangaReader follows the current CamoProf citizen pattern
   by linking shared C# sources and declaring its Downloader Python plugin name.
   This is mechanical project wiring, not a new deployment system.

Interactive sorting is already a shared `SettingTable` capability. Download
List enables it for Title, Chapter, Group, Status and Progress; Action remains
unsortable. Sorting is presentation-only and never changes durable queue order.
These contracts remove the prior preflight ambiguity;
Phase 1 begins independently, while the concrete dependencies are introduced
only in the phases that consume them.

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

`DownloaderView` owns only routing/composition and attaches children to their
lifetime. Each child owns its presentation state; Queue owns job state, not
either screen. Composition constructs only the module-lifetime services named
in sections 4-5 and must not
implement their policies. The route/event surface includes:

```text
CatalogScreen      -> OpenDownloadList
Catalog feature    -> QueueChapters command on the Queue contract
Queue              -> ChaptersQueued result
DownloadListScreen -> BackRequested
Queue              -> QueueSummaryChanged
```

Routing back to Catalog restores its provider, filters, result page, selected
title/group, selected chapters, and scroll position. Queue execution is not
owned by the visible child and continues while Catalog is shown. Hiding either
screen does not dispose the queue/provider; only the owning module lifetime
ends them. Additional internal controls are not new routed screens.

### 3.2 Lazy provider activation

- The provider dropdown is backed by a small explicit registry. It contains
  only Comix in the first implementation.
- Opening Downloader performs no remote request and starts no browser.
- Selecting Comix performs no remote request and starts no browser.
- Typing search text or changing a filter performs no remote request.
- `Start`, `Load more`, explicit Author/Artist lookup, title selection,
  explicit refresh, or a queued job are the only network triggers.
- There is no network polling timer, infinite-scroll fetch, provider prewarm,
  or automatic refresh. A bounded idle-cleanup timer/backoff for active work is
  allowed; it must not issue periodic remote requests or independent health probes.
- A provider browser/session is created lazily, retained only while an active
  request or job needs it, and released after bounded idle or module shutdown.
  The implementation must not leave an orphan Camoufox process.

### 3.3 Catalog toolbar and Advanced Filters

The Catalog action bar uses its available width instead of reserving space for
a separate `Idle - no request` label. In logical order it contains:

1. web-target dropdown;
2. search field;
3. compact direct filter dropdowns for Sort, Type, Release Status, Content
   Rating, and Genre/Format;
4. an `Advanced Filters` dropdown for Demographic, Minimum Chapter, Release
   Year, Author, Artist, genre AND/OR mode, and Reset;
5. one stateful `Start`/`Stop` button; and
6. `Download List (n)` with an active/paused/failed job count badge.

The action bar is fluid and may wrap its controls when the reader window is
narrow. It must not introduce horizontal clipping or a fixed-width desktop-only
layout.

`Advanced Filters` is a dropdown/popover composition, not a toggle that expands
a large inline panel and pushes the result grid. Multi-value dropdowns use a
checkbox per option and retain their draft selection until Reset or an explicit
change. Sort remains single-select. Minimum Chapter and Release Year remain
numeric inputs. Author and Artist remain explicit lookup fields.

The provider-owned Comix filter feature owns this input state and validation
and produces one immutable `ComixBrowseQuery`; its controls translate input
events. Neither the filter composition nor Catalog builds provider URLs.
Choosing a dropdown value edits draft state only and performs no Browse request.

The request button is also the visible catalog activity state:

- idle, ready, empty, successful, or failed terminal state: `Start`;
- active Browse request: `Stop`;
- after Stop is invoked while the existing inline PyHost command is reaching
  its bounded terminal response: disabled `Stopping...`;
- when that response settles: return to `Start`.

There is no separate idle/loading status text in the action bar. Provider
errors and validation messages remain local, non-destructive messages below the
bar; they are not encoded only in button text.

Rules:

- `Start` snapshots the current query and replaces the prior result set.
- A later `Start` is latest-request-wins. A stale response finishes its cleanup
  but cannot replace the current query, results, count, error, or pagination.
- `Load more` explicitly appends the next page for the current query snapshot.
- `Reset` restores provider defaults and does not fetch.
- Author and Artist fields fetch `tags/search` only on Enter or an explicit
  Search action; they never query on every keystroke. A selected result is
  resolved by provider ID before it enters the Browse query.
- Genre/format options use the 31 genres and 9 formats captured from the live
  provider UI. Multi-selection serializes provider IDs through `genres_in[]`;
  AND/OR serializes through `genres_mode`. Unknown display text is never sent
  as a provider value.
- Minimum chapter and year ranges validate locally. Empty means unset; invalid
  ranges prevent `Start` and show field-local errors.
- `Adjust listing` is omitted while the Downloader remains logged-out.
- `I'm Feeling Lucky` is not implemented until its live request/result contract
  is captured in a future plan. Its absence does not block this implementation.

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

Library remains the sole owner of that root. At MangaReader composition time,
create one module-lifetime `LibraryRootContext` backed by the existing
`LibraryPathStore`. The Library feature restores it once and commits a new
normalized value only after a successful scan, preserving the current
`LibraryScanPersistence` rule. The context exposes only a current immutable
snapshot and change notification needed by consumers. MangaReader parent
constructs and injects it, but owns no root policy. Downloader must not instantiate
another `LibraryPathStore`, read `library-path.txt`, or inspect a named
`LibraryView` control.

A remote title must map to one local title folder before its first job is
queued:

1. an existing source-identity mapping is reused when its folder still exists;
2. otherwise the user receives a suggested sanitized folder name;
3. a same-named existing folder is never claimed automatically; the user must
   explicitly choose that folder or create a distinct one; and
4. the confirmed mapping is persisted outside the manga folder.

Display-title equality alone is never evidence that a remote and local title
are the same.

Root/mapping are obtained through feature contracts, not named controls or a
second reader of Library preference files. Each job snapshots its confirmed
root and target; changing Library selection cannot silently redirect an active
job. A mismatch before publish pauses that job for explicit reconciliation.
Use a concrete context first; do not add an interface, repository, or event bus
until a real alternate implementation or consumer contract requires one.

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

Download List reuses `SettingTable`. Its default view follows the durable queue
order in `queue.json`. Interactive sorting is enabled for Title, Chapter,
Group, Status and Progress and operates on the WPF presentation view only. It
must not reorder jobs, rewrite `queue.json`, or affect runner scheduling. The
Action column is never sortable. The selected sort is session-only and resets
to durable queue order when the module is recreated.

### 3.7 Retry, failed-page recovery, and source fallback

The remote ordered page manifest is the completeness authority.

First pass:

1. attempt each expected page;
2. after the initial failure, retry that page exactly three times unless an
   attempt succeeds, waiting 1, 2 and 4 seconds before those retries;
3. if it still fails, record a warning and add its identity to
   `FailedPageSet`;
4. continue processing the remaining pages instead of blocking the chapter;
   and
5. never mark or publish the chapter as complete while the set is non-empty.

Recovery pass:

1. refresh the same chapter manifest/signed URLs;
2. retry only `FailedPageSet`, again using one initial recovery attempt followed
   by retries after 1, 2 and 4 seconds unless an attempt succeeds;
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

### 3.8 Local output and terminal publication

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
- Successful atomic publication, durable queue/index commit, and staging cleanup
  are the terminal Downloader effects. The job then returns as `Completed`.
- Downloader does not refresh or scan Library, emit a post-publication event to
  the parent, or invoke Cover Builder. Library refresh remains an explicit user
  action; Cover Builder remains an independent feature.

## 4. Parent/children architecture

```text
MangaReader composition / lifetime
└── Downloader feature
    ├── DownloaderView                     route host, no job policy
    │   ├── CatalogScreen                  browse/detail presentation
    │   └── DownloadListScreen             queue presentation only
    ├── Catalog feature                    query/detail/selection state
    ├── Queue feature                      sole job state/effect owner
    │   └── runner + recovery + publication
    └── source registry                    provider + filter contributions

Library feature ── LibraryRootContext ──> Downloader target snapshot
```

Arrows here indicate ownership, not new required classes. Use the existing
catalog/context/command/event conventions; no extra event bus or container.
Parent lifetime holds the queue independently of route visibility. Children
never reach into named controls or mutable collections on siblings. The narrow
context/contracts expose only what their actual consumers need:

- route commands;
- read-only queue summary and change events;
- current Library-root snapshot/change notification plus mapping operations;
- source-registry access;
- lifecycle cancellation (Dispatcher remains in the WPF presentation adapter); and
- typed `QueueChapters` commands.

The Library integration shape is fixed. `MangaReaderView` owns one
`LibraryRootContext` field and passes it to `LibraryView` and `DownloaderView`
after `InitializeComponent` but before either child receives `Loaded`, following
the existing `Use...` composition pattern. The context owns the existing `LibraryPathStore` and
`LibraryScanPersistence`, and exposes an immutable empty-or-valid root snapshot.
A successful scan
sets the in-memory root to its captured path and emits `RootChanged`; its existing
save result still controls the persistence warning. A failed or cancelled scan
changes neither root nor storage. Downloader only reads/snapshots the root; it
cannot save the root, request a scan, call `LibraryView`, or invoke Cover Builder.

Queue logic does not reference WPF; DownloadListScreen lives beside it but
only binds snapshots/commands. Catalog does not mutate Queue collections.
Library does not call Comix. Completion is terminal inside Queue after durable
publication bookkeeping and staging cleanup; it does not cross into another
feature.
The parent only constructs/injects these children and routes their messages; it
does not become the owner of Library, Cover, provider, queue, or transport state.

## 5. Feature ownership

| Feature | Owns | Must not own |
|---|---|---|
| Downloader parent | route and child lifetime | filters, jobs, API, files |
| Source registry | explicit provider list/capabilities | runtime discovery/reflection |
| Comix filter feature | input state/validation; panel binds and composes shared controls | URL/API construction |
| Catalog coordinator | query generation, result/detail state, stale-response guard | local CBZ mutation |
| Comix source adapter | Comix routes, signed calls, parsing, normalization | WPF, queue, archive writing |
| Title selection | active group, chapter selection, target mapping request | download execution |
| Queue coordinator | durable job intent and state transitions | network/browser implementation |
| Job runner | concurrency, pause, retry, recovery orchestration | WPF controls |
| Downloader transport | streaming and owned browser/session lifecycle | Comix parsing, queue retry policy, CBZ structure |
| Comix page decoder | Comix scramble header/algorithm handling | queue or publication |
| CBZ publisher | completeness check, package validation, atomic commit | provider UI/API |
| Source mapping | confirmed remote identity to target-folder mapping | Library preference storage, screen controls |
| Library root context | one normalized module-lifetime root snapshot/change signal, backed by existing Library persistence | remote browsing, target mapping, queue jobs |
| Cover Builder | typed local/remote source resolution and one bake operation over the existing archive flow | Downloader queue/retry policy |

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
headers remain in `Features/Downloader/Sources/Comix/`. `ComixFilterPanel` is colocated
there because its fields are also provider-specific, but it talks to Catalog
only through the generic filter-contribution contract and cannot call the
adapter directly.

The interface above is a boundary sketch, not an instruction to create every
DTO/class in advance. The filter contribution yields a UI-free query value
owned by its registered source. Catalog/Queue never switch on Comix types.
Manifest page transforms are also source-owned: the pipeline invokes a source
operation/contribution and receives validated output; it does not inspect
scramble headers or instantiate ComixPageDecoder. Keep that policy out of
generic PageTransport and PyHost. Add only the small callable seam needed by
this real consumer, not a separate decoder registry or backend framework.

Remote identity is at least:

```text
provider ID + title HID/internal ID + chapter remote ID + group ID
```

Chapter number is display/matching metadata, never the sole key.

## 7. Browser and transport capability contract

### 7.1 Browser backend

Use direct Camoufox behind the Downloader-owned browser adapter, based on the
historical Comix evidence. Reuse existing mechanisms where compatible;
`stealthB` is not copied or referenced. Do not require an `IBrowserBackend`
hierarchy or implement a hypothetical second backend merely to satisfy this
tree. Catalog/Queue depend on the source/transport contract, not browser internals.

Browser responsibilities are deliberately narrow:

- open the provider page/runtime needed for secure bootstrap;
- expose signed API/session data to the adapter;
- perform bounded browser-context fetch fallback when native transport cannot;
  and
- close cleanly on idle, cancellation, EOF, and process shutdown.

It never renders a Citadel streaming reader or owns downloaded chapter state.

### 7.2 Reuse PyHost; extend only a demonstrated gap

Reuse `module/sharedLogic/cs/PyHost.cs` request/response transport and the
existing Python `register_commands`/lifecycle-hook plugin mechanism. Downloader
owns its C# command/payload adapter and Python browser/provider plugin. Shared
PyHost must not acquire Comix URLs, download-job state, retries, filter DTOs,
or feature command wrappers. Never reference the sibling CamoProf
BrowserSessionCoordinator or reuse its Google browser/profile/process.

The MangaReader lifetime owns its lazily started PyHost instance and resources;
sharing transport code does not mean sharing CamoProf's running instance.
Reuse RuntimeSetup and deployed payload conventions rather than copying a
runtime installer. Startup must work without opening CamoProf's Runtime screen.

Required outcomes, implemented incrementally with their consuming slice:

- request IDs, one terminal response, existing timeouts, and a `4 MiB` maximum
  UTF-8 response line enforced before Python writes and checked again by C#;
  replace the C# side's unbounded line read with a bounded newline reader so the
  limit applies before full allocation. Overflow returns a stable
  `RESPONSE_TOO_LARGE` error rather than parsing an unbounded envelope;
- native page-byte streaming and progress stay in C# and outside NDJSON; no
  protocol event stream is part of the initial implementation;
- preserve the current inline FIFO dispatcher. C# caller cancellation removes
  the pending request and ignores its late response; it does not claim to cancel
  the Python handler. No new command is issued for that job while its current
  Python command is still completing;
- Downloader command timeouts are fixed at 120 seconds for Camoufox bootstrap,
  45 seconds for catalog/detail/lookup/manifest and browser fallback, and 30
  seconds for each native HTTP page attempt;
- C# and Python enforce the same per-command timeout. Either timeout result can
  win that race; the C# pending entry completes once, and a later response with
  the removed request ID is ignored. This is the intended single-terminal
  behavior—do not add timeout synchronization, acknowledgement, or another
  response path;
- an unused Downloader browser/session is released after 60 seconds. Module
  shutdown uses existing EOF/graceful-close cleanup and escalates only against
  the Downloader-owned process tree;
- URL validation and destination containment for browser writes.

Implement only the bounded response reader/writer that current shared transport
lacks. Document that additive protocol limit beside the existing protocol and
preserve request ordering, one terminal outcome, and all CamoProf wire/error/
lifecycle semantics. Mid-request Python cancellation, a broad "PyHost v2"
replacement, second registry, worker queue, event stream or lease framework is
outside this plan.

Browser operations cover open/navigation, bounded JSON evaluation, streamed
fetch and close as actually needed. Command names/payloads belong to the
Downloader plugin adapter, not a speculative universal browser API.
Browser fetch writes directly to allowed staging and returns headers, byte
count and hash/evidence. Do not encode chapter/page bytes into NDJSON.

Account commands retain `CITADEL_CREDENZ` validation. Downloader validates its
own absolute browser/download roots, with profiles under
`%LocalAppData%\Citadel\MangaReader\browser\<provider>`, never Google profiles.
Readiness/capabilities distinguish missing runtime, missing plugin and failed
provider bootstrap; opening the tab must not auto-install or start a browser.

### 7.3 Hybrid transport

The browser negotiates the secure/signed session. C# then uses bounded native
HTTP streaming as the primary page path, including the required Referer and
session headers. Browser-context fetch is the fallback for DNS, TLS route,
cookie, fingerprint, or provider rejection that the browser can satisfy.

Every page attempt follows one fixed transport rule: try native streaming first;
then use at most one browser-context fetch for that attempt when native fails on
DNS/TLS/connectivity, returns `401`/`403`, or returns an HTML/non-image challenge
payload. `404`/`410`, an unknown scramble algorithm, and corrupt decoded image
bytes do not invoke browser fallback; they enter normal failed-page recovery.
`429` and `5xx` use the retry schedule in section 3.7 without switching group or
changing system network settings. A successful fallback still passes the same
byte-format, transform, hash and image validation as native output.

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
  -> staging cleanup
  -> Completed (terminal)
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
Pausing
Paused
Failed
Completed
```

`Completed` means publication and durable completion are confirmed. Before
atomic rename, cancellation/failure leaves no new final CBZ. After rename but
before queue/index save, a valid final CBZ can exist while durable job state
still says Publishing. Reconcile that window using the existing source
manifest/identity; never delete or re-download a valid file just to make the
state diagram true. Restart still pauses unfinished work, with no automatic
network activity; local reconciliation recognizes an already completed job.

### 8.2 Concurrency and cancellation

- Initial default: one active chapter and two concurrently streamed pages.
- Native page operations receive the job `CancellationToken`; each attempt has
  the 30-second timeout and retry schedule fixed in section 3.7.
- Pause changes the job to `Pausing`, stops scheduling new pages, cancels native
  C# HTTP/file work, and waits for any already-written PyHost command to reach
  its bounded terminal response. It then validates completed staging files,
  commits queue state, and reports `Paused`.
- Pause never terminates PyHost or Camoufox. Process-tree termination is only an
  app-shutdown orphan-safety escalation against the Downloader-owned process;
  it must never target CamoProf or a process not created by Downloader.
- Manual Resume revalidates staging before reuse.
- Queue order is stable; retrying one job cannot silently reorder other jobs.

### 8.3 Staging and resume

```text
%LocalAppData%\Citadel\MangaReader\downloads\jobs\<job-id>\
├── job.json
├── manifest.json
└── pages\
```

These are runtime files, not manga-library sidecars. `queue.json` is the sole
durable owner of job identity, order and state. Every staged job has an immutable
`manifest.json` for its resolved remote manifest and a `job.json` staging journal
for per-page records only; neither duplicates queue state. Every staged page records
ordinal, remote page identity, expected/observed size when available, content
hash, detected format, transform state, and validation state. Resume reuses a
page only when its record and bytes validate against the current immutable job
manifest.

If a refreshed provider manifest changes identity/order, the job does not mix
old and new manifests. It becomes a visible conflict requiring a restart or a
new job.

### 8.4 Atomic publication

The publisher writes a unique `.partial.<guid>.tmp` ZIP payload in the final
target folder so final rename stays on the same volume without using a
discoverable chapter extension. Before commit it verifies:

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

Publication and queue JSON are not one atomic transaction. Persist the intended
job identity/target before rename; after interruption, validate the final file's
provenance/completeness and reconcile the existing job before retrying effects.
Reuse the job/store/source manifest already specified, not an extra transaction
database. Verify the temporary name against the current scanner: a
`.partial.<guid>.cbz` would still match its chapter-extension filter. Do not
broaden Library scanning rules or archive infrastructure to hide a temporary
file that the publisher can keep non-discoverable itself.

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
published remote chapter identities. Each staged job's `job.json` stores only
page completion/validation records. All mutable JSON files use bounded fail-soft
reads, schema versions, normalized-path process-wide gates, unique same-folder
temporary files, atomic replacement, and cleanup in `finally`.

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

Also reuse `SettingTabs`, `SettingViewport`, `SettingListStyle` and
`SettingCardStyle` for the tab, finite viewport, selectable lists and surfaces.
Shared styles and each control's documented behavior pair remain canonical.
`SettingTable` already provides opt-in interactive sorting. Download List enables
that capability for Title, Chapter, Group, Status and Progress and must not
create a local header/sort control. Action remains disabled for sorting. The
sorted collection view is disposable presentation state and never becomes queue
persistence or scheduling policy.

Multi-select and searchable resolved-tag selection are feature-local composites
inside `Features/Downloader/Sources/Comix/`. Build them only from existing
`SettingField`, `SettingButton`, `SettingToggle`, `SettingListStyle`,
`SettingCardStyle` and `SettingScrollViewerStyle`. The composite owns arrangement
and selected provider keys while delegating focus, keyboard, scrolling and
rendering behavior to those shared controls. This plan adds no shared primitive,
style or template and makes no change under `setting/Components`.

| Downloader field | Locked presentation |
|---|---|
| provider, group | standard `ComboBox` with `SettingComboBoxStyle` |
| sort | compact single-select dropdown |
| rating, type, demographic, status | compact multi-select dropdown with one checkbox per option |
| genre/format | compact searchable multi-select dropdown with one checkbox per resolved provider option and AND/OR control |
| minimum chapter, year from/to | numeric-validated `SettingField` inside `Advanced Filters` dropdown |
| author/artist | `SettingField`, explicit lookup action, and selectable resolved results inside `Advanced Filters` dropdown |
| Start/Stop, Reset, Load more, navigation/actions | `SettingButton` |
| queue rows/actions | `SettingTable` plus `SettingTableActions` |

The compact filter UI is a combo composition of existing shared primitives.
Implementation must first audit the current shared inventory for a checkbox
dropdown composition. If no such composition exists, a feature-owned combo may
compose the existing popup/dropdown, checkbox/list, field, scroll, and button
primitives; it may not introduce a new low-level control or clone shared visual
behavior.

Every provider option record exposes a non-empty `DisplayName`. Every
object-backed filter/lookup item binds an accessible name to `DisplayName` while
retaining the shared item template and its keyboard/focus/selection behavior.
Do not copy or override the shared template, and do not rely on record
`ToString()` for accessibility. This is feature-owned semantic metadata, not a
new shared visual/input behavior.

Shared controls/combos know nothing about Comix, genres, ratings, authors,
artists, providers, or requests. Numeric fields use `SettingField` with
feature-owned validation. Author/Artist lookup presentation stays with the
provider/Catalog feature; share only a proven provider-neutral composition.

Catalog uses one feature-owned `RemoteTitleCard` composed from
`SettingCardStyle` plus existing shared controls. Do not extract or modify
`MangaTitleCard`; local and remote models/actions remain separate, and no adapter
may fill a local `MangaTitle` with remote placeholders. Do not redesign local UI
to make Downloader possible.

## 11. Target file tree

This is the planned production owner set. Keep small records/enums beside their
owner and do not split Coordinator/State/Policy/DTO into one file each. Do not
add another layer, registry, repository, backend abstraction or empty interface.
Focused test files stay inside the single named Downloader test project and are
grouped by behavioral invariant. A genuinely incompatible current contract is
reported as a blocker instead of expanding production architecture.

```text
module/mangareader/
├── MangaReaderView.xaml(.cs)                 add one Downloader tab only
├── Module.Mangareader.csproj                 shared C# link + plugin name wiring
├── Features/Downloader/
│   ├── DownloaderView.xaml(.cs)              parent/router
│   ├── DownloaderContract.cs                 narrow context, routes, commands/events
│   ├── Catalog/
│   │   ├── CatalogScreen.xaml(.cs)
│   │   ├── RemoteTitleCard.xaml(.cs)          feature composite of shared UI
│   │   └── CatalogFeature.cs                  state, stale guard, detail/selection
│   ├── Queue/
│   │   ├── DownloadListScreen.xaml(.cs)
│   │   ├── DownloadQueueFeature.cs            job state, scheduling, retry/recovery
│   │   ├── DownloadQueueModels.cs             job/page identity and snapshots
│   │   ├── DownloadQueueStore.cs
│   │   ├── ChapterDownloadPipeline.cs        owned by Queue, not a global pipeline
│   │   ├── PageTransport.cs
│   │   └── CbzChapterPublisher.cs
│   ├── DownloadSourceIndex.cs                mapping/publication index owner
│   ├── DownloaderPyHostClient.cs             feature command/payload adapter
│   ├── mangareader_downloader/               owned Python plugin; registered lazily
│   │   └── plugin.py                         commands and owned browser lifecycle
│   └── Sources/
│       ├── MangaSourceContracts.cs            IMangaSource + normalized models
│       ├── MangaSourceRegistry.cs
│       └── Comix/
│           ├── ComixSource.cs                 query/contracts/adapter
│           ├── ComixFilterPanel.xaml(.cs)
│           └── ComixPageDecoder.cs
├── shareLogic/Archive/                       reuse lock/validation/replacement, no rewrite
├── CoverBuilder/
│   └── ...                                    independent feature; no Downloader call
└── Library/
    ├── LibraryView.xaml.cs                    consume context before Loaded
    ├── LibraryRootContext.cs                 one root snapshot/change signal
    ├── LibraryPathStore.cs                    existing persistence mechanism
    └── LibraryScanPersistence.cs             existing successful-scan rule

module/sharedLogic/
├── cs/
│   └── PyHost.cs                             reuse transport; minimal additive gap only
├── pyhost/
│   ├── pyhost.py                             existing dispatcher/plugin/lifecycle owner
│   └── README.md                             update actual protocol additions only
└── tests/test_pyhost.py                      existing shared host regression owner

tests/
└── Module.Mangareader.Downloader.Tests/      linked pure feature sources
```

The citizen project remains outside `Citadel.slnx`. Add one
`Module.Mangareader.Downloader.Tests` linked-source project to the solution for
the pure Downloader contracts; do not duplicate existing Archive, Library,
shared PyHost or shared-UI tests.
No new third-party archive or UI dependency is required. The feature-owned
Python package uses the existing `Citizen.targets` plugin deployment convention
(`Features/*/<package>/*.py`) and a module project registration; C# shared-source
inclusion follows the current CamoProf citizen pattern. Before the first PyHost
consumer, add `..\sharedLogic\cs\**\*.cs` to `Compile` and set
`PyhostPluginName` to `mangareader_downloader`; do not copy those shared sources
or invent a deployment target. Verify both build and packaged payload. No copy
of CamoProf internals, deployment framework, unconditional
browser bootstrap, or provider-specific file in shared pyhost is required.

## 12. Implementation sequence

Each phase below is a coherent consumer path, not a demand to scaffold all
models, controls and runtime capabilities before showing a result. Reuse
existing checks; record `phase | files/owners | outcome | checks | pending` in
this document. Do not repeat passing checks on unchanged inputs or create a
second tracker. Gate only the phase that actually depends on a blocked contract.

Testing stays proportional: reuse Archive, Library, shared PyHost and shared-UI
coverage; add one focused Downloader test per distinct invariant, using theories
for data variants. Do not add source-text tests, private call-sequence tests,
duplicate shared-control tests, broad snapshot fixtures, performance harnesses,
or a second test project. Run the narrow affected project during each phase and
the full solution once in Phase 8.

Once implementation is explicitly authorized, execute Phases 0-8 continuously.
A failed focused check is diagnosed and fixed inside its current owner before
continuing; it is not a reason to request a new design decision. Compatible path
or signature drift is handled mechanically from the live owner. Stop only when
the fixed Comix public contract is externally unavailable or current source makes
a locked contract impossible without an out-of-scope change; report that exact
blocker and leave completed independent phases intact.

### Phase 0 — scoped baseline and contract capture

Owner: this plan, current integration contracts and research fixtures.
Inspect live paths/callers, shared UI inventory and transport capabilities.
Preserve unrelated WIP; coordinate overlapping edits, not a mandatory clean
worktree/commit. During authorized implementation, revalidate Comix query keys,
IDs/defaults and the page contract against current logged-out evidence. Keep
Lucky deferred. Use `https://comix.to/title/dy88-the-novels-extra?group_id=9897`
as the fixed detail/group smoke target and use disposable library/job roots,
never the real collection.

Gate: owner/consumer map and exact gaps are recorded; dated research is not
claimed as live PASS. Reuse or add only missing characterization for non-image
metadata and partial-file discovery before publication is implemented.
The four preflight contracts in section 2.1 are already decided; do not reopen
them as an architecture phase unless current source has materially changed.

### Phase 1 — minimal catalog contract and idle screen

Owner: Downloader composition, Catalog and source registration.
Implement only identity/query/result types needed for Browse, one explicit
source registration, and the idle Catalog composed from shared controls.
Filter validation belongs to its feature; views remain adapters. Do not create
the entire queue/decoder hierarchy here. Build the feature-local filter
composites and `RemoteTitleCard` exactly as section 10 defines; do not wait for
or change a shared-control framework. Provider option records and their derived
item-container accessibility binding are created with the first object-backed
list, not deferred to final UI cleanup.

Gate: one tab, no remote activity on open/provider/input changes, local query
validation and stable child-owned state. Check the actual UI/feature boundary
with a controlled source and record live UI evidence separately.
Dependency: Phase 0.

### Phase 2 — explicit Browse through a feature-owned browser adapter

Owner: Comix source, Downloader C#/Python adapter and its process lifetime.
Wire Start -> lazy Camoufox -> one normalized Browse response -> Catalog.
First apply the mechanical MangaReader citizen wiring from section 11. Reuse
transport/plugin installation and add only the bounded response change specified
in section 7.2. Preserve the inline PyHost loop; do not implement cooperative
Python cancellation, a v2 backend/registry rewrite, or an event stream. Timeout,
root separation and disposal must work for this real path before extension.

Gate: Start succeeds or reports a bounded error; latest-request-wins and
shutdown/EOF cleanup hold. Run the existing shared PyHost and affected CamoProf
transport checks because the response reader changes. Tests use recorded/synthetic data, followed by one logged-out live
Browse smoke with no download or mutation. Provider unavailability is reported
as LIVE PROVIDER VERIFICATION PENDING and does not authorize contract changes.
Dependency: Phase 1.

### Phase 3 — catalog, filters and title/group selection end to end

Owner: Catalog/TitleSelection and Comix source/filter feature.
Extend the working Browse path to explicit pagination, Enter/Search-only
lookups, detail/group/chapter/manifest discovery and Back restoration. Keep
provider payloads local and preserve one active group. Add only contracts with
actual consumers. Introduce the single Library-owned `LibraryRootContext` and
establish confirmed folder mapping from its snapshot, never from the View or
preference file directly. Replace the large inline Advanced Filters panel with
the compact direct/advanced dropdown action bar from section 3.3. Implement the
captured 2026-09-06 query keys and sort map exactly; do not retain the current
`UncapturedFilters` refusal for a contract now proven live. Complete provider
filter combos from section 10 using existing shared controls only, and persist
confirmed mappings through `DownloadSourceIndex` before queueing is enabled.

Gate: public logged-out filter/selection path works, stale responses cannot
commit, remote models never masquerade as local title/card models, and the real
Catalog proves meaningful accessible names, multi-checkbox state,
keyboard/focus/selection/disabled/fluid-layout behavior without changes under
`setting/`. Filter edits and Reset make no Browse call; Start performs exactly
one query snapshot; Stop visibly transitions through the existing bounded
inline-command cancellation boundary and returns to Start.
Dependency: Phase 2.

### Phase 4 — one queued chapter through atomic publication

Owner: Queue, its pipeline/store/publisher and source-owned decoder.
Deliver queue intent -> staged pages -> validated CBZ for one
selected chapter using native streaming and the fixed browser fallback. Use
existing archive locks/validation and source-owned transforms; do not create a
parallel downloader/archive implementation. Reconcile crash-after-rename before
retrying publication.

Gate: a complete chapter opens locally; missing/unknown/corrupt pages never
publish. Focused checks cover identity, path containment, collision, archive
validation, atomic publication and crash-after-rename reconciliation.
Dependency: Phase 3. Queue progress/actions are consumed in Phase 6.

### Phase 5 — persistent queue, pause and failed-page recovery

Owner: Queue and its existing pipeline/store boundaries.
Add atomic queue/job-journal persistence, restart-to-Paused, `Pausing` behavior,
two-page native concurrency, fixed retry schedule, failed-page-only recovery,
manifest-change conflict, and the alternate-source candidate state. Reuse the
Phase 4 pipeline and publisher; do not add a second runner, scheduler, archive
path or persistence authority.

Gate: restart never auto-resumes; Pause reaches `Paused` through the fixed
boundary; retries touch only failed pages; matching valid staging is reused;
changed manifests conflict visibly; persistence failures never publish partial
or ambiguously owned state.
Dependency: Phase 4.

### Phase 6 — Download List and two-screen routing

Owner: DownloadList presentation and Downloader route host.
Connect queue commands/snapshots to Download List, accessible only from the
Catalog button. Back restores Catalog state without fetching. Route changes
never dispose Queue; immutable progress updates are marshalled by the WPF
adapter, not by WPF code inside Queue. Verify all specified queue actions.
Enable the five sortable columns fixed in sections 2.1/3.6 using `SettingTable`;
verify that sorting changes only the presentation view while persisted queue
order and scheduling remain unchanged.

Gate: background job continues while Catalog is visible; screens do not share
mutable state or call sibling internals; open/select/type remain network-idle.
Dependency: Phases 3-5.

### Phase 7 — fallback and integration cleanup

Owner: Queue/Comix fallback.
Finish alternate-group confirmation and whole-chapter replacement. Preserve
independent source identities. Publication must remain terminal and must not
trigger Library, Cover Builder, or parent behavior.
Delete directly superseded paths/temporary harnesses after caller checks;
retain the small sanitized regression fixtures actually used by tests.

Gate: no mixed-group archive, no silent folder claiming, no private sibling
feature access, and no leftover alternate download path.
Dependency: Phases 5 and 6.

### Phase 8 — integration validation and handoff

Build MangaReader, CamoProf and Shell in Release and verify MangaReader's isolated
deployment including its Python plugin. Reuse per-phase test evidence, then run
the solution suite once with bounded parallelism for final shared integration.
Do not add a Debug build or repeat the full suite without a concrete failure to
diagnose.

Live gates: Catalog/Download List at minimum, normal and maximized sizes; one
complete logged-out Comix chapter in a disposable library, restart/pause/resume
and Reader open. Use the lowest-page-count public chapter found under the
captured test title/group; never use the real manga collection. Run one CamoProf
smoke because the shared response reader changed. Unavailable live evidence
remains PENDING, not an implied PASS and does not authorize a redesign.
Do not commit, bump, build an installer or publish unless separately requested.
Dependency: all required outcomes above, not a prescribed number of files/tests.

## 13. Validation matrix

### 13.1 Automated gates

| Gate | Pass condition |
|---|---|
| explicit network triggers | open/provider/filter typing cause zero calls; Start/Load more/lookup/title/job cause expected calls only |
| query contract | all captured Comix defaults, 13 sorts, repeated type/rating/status/demographic/genre/author/artist IDs, genre mode, minimum chapter and year range serialize exactly; invalid ranges never call provider |
| Catalog action state | no idle status label; button is Start while terminal, Stop while Browse is active, disabled Stopping while an inline command settles, then Start again |
| stale request | an older Browse/detail response cannot commit after a newer request |
| source identity | same chapter number across groups remains distinct |
| mapping/collision | same title text never auto-claims a folder; different identity never overwrites or becomes `(2)` |
| Library root ownership | Library restores/commits one context; Downloader never rereads storage/control state; a queued job retains its captured target |
| queue persistence | atomic round-trip, concurrent instances, corruption fallback, and in-flight-to-Paused restart pass |
| Download List sorting | visual order may change, but `queue.json`, job identity and runner order remain unchanged; Action is not sortable |
| retry policy | first pass continues; only failed pages recover; bounds are enforced |
| source fallback | same-source page fallback is allowed; cross-group page mixing is impossible; whole-chapter confirmation is required |
| decoder | normal images, captured algorithm 3, synthetic round-trip, bad headers, unknown algorithm, and corrupt output pass/fail correctly |
| resume | only hash/manifest-valid staged pages are reused |
| publication | expected count/order/decode/ZIP/source manifest validated; no partial final path on any injected failure |
| Reader regression | non-image metadata is ignored; final CBZ opens; cover still selects the first supported image |
| PyHost v1 | all existing CamoProf commands and error/lifecycle contracts remain green |
| Downloader transport capabilities | 4 MiB response bound, unchanged inline FIFO ordering, client-side late-response discard, fixed timeouts, path containment, EOF/disconnect and owned-process cleanup pass; no cancel command, events or v2 rewrite |
| timeout race | C#-first and Python-first timeout paths each produce one caller-visible terminal outcome; late removed IDs are ignored without a second response path |
| Cover contract | local and remote inputs reach one bake operation; remote fetch failure never enters archive mutation; both View and Downloader use the same policy owner |
| shared controls | keyboard, focus, selection, fluid layout, unload/cleanup, and UIA behavior pass; object-backed options expose `DisplayName`, never record `ToString()` |
| full regression | current `Citadel.slnx` suite passes after integration |
| citizen builds | MangaReader, CamoProf and Shell Release builds pass; MangaReader deploy contains its plugin and no private shared Citadel DLLs; no Debug build required |
| hygiene | `git diff --check`; no runtime profiles, pages, CBZ fixtures, secrets, caches, or queue data tracked |

### 13.2 Live WPF gates

| State | Required evidence |
|---|---|
| first open | Downloader shows no loading/network/browser activity |
| Catalog action bar | available width is used; direct filter dropdowns plus Advanced Filters dropdown remain fluid without pushing the result grid or clipping at normal/maximized sizes |
| Advanced Filters | multi-value dropdowns expose checkboxes; Sort stays single-select; numeric and lookup fields remain typed; Reset performs no Browse fetch |
| filter accessibility | UIA exposes each option's `DisplayName` and each action's explicit accessible name in every object-backed list |
| Start/pagination | explicit request, loading/error/empty/result states, volatile count, and Load more behave correctly |
| result/detail/back | fluid cards; detail replaces Catalog content; Back restores result and scroll without refetch |
| group/chapter | one group active; source variants never merge; local status is accurate |
| two-screen routing | Download List opens only by button; Back restores Catalog; no new tab/window |
| queue | progress remains responsive; pause/retry/remove/clear/open-folder actions reflect durable state |
| pause boundary | native work cancels; an in-flight Python command shows Pausing and reaches Paused after its bounded response without killing the browser |
| restart | active job reappears Paused and does not resume before user action |
| failed pages | warning appears while remaining pages continue; recovery touches failed pages only |
| fallback | alternate group requires confirmation and restarts the whole chapter |
| final publication | complete CBZ appears atomically, job becomes Completed, no partial file is visible, and no automatic Library scan starts |
| cover | catalog covers render through the shared MangaTitleCard; Downloader publication does not invoke Cover Builder |
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
| partial chapter appears complete | incomplete temporary files are not discoverable; final path exists only after full validation and atomic rename |
| crash after rename before queue save | reconcile final provenance/identity with durable job intent; never blindly delete or publish twice |
| fallback mixes translations | cross-group recovery always replaces the whole chapter |
| signed URL expires during resume | refresh same-source manifest, then validate identity before reuse |
| browser download escapes staging | canonical allowed-root containment in C# and Python |
| shared transport extension breaks CamoProf | change only the bounded response mechanism; keep feature commands local and verify affected v1 contracts |
| sorting mutates queue semantics | sort only the Download List collection view; never persist visual order or use it for scheduling |
| root changes redirect an active job | capture normalized root/target at queue time and pause on pre-publication mismatch |
| browser remains after app exit | EOF/finally/graceful close/process-tree escalation contract |
| source site changes filter/API | Comix contract/version remains inside its adapter and fixtures fail visibly |
| shared UI gains Comix logic | shared controls accept generic items/state only |
| Catalog state is lost on queue view | Catalog owns state; route host preserves child lifetime without owning/mutating that state |
| screen navigation stops a job | Queue is owned by module lifetime, not DownloadListScreen |
| update removes profiles/queue | all mutable state remains under LocalAppData |

## 15. Done definition

Done means the two routed child screens work through the boundaries above;
opening and editing remain network-idle until explicit actions; the complete
public logged-out Comix Browse/filter/detail/group path works; queue state
survives restart without auto-resuming; page failures continue then recover
only failed pages; a cross-group fallback can never mix individual pages; every
published CBZ is complete, validated, provenance-tagged, atomically committed,
present on disk and discoverable after an explicit Library scan; all UI uses
the existing shared primitives through feature-owned composition; existing CamoProf
transport remains compatible; Library scanning remains owned by Library;
Download List sorting cannot mutate durable job order; and relevant automated,
live WPF and live chapter gates pass. If implementation/build checks pass but
required live evidence is unavailable, report IMPLEMENTATION COMPLETE / LIVE
VERIFICATION PENDING, not
the whole goal complete. Do not broaden this task into unrelated repairs merely
to clear every older repository issue.

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

The product and implementation choices above stay locked. The executor resolves
only routine syntax/mechanical details from current owners and consumers and does
not ask the user to choose among alternative architectures. Any change to screen
count, trigger policy, source/group fallback, identity, persistence, output
integrity, browser choice, local-folder mapping, dependencies, shared UI, or
PyHost execution model is outside scope and must not be made during execution.

## 17. Execution checkpoint — 2026-09-05

**Overall status: IMPLEMENTATION COMPLETE / LIVE VERIFICATION PENDING.**
Per section 15 this is not the whole goal complete: no live WPF gate and no live
Comix request has been run. Baseline at start: clean HEAD `b882b64`, version
`2.0.3`. No commit, bump, installer build, or release was performed.

Result: 26 production files under `module/mangareader/Features/Downloader/` plus
`Library/LibraryRootContext.cs` and `CoverBuilder/CoverSourceReference.cs`; one
new linked-source test project (`tests/Module.Mangareader.Downloader.Tests`, 11
test files) added to `Citadel.slnx`; 12 existing source/config files modified
plus this document. `git diff --check` clean; no runtime profile, page, CBZ
fixture, secret, cache, or queue data is tracked (all test state lives under
`%TEMP%`, all product state under `%LocalAppData%`).

| phase | files/owners | outcome | checks | pending |
|---|---|---|---|---|
| 0 | this document; live source inspection | Owner/consumer map confirmed against HEAD `b882b64`; the four section 2.1 contracts implemented as locked | Reading of `PyHost.cs`, `pyhost.py`, `ArchivePageReader`, `LibraryPathStore`/`LibraryScanPersistence`, `CoverBuilderService`/`CoverSourceLoader`, `SettingTable`, `Citizen.targets` | Comix query keys, ids, routes and page contract still need live revalidation |
| 1 | `Module.Mangareader.csproj`; `Sources/MangaSourceContracts.cs`, `MangaSourceRegistry.cs`; `Catalog/CatalogFeature.cs`, `CatalogScreen.xaml(.cs)`, `RemoteTitleCard.xaml(.cs)`; `DownloaderContract.cs`, `DownloaderView.xaml(.cs)`; `MangaReaderView.xaml(.cs)` | One Downloader tab; two routed children; explicit single-source registry; idle Catalog; `CatalogFeature` owns query/result/detail/selection and the latest-request-wins guard | Citizen build 0 error 0 warning; no network call exists on any open/provider/filter/input path by construction | Live first-open and Advanced Filters gates |
| 2 | `sharedLogic/cs/PyHost.cs`; `sharedLogic/pyhost/pyhost.py` + `README.md`; `sharedLogic/tests/test_pyhost.py`; `DownloaderPyHostClient.cs`; `mangareader_downloader/{plugin,browser}.py` | Citizen wiring applied (shared C# link + `PyhostPluginName=mangareader_downloader`). 4 MiB bound enforced in Python before writing and by a bounded C# reader returning `RESPONSE_TOO_LARGE`; inline FIFO loop untouched; no cancel command, event stream, or v2. Plugin owns Camoufox bootstrap, page-context API, staging-only page fetch, and close | `test_pyhost` 26/26 (24 existing + 2 new bound tests); plugin deploys as `__init__.py` + `browser.py`; CamoProf plugin still deploys beside it | Live Browse smoke; real Camoufox bootstrap behavior unverified |
| 3 | `Sources/Comix/ComixSource.cs`, `ComixFilterPanel.xaml(.cs)`; `Library/LibraryRootContext.cs`; `LibraryView.xaml.cs`; `DownloadSourceIndex.cs` | Browse/pagination/lookup/detail/group/chapter/manifest; Back restores grid and anchor without fetch; compact direct/advanced filter dropdowns built only from existing shared controls; captured status/demographic/genre/year/minimum/author/artist/sort requests replace the earlier refusal paths; `LibraryRootContext` owns the root and Library consumes it before `Loaded`; mapping confirmation with explicit folder choice | Downloader tests: exact captured query serialization, local range validation, mapping identity rules | Live compact action-bar, multi-checkbox, Start/Stop and filter/selection path |
| 4 | `Queue/{DownloadQueueModels,PageTransport,ChapterDownloadPipeline,CbzChapterPublisher}.cs` | Queue intent → staged pages → validated CBZ → terminal Completed state. Native streaming first with one fixed browser fallback; `.partial.<guid>.tmp` staging; full pre-commit validation; atomic rename; provenance-based collision policy | Downloader tests: publication, missing-page refusal, different-provenance conflict with no `(2)`, same-provenance replacement with one backup, temporary file invisible to `LibraryScanner` | Live chapter download |
| 5 | `Queue/DownloadQueueStore.cs`, `DownloadQueueFeature.cs` | Atomic `queue.json` as sole durable authority; `job.json` page journal only; restart parks every unfinished job (including merely queued); `Pausing`→`Paused`; two-page concurrency; 1/2/4 s retry schedule; failed-page-only recovery; manifest-change conflict | Downloader tests: round-trip and order, corrupt-file fail-soft, unknown schema preserved and never overwritten, restart parking, crash-after-rename reconciliation, summary badge counts | Live pause/resume/restart |
| 6 | `Queue/DownloadListScreen.xaml(.cs)`; `DownloaderView.xaml(.cs)` | Download List reachable only from the Catalog button; Back restores Catalog without fetch; route changes never dispose the queue; `SettingTable` with five sortable columns and an unsortable Action column | Citizen build clean; sorting is presentation-only because the shared table sorts its collection view | Live routing, progress responsiveness, and all queue actions |
| 7 | `Sources/MangaSourceContracts.cs` (`RemoteAlternateChapter` + `FindAlternateGroupsAsync`), `ComixSource.cs`; `DownloadQueueFeature.cs` | Alternate-group discovery composed from existing group/chapter calls; `AwaitingSourceFallback` with per-candidate confirmation replacing the whole chapter identity, group and filename. Successful publication ends inside Queue without Library refresh or Cover Builder invocation | Downloader test: cross-group confirmation replaces the whole chapter and nothing changes without it; filename determinism and natural sort order | Live fallback and terminal publication |
| 8 | all of the above | MangaReader, CamoProf and Shell all build Release with 0 error 0 warning; both citizens deploy with their plugins and no private shared Citadel DLL | Solution suite once: **663 passed, 0 failed, 0 skipped** (Core 108, Ui 14, Uia 229, Camoprof 55, Archive 33, Library 24, Reader 97, Downloader 103). Python `test_pyhost` 26/26 | Every live gate in section 13.2 |

### Automated gates closed by the completion audit

After the phase table was first written, the section 13.1 automated gates were
re-read against the suite and seven test files were added to close the ones that
were still uncovered. The Downloader project went from 49 to 103 tests and the
solution from 609 to 663, both green. Newly covered:

- Reader regression on a published chapter: `META-INF/citadel-source.json` is
  invisible to chapter/page enumeration and the cover is still the first image.
- Transport path containment: a staged page record cannot make `PageTransport`
  write or read outside the job's staging root.
- Cover contract ordering: `BakeAsync` resolves the source before any bake, a
  remote reference is never routed to `LoadLocalAsync`, and reuse requires the
  stored artifact to still decode.
- Explicit network triggers: opening the tab, switching provider, editing a
  filter, typing in search and paging all make **zero** provider calls; only the
  search/apply action does. A superseded request is discarded by the
  generation guard rather than written over the newer result.
- Invalid local range never reaches the provider (validation is local-first).
- Library root ownership: `LibraryRootContext` is the only root authority,
  `LibraryView` consumes it before `Loaded`, and the queued target captures the
  root at queue time rather than at publish time.
- Two `DownloadQueueStore` instances over one file do not corrupt it.
- Resume: an intact staged page is reused without refetch, a corrupt staged page
  is rejected and recovered, and a changed chapter manifest conflicts instead of
  publishing mixed provenance.
- The production retry schedule is asserted to be 1 s / 2 s / 4 s with page
  concurrency 2.
- The object-backed half of the shared-controls gate: every Comix option list and
  a resolved `RemoteLookupOption` render `DisplayName` through `ToString()` and
  never a record dump, keys are unique within each list, and the panel defaults
  resolve to real options and match `ComixBrowseQuery.Default`. The
  accessible-name/UIA half of that row stays live under section 13.2's "filter
  accessibility" gate, because proving it headlessly would require an
  `Application` with `SettingResources` merged before the panel's
  `StaticResource`-based XAML parses.

One test seam was added to production code for this: `ChapterDownloadPipeline`
takes an optional `IReadOnlyList<TimeSpan>? retryDelays = null` that defaults to
the production schedule, so the corrupt-staging recovery test runs in
milliseconds instead of fourteen seconds. No other production signature changed
for testability, and no transport interface or backend abstraction was
introduced.

Three section 13.1 gates stay live-only or partly live:

- **Download List sorting** — the test project deliberately does not link screen
  XAML (see the comment in its `.csproj`), so the rendered table's collection
  view and the Action column's sortability cannot be exercised headlessly. The
  persistence half of the gate is structural rather than tested: the screen binds
  immutable `DownloadJobRecord` snapshots and has no write path to
  `DownloadQueueStore`, so a visual sort cannot reach `queue.json`, job identity
  or runner order.
- **Per-page retry bound counting** — proving "at most three attempts per page"
  against the real transport needs a transport seam, and section 16 forbids
  adding an abstraction beyond those named in the plan. The schedule constant is
  asserted instead; the count itself is observed live.
- **C# halves of "Downloader transport capabilities" and "timeout race"** —
  `PyHost.Start` is the only construction path and it launches the real
  interpreter, so the bounded C# line reader, late-response discard, the client's
  fixed timeouts and owned-process cleanup cannot be driven headlessly. Doing so
  would mean adding a process/stream seam to shared code that CamoProf also
  depends on, which section 2.1's locked execution model and section 16 forbid.
  The Python halves of the same contracts *are* automated: the 26 `test_pyhost`
  cases cover the 4 MiB bound, open/close timeouts, stdin EOF and clean shutdown,
  and CamoProf's 55 cases keep every v1 command green. The C# halves are observed
  in the live Browse smoke and the CamoProf smoke.

### Defect found and fixed during execution

`DownloadQueueFeature.NormalizeNumber` formatted a parsed `double` with the
integer-only `"D4"` specifier, which throws `FormatException` for every numeric
chapter number. That would have failed the first real queue operation, before
any file was written. Caught by the filename gate added in Phase 7 and fixed by
parsing integers separately for zero padding and using a custom numeric format
for decimal chapter numbers such as `10.5`.

### Contract details that are assumptions, not captured evidence

These live in one versioned table (`ComixContract`) so live revalidation is a
single-place edit. They must be checked in Phase 2/3 before any live claim:

- `RouteTitle = /api/v1/manga/{hid}` was **not** in the research's observed route
  list, which recorded only `/api/v1/manga`, `/api/v1/manga/{hid}/chapters` and
  `/api/v1/chapters/{id}`.
- Genre/format resolution is routed through the manga list endpoint with the
  genre/format key because no dedicated tag endpoint was captured.
- Response field names (`items`, `total`, `chapters`, `pages`, `cover`, `slug`,
  `group_name`, `number`, `id`, `hid`, `name`) are plausible normalizations, not
  transcribed payloads. A missing field raises `ComixContractException` rather
  than yielding an empty success.
- Sort options: research recorded that the provider offers 13 but did not
  capture their names. Only the evidenced default ("latest update") is listed;
  the remaining options were **not invented** and must be captured live.
- Ratings/types/demographics/statuses use the researched display values with
  semantic keys (`safe`, `manhwa`, `on_hiatus`, …); the provider's actual key
  encoding was not captured.

### Other honest limitations

- The descrambler implements the documented shape (xorshift32 permutation,
  inverse tile permutation, grid, algorithm 3, unknown-hash fallback) and is
  verified by a synthetic round-trip and a bijection theory. The section 13.1
  "captured algorithm 3" gate is **not** satisfied: those two live samples were
  deleted after the 2026-09-01 research.
- No Comix request was made during this execution. Every provider-facing path is
  unverified against the live site.
- Result grid cards are text-only; the remote cover is loaded asynchronously in
  the detail view. This avoids synchronous UI-thread image downloads for a whole
  grid, and section 3.4 requires cover on the detail, which is satisfied.
- `BasedOn="{StaticResource SettingListItemStyle}"` in `CatalogScreen.xaml` and
  `ComixFilterPanel.xaml` is the first static resource reference in any
  `module/` XAML. It resolves at runtime because `App.xaml` merges
  `SettingResources` before any module loads, but any harness that loads those
  XAML files must merge `SettingResources` first or the load throws. The
  `ThemeResourcesTests` no-static-resource invariant applies only to
  `Citadel.Ui` theme files and is unaffected.
- Downloader publication is terminal. It does not refresh Library or invoke the
  independent Cover Builder feature; users explicitly Scan when they want the
  new CBZ reflected in Library.

### Remaining work before this can be called done

Every gate in section 13.2, plus: one logged-out live Browse smoke (Phase 2),
live revalidation of the Comix contract table above (Phase 0/3), a captured
algorithm-3 fixture for the decoder gate, one complete live chapter into a
disposable library with restart/pause/resume and Reader open (Phase 8), and one
CamoProf smoke because the shared response reader changed.
