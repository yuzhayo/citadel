# MangaReader Library, Catalog, History, and Update Feature Decisions

Status: product decisions approved in discussion on 2026-09-06.  This is a
locked behavior and ownership record, not implementation approval and not a
replacement for `.docs/PLAN-mangareader-downloader.md`.

## 1. Goal

Extend MangaReader with reusable detail/list presentation and independent
Library, History, catalog, cover, listing, grouping, and update behaviors while
preserving these project invariants:

- MangaReader parents remain composition and routing roots only.
- Every feature owns its state, actions, errors, and lifecycle.
- Features communicate through small contracts; they do not call sibling
  internals or exchange UI/card models.
- Existing shared Citadel controls and behavior are reused before any new UI is
  considered.
- A normal feature can be added or removed without dismantling unrelated
  features.
- A completed chapter download has no automatic Library, Cover Builder, or
  update-checking chain.

## 2. Shared presentation boundary

The following are presentation capabilities, not separate business features:

- action bar;
- tab strip;
- manga card;
- manga detail layout;
- cover slot;
- text-detail card;
- sortable table shell;
- grid container;
- checkbox and header checkbox;
- icon button;
- shared auto-fade scrollbar;
- loading, empty, error, ready, and disabled visual states.

Before implementation, inspect `setting/Components/`,
`setting/SettingResources.xaml`, and `.docs/SHARED-UI-BEHAVIOR.md`. Reuse the
existing control or documented behavior when it satisfies the role. A feature
may create a combo component from existing primitives, but no new primitive,
style, template, or parallel screen-local substitute may be created without
explicit user approval.

Shared presentation receives typed display data and commands. It must not own
provider access, filesystem access, Library scanning, queue state, grouping,
history, or update rules.

## 3. Composition map

```text
MangaReader
|- Library
|  |- Grouping
|  |- Library View Mode
|  |- Local Title Detail
|  `- Update Checker entry point
|- History
|  |- Clear History
|  |- Pinned History
|  `- History View Mode
`- Downloader
   |- Catalog
   |- Remote Manga Detail
   |- Lister
   |- Chapter Selection
   |- Auto Cover
   `- Download Queue
```

The root knows feature entry contracts and routes navigation/messages. It does
not implement feature rules or inspect feature-owned data structures.

## 4. Shared manga-detail presentation

Library Local Title Detail and Downloader Catalog Detail use one shared
presentation composition with different adapters and actions.

Required layout:

- cover and text-detail card appear side by side;
- the text-detail card has reserved typed slots for title, synopsis, genres,
  type, status, year, language, content rating, authors, artists, and other
  provider/local values that are available;
- absent values use an explicit fallback and do not break layout;
- long content uses the existing shared auto-fade scrollbar behavior;
- an action slot exists below the cover;
- a separate action slot exists in the detail area before Grouping.

Remote Catalog supplies provider metadata and remote actions. Library supplies
local folder/chapter data and local actions. Sharing this view must not create a
Library-to-Downloader dependency.

## 4.1 Catalog action bar and provider filters

The Catalog action bar uses its available width. Remove the separate
`Idle - no request` text; the request button communicates activity:

- `Start` when no Browse request is active;
- `Stop` while Browse is active;
- disabled `Stopping...` after Stop until the current bounded inline PyHost
  command settles;
- return to `Start` for every terminal result.

Provider errors and validation remain local messages below the bar. They are
not hidden merely because the button returned to Start.

The fluid action bar contains provider, search, direct compact dropdowns for
Sort, Type, Release Status, Content Rating and Genre/Format, an `Advanced
Filters` dropdown, Start/Stop, and Download List. `Advanced Filters` is a
dropdown/popover, not an inline toggle panel that pushes the catalog grid. It
contains Demographic, Minimum Chapter, Release Year, Author, Artist, genre
AND/OR mode, and Reset.

