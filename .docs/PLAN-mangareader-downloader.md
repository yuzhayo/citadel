# PLAN: MangaReader local-first Downloader

Status: **IMPLEMENTATION-READY PLAN — product and preflight contracts locked;
this document is not implementation approval**
Historical baseline: `main` at `ae450bc` on 2026-09-02.
Yuzskill reconciliation: 2026-09-05, inspected clean HEAD `54b5a06`, version
`2.0.3`; no Downloader source was found. The ownership/shared-UI refactor and
interactive `SettingTable` sorting are already committed. Recheck HEAD, status
and actual integration contracts before execution; historical paths are not a
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
This refinement used that official file fallback because the Yuzskill MCP gate
was unavailable; it does not claim an MCP receipt.

This refinement preserves the product in section 3. It corrects implementation
directions that could create duplicate ownership or unnecessary infrastructure:
Queue is independent of both screens; feature protocol/decoding stays local;
PyHost extensions are capability-driven, not a mandatory v2 rewrite; new UI
primitives require the existing approval boundary; and phases verify coherent
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
3. **Shared transport:** retain the current PyHost v1 request/response model.
   Add only a bounded response envelope and generic cooperative request cancel.
   Preserve one-at-a-time FIFO command execution; no event stream, parallel
   dispatcher, backend framework, or v2 rewrite is required.
4. **Citizen wiring:** MangaReader follows the current CamoProf citizen pattern
   by linking shared C# sources and declaring its Downloader Python plugin name.
   This is mechanical project wiring, not a new deployment system.

Interactive sorting is already a shared `SettingTable` capability. Download
List may opt into it, but sorting is presentation-only and never changes the
durable queue order. These contracts remove the prior preflight ambiguity;
Phase 1 may begin independently, while the concrete dependencies are introduced
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
either screen. Composition may construct module-lifetime services but must not
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

The Catalog header contains:

1. web-target dropdown;
2. search field;
3. `Advanced Filters` toggle;
4. `Start`;
5. remote/network state; and
6. `Download List (n)` with an active/paused/failed job count badge.

Advanced Filters are provider-owned composition built from shared fields. The
Comix filter feature owns input state and validation and produces one immutable
`ComixBrowseQuery`; its panel binds that state and translates input events.
Neither panel nor Catalog builds provider URLs. Remote/network state reflects
explicit requests/jobs, not a background connectivity monitor.

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

Library remains the sole owner of that root. At MangaReader composition time,
create one module-lifetime `LibraryRootContext` backed by the existing
`LibraryPathStore`. The Library feature restores it once and commits a new
normalized value only after a successful scan, preserving the current
`LibraryScanPersistence` rule. The context exposes only a current immutable
snapshot and change notification needed by consumers. MangaReader parent may
construct and inject it, but owns no root policy. Downloader must not instantiate
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
order in `queue.json`. If interactive column sorting is enabled, it operates on
the WPF presentation view only: it must not reorder jobs, rewrite `queue.json`,
or affect runner scheduling. The Action column is never sortable. Sorting is a
usable shared capability, not a prerequisite for the first end-to-end job.

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
  action that reuses the existing Cover Builder feature contract. It defaults
  on for a newly created title folder and off for a pre-existing folder.
- Cover Builder exposes one bake operation with a typed local-or-remote source.
  For a remote source, that service resolves/downloads it into Citadel-owned
  storage before entering the existing archive transaction; a prior matching
  Fetch may be reused as a cache, but is not a UI-owned prerequisite.
- The existing Cover Builder View and Downloader both call that operation.
  Downloader never calls the View, its fields, or a private archive writer, and
  this slice does not introduce a global image service or second archive flow.
- A cover failure warns but does not invalidate already published chapters.

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
Cover Builder ──── typed Bake operation <── Downloader optional post-action
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

Queue logic does not reference WPF; DownloadListScreen may live beside it but
only binds snapshots/commands. Catalog does not mutate Queue collections.
Library does not call Comix. Completion crosses a typed `ChapterPublished`
event to Library's refresh contract, not a call into `LibraryView` internals.
Cover integration calls CoverBuilder's feature contract, not its screen or
private service. Reuse the current post-refactor contracts; if absent, add the
smallest entry at the owning feature, without moving its policy into parent.
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
| Library bridge | consume the root context and invoke post-publication refresh via contract | preference-file access, mapping persistence, screen controls |
| Cover Builder | typed local/remote source resolution and one bake operation over the existing archive flow | Downloader queue/retry policy |
| Cover integration | optional Cover Builder contract invocation | private sibling services, fetch-before-bake policy, second archive writer |

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
- generic cooperative cancellation uses a `request.cancel` control frame. The
  stdin reader remains able to receive that frame while exactly one worker
  executes ordinary commands FIFO. It cancels the active target or marks a
  queued target cancelled; ordinary commands never execute concurrently;