Multi-value dropdowns use one checkbox per option. Sort is single-select;
Minimum Chapter and Release Year are numeric; Author and Artist are explicit
lookups. Filter edits change draft state only and do not issue Browse requests.
Start snapshots and executes the draft. Reset restores Latest Update plus Safe
and Suggestive without fetching.

The 2026-09-06 live provider capture locks these Comix browse parameters:

```text
keyword
types[]
content_rating[]
statuses[]
demographics[]
genres_in[]
genres_mode=and|or
min_chap
year_from / year_to
authors[] / artists[]
page / limit
order[<captured field>]=asc|desc
```

Author and Artist lookup use `tags/search` with `type=author|artist`, then
`tags/by-ids`, before the resolved provider ID enters the Browse request. The
13 captured sorts and their exact order fields remain canonical in
`.docs/PLAN-mangareader-downloader.md`.

Types are Manga, Manhwa, Manhua and Other. Content ratings are Safe,
Suggestive, Erotica and Pornographic. Release status uses Releasing, Finished,
On hiatus, Discontinued and Not yet released. Demographics are Josei, Seinen,
Shoujo and Shounen. The Genres dropdown contains the 31 captured genres plus
the 9 captured formats and supports AND/OR matching.

`Uncensored` is not a provider filter and must not be invented. Manga/Manhwa
are types, Pornographic is a content rating, and Adult/Hentai/Mature/Smut are
genre/tag choices.

## 5. Downloader Catalog Detail

### 5.1 Cover and detail

- The provider parser fills the shared manga-detail presentation.
- `Fetch Cover` appears below the cover and invokes Auto Cover manually.
- Cover-loading failure is local to the cover state and must not remove title
  details or crash MangaReader.

### 5.2 Chapter table

- Reuse Citadel's existing sortable shared table used by CamoProf when its
  current contract is suitable.
- Each chapter row has a checkbox.
- The checkbox-column header has the standard select-all checkbox with checked,
  unchecked, and indeterminate states.
- Select All affects only rows currently visible after filter and sort.
- Changing source group clears selection.
- Selection and scroll position are session-only.
- A chapter already present locally is visually dimmed but remains selectable.
- Re-downloading existing chapters presents one warning/confirmation for the
  batch, not one dialog per row.
- The same chapter from a different source/group remains a distinct variant.
  Its publication name must preserve source identity so it does not overwrite
  another source accidentally.
- Re-download from the same source/group replaces its target atomically after
  confirmation.

Chapter Selection owns checkbox state and emits an immutable collection of
selected chapter identities when the user invokes Download. Download Queue does
not own table-selection state.

## 6. Lister

`Title Lister` and `Lister` are the same feature. Chapter availability is an
operation inside Lister, not another independent feature.

Lister answers:

- whether the selected title currently exists in local storage; and
- which chapters/source variants currently exist for that title.

The filesystem is authoritative. A Library UI snapshot or a previous Library
scan is not sufficient because Library refresh is intentionally not chained to
download completion.

Lister behavior:

1. Resolve the configured Library/download root and deterministic title folder.
2. Probe only the selected title folder; never scan the full Library.
3. Read actual supported archives and their embedded/source identity where
   available.
4. Return title existence and existing chapter identities.
5. Run when Detail opens, source group changes, or the user returns to Detail.

An index may be a hint, but it is never stronger than actual files. Lister is
read-only: it does not scan or mutate Library state, download files, fetch
covers, or trigger post-download work.

Conceptual read contract:

```text
ILocalTitleProbe.ProbeTitle(rootPath, titleIdentity)
  -> titleExists
  -> existingChapters[]
```

## 7. Auto Cover

Auto Cover is an independent feature. It is not a final step of chapter
download and is not part of Cover Builder.

Accepted triggers:

- a new queue entry supplies a title candidate that Lister reports absent from
  local storage; or
- the user presses `Fetch Cover` below the cover.

Both triggers invoke the same Auto Cover command. Queue publication does not
wait for Auto Cover and Auto Cover failure does not change chapter-job status.

Behavior:

- save the cover as `cover.png` in the deterministic downloaded-title folder;
- an automatic invocation skips an existing `cover.png`;
- a manual invocation asks before overwriting an existing cover;
- download/write to a temporary file and publish atomically;
- creating the deterministic title folder is allowed;
- expose local loading, success, skipped, and error states;
- do not invoke Library scan, Library refresh, Cover Builder, or Update Checker.

There is no resource polling. The MangaReader routing layer may relay an
immutable cover candidate when a queue item is added; it must not interpret or
execute Auto Cover rules.

## 8. Download Queue

Chapter-download completion remains terminal:

```text
download ordered pages
-> validate
-> compile CBZ
-> publish atomically
-> commit queue/publication bookkeeping
-> clean staging
-> Completed
```

It must not continue into Library scan/refresh, Cover Builder, Auto Cover,
Lister, Update Checker, or another parent workflow.

Download List must:

- use the existing shared action-bar/table/button presentation;
- fix the overlapping Action-column controls;
- keep row actions owned by Download Queue;
- disable invalid actions while an operation is active;
- keep failures local to their row instead of crashing MangaReader.

## 9. Library Grouping

Grouping is a Library feature with its own durable state. It does not move or
rename manga folders and does not alter archive contents.

Required behavior:

- place the group tab/action area below the Library title/chapter count;
- use the existing shared tab component;
- the leftmost tab is `Add Group`;
- `Add Group` opens a popup/dialog;
- group name is required;
- selecting/adding a title during creation is optional, so an empty group is
  valid;
- stored groups appear as subsequent tabs;
- Local Title Detail exposes `Add Group` in the detail action area;
- group definitions and membership autosave;
- a missing/unavailable local title does not silently delete its membership.

Rename group, delete group, and remove-title management are outside the current
approved scope.

**Recorded deviation (2026-09-06 implementation).** The tab strip carries one
extra tab, `All titles`, positioned immediately after `Add Group` and before the
stored groups. It is not in the list above. It exists because the shared tab
component is a `TabControl`, which always has a selected tab, so without it there
is no way back to an unfiltered view once a group is selected and the group filter
becomes one-way. `All titles` is not a group: it stores nothing, has no
membership, is never persisted, and selecting it only clears the active filter.
Rename, delete and remove-title management remain out of scope and this tab adds
none of them.

## 10. Library and History View Mode

Library and History each have an independent Grid/List view preference.

- Reuse existing shared icon buttons and table/card/grid presentation.
- Default is Grid.
- `Library.ViewMode` and `History.ViewMode` persist separately and autosave.
- Switching view preserves current data, filter, sort, selection, and logical
  position.
- Grid uses the existing shared manga card.
- List uses the shared sortable table shell, with feature-owned row models,
  columns, and actions.
- Library and History must not reuse Catalog business contracts merely because
  they share table presentation.

## 11. History action bar

History receives an action bar in the same visual position/pattern as the
Library/tab action area. The shared action bar is presentation; its commands
belong to two independent History subfeatures.

### 11.1 Clear History

- Expose `Clear History` in the action bar.
- Remove only unpinned history.
- Never delete manga files or Library entries.
- Disable the action when no removable history exists or while it is running.
- Report failure inside History without crashing MangaReader.

### 11.2 Pinned History

- Expose a small shared pin icon on each history card/row.
- Pin and unpin autosave.
- Pinned items remain above ordinary recent history.
- Pinned items survive `Clear History`.
- Unpin returns the entry to ordinary recent-history retention; it does not
  delete the entry immediately unless ordinary retention naturally evicts it.
- Retain at most 15 unpinned recent-history entries.
- Pinned entries do not count toward that limit.

Clear History and Pinned History are independent feature commands but use the
single existing History persistence owner. They must not create competing
history files or persistence writers.

## 12. Update Checker

### 12.1 Entry point and UI

Update Checker is launched from Library Local Title Detail. `Check Updates`
appears below the cover alongside the other title-level cover actions. `Add
Group` remains in the detail action area above Grouping.