- C# sends cancellation only after the target request was written, completes
  the caller as cancelled, and safely ignores a late terminal response;
- cancellation must not tear down another job/session/process, and must not use
  unbounded status polling;
- disconnect/EOF/shutdown cleanup and bounded idle release;
- URL validation and destination containment for browser writes.

Implement only the response bound and cancellation control that current source
lacks. Document these additive protocol changes beside the existing protocol;
preserve request ordering, one terminal outcome, and all CamoProf wire/error/
lifecycle semantics. A broad
"PyHost v2" replacement, second registry or lease framework is not a milestone.
If compatibility cannot be kept with a small extension, surface that specific
gap before changing shared behavior; independent UI/domain work can continue.

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

`Completed` means publication and durable completion are confirmed. Before
atomic rename, cancellation/failure leaves no new final CBZ. After rename but
before queue/index save, a valid final CBZ can exist while durable job state
still says Publishing. Reconcile that window using the existing source
manifest/identity; never delete or re-download a valid file just to make the
state diagram true. Restart still pauses unfinished work, with no automatic
network activity; local reconciliation may recognize an already completed job.

### 8.2 Concurrency and cancellation

- Initial default: one active chapter and two concurrently streamed pages.
- Each page has bounded timeout, initial attempt, and at most three retries per
  pass.
- Job cancellation is cooperative first; process-tree termination is the last
  orphan-safety escalation and only for a process owned by Downloader. Do not
  kill a browser serving another job/request or the separate CamoProf process.
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

These are runtime files, not manga-library sidecars. `queue.json` is the sole
durable job-state authority; `job.json`, if needed, contains staging/page records,
not a second independently mutable copy of queue state. Omit it if those records
already have an owner in the manifest/store. Every staged page records
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

Also reuse `SettingTabs`, `SettingViewport`, `SettingListStyle` and
`SettingCardStyle` for the tab, finite viewport, selectable lists and surfaces.
Shared styles and each control's documented behavior pair remain canonical.
`SettingTable` already provides opt-in interactive sorting. Download List uses
that capability directly when sorting is wanted; it must not create a local
header/sort control. The feature supplies sortable scalar values and keeps the
Action column disabled. The sorted collection view is disposable presentation
state and never becomes queue persistence or scheduling policy.

Multi-select and searchable resolved-tag selection are required capabilities,
not automatic authorization to add `SettingMultiSelect`/`SettingTagPicker`
primitives. Inspect the current inventory first. Prefer a reusable combo of
existing fields, selectable lists/toggles, buttons and shared scrolling. A
combo owns arrangement/selection composition, delegates universal input/focus/
rendering, and needs no extra approval within approved feature implementation.
If new primitive/style/template behavior is genuinely missing, report the gap
and obtain explicit approval before adding it; this plan refinement does not
grant that approval. Do not block unrelated slices on that UI decision.

Shared controls/combos know nothing about Comix, genres, ratings, authors,
artists, providers, or requests. Numeric fields use `SettingField` with
feature-owned validation. Author/Artist lookup presentation stays with the
provider/Catalog feature; share only a proven provider-neutral composition.

The Catalog grid may extract the visual frame of `MangaTitleCard` only if both
local and remote consumers can use a screen-blind data/presentation contract.
Local and remote models and actions remain separate; no adapter may fill a
local `MangaTitle` with remote placeholders. Extraction is optional, not a gate:
an existing shared card/combo can host remote content without refactoring the
local Library card. Do not redesign local UI to make Downloader possible.

## 11. Target file tree

This is an ownership map, not a file-creation checklist. Reuse current files
and introduce a folder/class only for a meaningful owner or real boundary.
Do not make a hierarchy per method, enforce a file-count quota, or create empty
layers for hypothetical future providers. Queue's pipeline remains inside
Queue ownership until another actual consumer needs a narrower shared service.