Pressing `Check Updates` opens an Update Checker-owned popup/screen with
loading, no-updates, ready, and local error states. When ready, its shared
chapter table displays only chapters not currently present in local storage.

### 12.2 Provider matching

No URL input is required for the normal flow. Matching order is:

1. Read provider/title/group identity embedded in Citadel-produced CBZ files.
2. Otherwise read the existing confirmed folder mapping in
   `source-index.json`.
3. For an older local title without a binding, search the selected provider by
   normalized local title name and compare local chapter numbers with provider
   groups.
4. Automatically select and persist a unique best title/group match.
5. If no safe match exists or the best result is ambiguous, show candidates in
   the same popup for one-time user confirmation. Never silently bind an
   ambiguous title.

One stored binding follows one preferred source group. A missing or ambiguous
group is a local warning/rebind state, not permission to mix groups.

### 12.3 Durable source binding

Current Downloader persistence already retains provider/title identifiers and
published chapter identities, but a durable update binding needs the complete
provider reference:

```text
ProviderId
RemoteTitleId
RemoteTitleHid
Slug
CanonicalTitleUrl
LocalFolderName
PreferredGroupId
PreferredGroupName
LastCheckedUtc
```

Provider identity, not the URL, is authoritative. The canonical URL exists for
provenance and `Open Source`; provider adapters remain responsible for current
API routes. Do not persist authentication tokens, cookies, raw provider
responses, or temporary image/page URLs.

### 12.4 Update comparison and action

```text
stored or resolved source binding
-> fetch chapters for preferred provider group
-> probe actual local title folder
-> compare ProviderId + ChapterId + GroupId
-> show only missing chapters
-> user selects chapters
-> Add to Download Queue
```

Update Checker may read local files and provider data and submit immutable
selected chapter identities to Download Queue. It does not download directly,
run Library scan/refresh, invoke Auto Cover, or run automatically after chapter
publication.

Checking is manual. There is no startup request, background polling, scheduled
check, or automatic download in the current scope. Failure of one provider/title
stays inside the popup.

## 13. State and persistence decisions

Autosave:

- Library View Mode;
- History View Mode;
- Group definitions and membership;
- pinned state;
- ordinary History within its retention limit;
- confirmed Update Checker source binding and last-check metadata.

Session-only:

- chapter checkbox selection;
- current scroll position;
- open popup state;
- in-progress transient UI state.

Every feature exposes explicit loading, empty/no-result, error, and ready states
where applicable. Bulk actions are disabled without selection and while the
owning operation is active. One feature's error must not crash MangaReader or
silently corrupt another feature's state.

## 14. Minimal cross-feature contracts

Only narrow, UI-free values cross boundaries:

```text
LocalTitleProbeRequest / LocalTitleProbeResult
CoverCandidate
SelectedChapter
RemoteTitleIdentity / RemoteGroupIdentity / RemoteChapterIdentity
SourceBinding
TitleDetailPresentation
ViewMode (Grid | List)
```

These are conceptual responsibilities, not approval to add duplicate types.
Implementation must first reuse compatible current contracts. Do not exchange
card models, WPF controls, provider response JSON, queue records, or feature
internals across boundaries. Do not introduce a general event bus when a small
existing routing contract is sufficient.

## 15. Explicitly outside this decision record

- automatic Library refresh/scan after download;
- automatic Cover Builder invocation;
- startup or scheduled provider polling;
- automatic download of detected updates;
- direct URL input for normal update matching;
- storing provider credentials or raw API responses;
- Grouping rename/delete/remove-title management;
- new shared primitive controls without user approval;
- broad MangaReader parent refactoring unrelated to these feature plugs.

## 16. Implementation readiness

The product behavior above is sufficiently decided for an end-to-end
implementation plan. Technical discovery may identify the exact existing
shared control or compatible current contract to reuse, but it must not reopen
the approved behavior, introduce new product choices, or expand scope.

Build/unit evidence and live WPF evidence must be reported separately. Visual
behavior is not PASS until it is exercised in the running application.