```text
module/mangareader/
├── MangaReaderView.xaml(.cs)                 add one Downloader tab only
├── Module.Mangareader.csproj                 shared C# link + plugin name wiring
├── Features/Downloader/
│   ├── DownloaderView.xaml(.cs)              parent/router
│   ├── DownloaderContract.cs                 narrow context, routes, commands/events
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
│   │   ├── DownloadQueueStore.cs
│   │   ├── ChapterDownloadPipeline.cs        owned by Queue, not a global pipeline
│   │   ├── PageTransport.cs
│   │   ├── PageRecoveryPolicy.cs
│   │   └── CbzChapterPublisher.cs
│   ├── DownloadIdentity.cs                   UI-free identity used by real consumers
│   ├── DownloadSourceIndex.cs                mapping/publication index owner
│   ├── DownloaderPyHostClient.cs             feature command/payload adapter
│   ├── mangareader_downloader/               owned Python plugin; registered lazily
│   │   └── plugin.py                         commands/lifecycle + cohesive files as needed
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
├── shareLogic/Archive/                       reuse lock/validation/replacement, no rewrite
├── CoverBuilder/                            typed bake contract, existing archive flow
└── Library/                                 one root context + refresh; no remote logic

module/sharedLogic/
├── cs/
│   └── PyHost.cs                             reuse transport; minimal additive gap only
├── pyhost/
│   ├── pyhost.py                             existing dispatcher/plugin/lifecycle owner
│   └── README.md                             update actual protocol additions only
└── tests/test_pyhost.py                      existing shared host regression owner

setting/Components/
└── <reusable combo only if missing>          primitives require separate approval

tests/
├── Module.Mangareader.Downloader.Tests/      linked pure feature sources
└── Citadel.Uia/                              relevant shared-control/WPF checks
```

The citizen project remains outside `Citadel.slnx`; pure linked-source tests may
be added to the solution like the existing Archive and Library test projects.
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

### Phase 0 — scoped baseline and contract capture

Owner: this plan, current integration contracts and research fixtures.
Inspect live paths/callers, shared UI inventory and transport capabilities.
Preserve unrelated WIP; coordinate overlapping edits, not a mandatory clean
worktree/commit. During authorized implementation, revalidate Comix query keys,
IDs/defaults and the page contract against current logged-out evidence. Keep
Lucky deferred. Use disposable library/job roots, never the real collection.

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
the entire queue/decoder hierarchy here. Resolve required UI compositions under
section 10 when this slice needs them, rather than waiting for a control framework.

Gate: one tab, no remote activity on open/provider/input changes, local query
validation and stable child-owned state. Check the actual UI/feature boundary
with a controlled source and record live UI evidence separately.
Dependency: Phase 0.

### Phase 2 — explicit Browse through a feature-owned browser adapter

Owner: Comix source, Downloader C#/Python adapter and its process lifetime.
Wire Start -> lazy Camoufox -> one normalized Browse response -> Catalog.
First apply the mechanical MangaReader citizen wiring from section 11. Reuse
transport/plugin installation; add only the bounded response and cooperative
cancellation gaps specified in section 7.2. No mandatory v2 backend/registry
rewrite or event stream. Cancellation, timeout, root separation and disposal
must work for this real path before extension.

Gate: Start succeeds or reports a bounded error; latest-request-wins and
shutdown/EOF cleanup hold. Run affected CamoProf transport checks if shared
code changes. Regular tests use recorded/synthetic data; live smoke is opt-in.
Dependency: Phase 1.

### Phase 3 — catalog, filters and title/group selection end to end

Owner: Catalog/TitleSelection and Comix source/filter feature.
Extend the working Browse path to explicit pagination, Enter/Search-only
lookups, detail/group/chapter/manifest discovery and Back restoration. Keep
provider payloads local and preserve one active group. Add only contracts with
actual consumers. Introduce the single Library-owned `LibraryRootContext` and
establish confirmed folder mapping from its snapshot, never from the View or
preference file directly.

Gate: public logged-out filter/selection path works, stale responses cannot
commit, and remote models never masquerade as local title/card models.
Dependency: Phase 2; relevant reusable filter combo from section 10.

### Phase 4 — one queued chapter through publication, then recovery

Owner: Queue, its pipeline/store/publisher and source-owned decoder.
First deliver queue intent -> staged pages -> validated CBZ -> Library refresh
for one selected chapter. Then add restart-to-Paused, bounded concurrency,
native streaming/browser fallback, failed-only recovery and source-fallback
state around that same path; no parallel downloader/archive implementation.
Use existing archive locks/validation and implement source transforms behind
the source boundary. Reconcile crash-after-rename before retrying publication.

Gate: a complete chapter opens locally; missing/unknown/corrupt pages never
publish; resume reuses only validated matching staging. Targeted fault checks
cover retry bounds, pause, manifest change, collision and publication recovery.
Dependency: Phase 3. UI for queue progress/actions is consumed in Phase 6.

### Phase 5 — shared-composition completion (only remaining gaps)

Owner: existing shared components/combos and provider filter presentation.
Reuse compositions already delivered with Catalog. Add a reusable combo only
where the inventory is insufficient; do not automatically create MultiSelect
and TagPicker primitives. Any genuine new primitive/style/template requires
approval under section 10. Keep provider options and remote calls outside shared UI.

Gate: relevant keyboard/focus/selection/disabled/fluid-layout/cleanup behavior
works. If existing controls already cover it, mark satisfied; no new file/test
suite is required just to have a Phase 5 deliverable.
Dependency: actual UI consumer; may be completed within Phases 1-3.

### Phase 6 — Download List and two-screen routing

Owner: DownloadList presentation and Downloader route host.
Connect queue commands/snapshots to Download List, accessible only from the
Catalog button. Back restores Catalog state without fetching. Route changes
never dispose Queue; immutable progress updates are marshalled by the WPF
adapter, not by WPF code inside Queue. Verify all specified queue actions.
Use `SettingTable`; if sorting is enabled, verify that it changes only the
presentation view while persisted queue order and scheduling remain unchanged.

Gate: background job continues while Catalog is visible; screens do not share
mutable state or call sibling internals; open/select/type remain network-idle.
Dependency: Phases 3-4 and any remaining Phase 5 capability.

### Phase 7 — fallback, cover and integration cleanup

Owner: Queue/Comix fallback plus Library and CoverBuilder feature contracts.
Finish alternate-group confirmation and whole-chapter replacement, optional
post-batch cover bake through Cover Builder's single typed source operation,
and publication refresh. Remove the View-owned fetch-before-bake prerequisite
when wiring the existing View to the same operation. Preserve independent
source identities and warn without invalidating published chapters on cover
failure.
Delete directly superseded paths/temporary harnesses after caller checks;
retain the small sanitized regression fixtures actually used by tests.

Gate: no mixed-group archive, no silent folder claiming, no private sibling
service access, and no leftover alternate download path.
Dependency: Phases 4 and 6.

### Phase 8 — integration validation and handoff

Build MangaReader Release directly and verify isolated deployment including
its Python plugin. Build CamoProf/Shell when shared changes affect them; Debug
is additional only for a concrete configuration concern. Reuse per-phase test
evidence, then run the solution suite once with bounded parallelism for final
shared integration. Diagnose failures locally instead of looping full suites.

Live gates: Catalog/Download List at minimum, normal and maximized sizes; one
opt-in complete Comix chapter in a disposable library, restart/pause/resume and
Reader open. CamoProf smoke remains necessary if shared runtime behavior changed.
Unavailable live evidence remains PENDING, not an implied PASS.
Do not commit, bump, build an installer or publish unless separately requested.
Dependency: all required outcomes above, not a prescribed number of files/tests.

## 13. Validation matrix

### 13.1 Automated gates

| Gate | Pass condition |
|---|---|
| explicit network triggers | open/provider/filter typing cause zero calls; Start/Load more/lookup/title/job cause expected calls only |
| query contract | all captured Comix defaults/options/IDs serialize exactly; invalid ranges never call provider |
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
| Downloader transport capabilities | 4 MiB response bound, targeted cooperative cancellation, FIFO command ordering, path containment, timeout, EOF/disconnect and owned-process cleanup pass; no events or v2 rewrite required |
| Cover contract | local and remote inputs reach one bake operation; remote fetch failure never enters archive mutation; both View and Downloader use the same policy owner |
| shared controls | keyboard, focus, selection, chips, fluid layout, unload/cleanup, and UIA behavior pass |
| full regression | current `Citadel.slnx` suite passes after integration |
| citizen builds | MangaReader Release and affected citizens build/deploy cleanly with the Downloader plugin present and no private shared Citadel DLLs; Debug only if configuration-specific risk exists |
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
| partial chapter appears complete | incomplete temporary files are not discoverable; final path exists only after full validation and atomic rename |
| crash after rename before queue save | reconcile final provenance/identity with durable job intent; never blindly delete or publish twice |
| fallback mixes translations | cross-group recovery always replaces the whole chapter |
| signed URL expires during resume | refresh same-source manifest, then validate identity before reuse |
| browser download escapes staging | canonical allowed-root containment in C# and Python |
| shared transport extension breaks CamoProf | keep feature commands local; verify affected v1 contracts before integration |
| sorting mutates queue semantics | sort only the Download List collection view; never persist visual order or use it for scheduling |
| root changes redirect an active job | capture normalized root/target at queue time and pause on pre-publication mismatch |
| remote cover policy is duplicated in UI | Cover Builder owns one typed local/remote bake operation; View and Downloader are callers |
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
visible in the local Library, and readable by the existing Reader; all shared
UI is reused or approved/promoted without provider logic; existing CamoProf
transport remains compatible; Library root and Cover bake each have one owner;
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

The product choices above stay locked. Implementation details are resolved from
current owners/consumers, not treated as pre-approved infrastructure or primitive
creation. Ask only for a genuine missing capability approval or material product
change; do not repeatedly ask permission for normal steps already authorized.
Changes to screen count, trigger policy, source/group fallback, identity,
persistence, output integrity, browser choice or local-folder mapping require
review as a plan change before implementation.
