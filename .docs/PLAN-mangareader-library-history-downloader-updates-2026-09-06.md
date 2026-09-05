# Implementation Plan: MangaReader Library, History, and Downloader Updates

Status: implementation-ready plan. Product decisions are locked by
`.docs/SPEC-mangareader-library-catalog-features-2026-09-06.md`; the existing
Downloader pipeline contract remains governed by
`.docs/PLAN-mangareader-downloader.md`.

This plan deliberately separates work by area and by feature. An executor must
finish and verify one slice before beginning the next. It must not combine
unrelated feature edits into a single large refactor.

## 1. Goal

Deliver three independent update groups:

1. **Update for Library:** shared local detail, Grouping, independent Grid/List
   preference, and manual Update Checker that auto-matches a provider and shows
   only missing local chapters.
2. **Update for History:** shared action bar, independent Clear History and Pin
   features, and an independent Grid/List preference.
3. **Update for Downloader:** compact Catalog action bar and proven Comix
   filters, shared remote detail, filesystem-backed Lister, selectable sortable
   chapter table, independent Auto Cover, and corrected Download List actions.

The completed chapter-download pipeline remains terminal after atomic CBZ
publication and its own durable bookkeeping. Nothing in this plan reintroduces
automatic Library scan, Cover Builder invocation, update checking, or another
post-download chain.

## 2. Non-negotiable architecture

- Parent views compose and route; feature classes own behavior and state.
- Features exchange small UI-free contracts, never controls, card models,
  provider JSON, or another feature's internal state.
- Reuse `SettingButton`, `SettingField`, `SettingTabs`, `SettingActionCard`,
  `SettingTable`, `SettingTableActions`, `SettingDialog`,
  `SettingComboBoxStyle`, `SettingListStyle`, `SettingCardStyle`, and
  `SettingScrollViewerStyle` where their roles apply.
- Do not modify `setting/Components/` or add a new shared primitive. A
  MangaReader combo may compose existing primitives when required.
- Reuse the existing `MangaTitleCard`; do not create another card primitive.
- Use the shared auto-fade scrollbar. No screen-local scrollbar template.
- Keep WPF controls out of persistence, provider, matching, and filesystem
  services.
- Keep current unrelated worktree changes intact. Do not use broad staging.
- No new dependency, backend, database, event-bus framework, polling worker, or
  transport rewrite.
- No version bump, installer, commit, push, or release unless separately
  requested.

## 3. Validation discipline

Each slice has one focused automated gate and one focused live check. Do not
run the full solution after every small visual edit.

Use these standard commands where applicable:

```powershell
dotnet test tests/Module.Mangareader.Library.Tests/Module.Mangareader.Library.Tests.csproj -c Release -m:1
dotnet test tests/Module.Mangareader.History.Tests/Module.Mangareader.History.Tests.csproj -c Release -m:1
dotnet test tests/Module.Mangareader.Downloader.Tests/Module.Mangareader.Downloader.Tests.csproj -c Release -m:1
dotnet build module/mangareader/Module.Mangareader.csproj -c Release -m:1
```

`Module.Mangareader.History.Tests` is the only new test project allowed by this
plan. Keep it pure/non-WPF where possible and use one focused test file per
behavior owner. Do not create separate test projects for Grouping, View Mode,
Lister, Auto Cover, or Update Checker.

Run the full solution once, at final integration only:

```powershell
dotnet test Citadel.slnx -c Release -m:1
```

Build evidence is not visual evidence. Each changed WPF surface requires the
manual check named in its slice before it may be reported visually PASS.

## 4. Shared foundations

These foundations are real cross-feature reuse. Implement them first and freeze
their contracts before area-specific work begins.

### F1 — Shared Manga Detail combo

**Owner:** MangaReader module-level presentation.

**Purpose:** one presentation composition for Library Local Title Detail and
Downloader Remote Title Detail without sharing their domain models/actions.

**Target files:**

- `module/mangareader/Components/MangaDetailView.xaml`
- `module/mangareader/Components/MangaDetailView.xaml.cs`
- `module/mangareader/shareLogic/MangaDetailPresentation.cs`
- `module/mangareader/Module.Mangareader.csproj`

**Contract:**

- cover and text-detail card appear side by side;
- typed slots cover title, synopsis, metadata rows, cover action, and detail
  action;
- missing metadata renders a stable fallback instead of collapsing unrelated
  content;
- long content uses `SettingScrollViewerStyle`;
- caller supplies presentation data and commands; the combo performs no local
  or remote data access.

**Acceptance criteria:**

- [ ] The combo uses existing Citadel controls/styles only.
- [ ] It imports no Library, History, Downloader, provider, queue, or store type.
- [ ] Both local and remote adapters can bind without placeholder domain data.

**Verification:** MangaReader Release build; disposable sample bindings for
local and remote data; manual normal/minimum/maximized layout check.

**Dependencies:** none.

### F2 — Shared Grid/List selector combo

**Owner:** MangaReader module-level presentation; preference remains
feature-owned.

**Target files:**

- `module/mangareader/Components/MangaViewModeSelector.xaml`
- `module/mangareader/Components/MangaViewModeSelector.xaml.cs`
- `module/mangareader/shareLogic/MangaViewMode.cs`

**Contract:**

- exposes Grid and List icon actions using shared buttons;
- receives current mode and emits requested mode;
- owns no persistence, collection, sort, filter, scroll, or selection state.

**Acceptance criteria:**

- [ ] Keyboard/focus/accessible names distinguish Grid and List.
- [ ] Selector can be reused by Library and History with independent state.
- [ ] No global MangaReader view-mode singleton is introduced.

**Verification:** MangaReader Release build and focused manual keyboard check.

**Dependencies:** none.

### F3 — Local title filesystem probe

**Owner:** MangaReader module-local read infrastructure.

**Target files:**

- `module/mangareader/shareLogic/LocalTitleProbe.cs`
- focused tests in the existing Library and/or Downloader test project; do not
  duplicate identical probe tests in both.

**Contract:**

```text
ProbeTitle(rootPath, deterministicFolder, remoteTitleIdentity?)
  -> titleExists
  -> existing chapter/source identities
  -> non-fatal warnings
```

**Rules:**

- actual filesystem is authoritative;
- inspect only the selected title directory, never the whole Library;
- recognize current supported archive extensions and embedded Citadel source
  identity when present;
- an index may be a hint but cannot override absent/present files;
- read-only: never scan Library state, create folders, rename, or delete.

**Acceptance criteria:**

- [ ] A newly published file is visible without Library refresh.
- [ ] A manually deleted file is absent even if an index still mentions it.
- [ ] Same chapter number from different source/group remains distinguishable.

**Verification:** one focused temporary-directory test file; no real manga
collection.

**Dependencies:** none.

### F4 — Neutral remote-source contract exposure

**Owner:** MangaReader module composition infrastructure.

Update Checker and Downloader now have a real shared consumer need for the
existing provider contract. Expose the smallest neutral source/registry surface
without duplicating Comix or moving the working Python transport.

**Scope:**

- reuse the current `IMangaSource`, source identities, and explicit registry;
- move only contract/registration ownership if needed so Update Checker does
  not import Catalog, Queue, or Downloader screen types;
- preserve the existing Comix adapter, page client, command names, and browser
  lifetime unchanged;
- the composition root may provide the same registered source to both feature
  entry points, but must not interpret provider behavior.

**Acceptance criteria:**

- [ ] Update Checker depends only on neutral source contracts.
- [ ] Downloader behavior and all captured Comix calls remain unchanged.
- [ ] No second Comix client, registry, or provider implementation is created.

**Verification:** current Downloader contract tests and MangaReader Release
build.

**Dependencies:** none.

### Foundation checkpoint

- [ ] F1-F4 contracts are frozen.
- [ ] Focused tests/builds pass.
- [ ] No `setting/Components/` change exists.
- [ ] Area agents may now work without editing each other's feature files.

---

# Update for Library

## L1 — Local Title Detail integration

**Owner:** Library local-detail adapter.

**Target files:**

- `module/mangareader/Library/ChapterSelectorView.xaml`
- `module/mangareader/Library/ChapterSelectorView.xaml.cs`
- optional small adapter beside those files; do not put mapping logic in the
  shared Manga Detail combo.

**Behavior:**

- replace the current bespoke title header/detail arrangement with the shared
  Manga Detail combo;
- populate cover, title, local path, and chapter count from the active local
  title;
- reserve the cover action slot for `Fetch Cover` and `Check Updates`;
- reserve the detail action slot above Grouping for `Add Group`;
- keep the existing chapter-open behavior intact.

**Acceptance criteria:**

- [ ] Library uses local data only and does not import Catalog models.
- [ ] Cover and detail remain side by side with shared scrolling.
- [ ] Existing chapter selection/open still works.

**Verification:** MangaReader build; manual open one disposable local title and
chapter.

**Dependencies:** F1.

## L2 — Grouping feature

**Owner:** `Library/Grouping/`.

**Target files:**

- `module/mangareader/Library/Grouping/GroupingFeature.cs`
- `module/mangareader/Library/Grouping/GroupingStore.cs`
- `module/mangareader/Library/Grouping/GroupingBar.xaml`
- `module/mangareader/Library/Grouping/GroupingBar.xaml.cs`
- focused tests in `Module.Mangareader.Library.Tests`

**Behavior:**

- place the shared tab/action area below the title/chapter count;
- leftmost tab is `Add Group`;
- use `SettingDialog` to create a group;
- group name required; selecting a title is optional; empty group valid;
- stored groups become tabs;
- `Add Group` from Local Title Detail adds the active title to a selected or
  newly created group;
- autosave group definitions and stable title membership;
- missing local titles remain recorded and appear unavailable; they are not
  silently removed.

**Out of scope:** rename group, delete group, and remove-title management.

**Acceptance criteria:**

- [ ] Empty and populated groups survive app restart.
- [ ] Grouping never moves/renames folders or edits archives.
- [ ] Library scanning remains owned by Library, not Grouping.

**Verification:** focused store round-trip and validation tests; Library build;
manual create empty group and add one disposable title.

**Dependencies:** L1.

## L3 — Library Grid/List View feature

**Owner:** Library view-mode state and presentation adapter.

**Target files:**

- `module/mangareader/Library/LibraryViewModeFeature.cs`
- `module/mangareader/Library/LibraryView.xaml`
- `module/mangareader/Library/LibraryView.xaml.cs`
- focused preference test in `Module.Mangareader.Library.Tests`

**Behavior:**

- default Grid;
- autosave only `Library.ViewMode`;
- Grid reuses `MangaTitleCard`;
- List reuses `SettingTable` with Library-owned columns and actions;
- switching presentation keeps loaded titles, active Grouping filter, sort,
  selection, and logical position.

**Acceptance criteria:**

- [ ] History mode is unaffected.
- [ ] No scan occurs during view change.
- [ ] Grid and List open the same local title identity.

**Verification:** focused preference test; Library build; manual Grid -> List ->
Grid check after scan.

**Dependencies:** F2 and L2.

## L4 — Update Checker matching and persistence

**Owner:** `Library/UpdateChecker/`; UI-free core first.

**Target files:**

- `module/mangareader/Library/UpdateChecker/SourceBindingStore.cs`
- `module/mangareader/Library/UpdateChecker/UpdateMatcher.cs`
- `module/mangareader/Library/UpdateChecker/UpdateCheckerFeature.cs`
- focused tests in `Module.Mangareader.Library.Tests`

**Stored binding:**

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

Do not store cookies, tokens, raw provider responses, or temporary page/image
URLs.

**Matching order:**

1. Embedded Citadel source identity from actual local CBZ files.
2. Existing confirmed mapping from `source-index.json`.
3. Provider title search using normalized local title plus chapter-overlap
   comparison across provider groups.
4. Persist a unique safe title/group match.
5. If no safe unique match exists, return candidate choices for one-time user
   confirmation; never silently bind ambiguity.

One binding follows one preferred source group.

**Acceptance criteria:**

- [ ] Matching reads actual local files through F3, not Library UI state.
- [ ] A unique embedded/index match needs no URL input.
- [ ] Ambiguous matches remain unbound until confirmed.
- [ ] Store writes atomically and malformed state fails locally.

**Verification:** focused tests for embedded, index fallback, unique search,
ambiguity, group choice, and atomic round-trip. Avoid a combinatorial matrix.

**Dependencies:** F3 and F4.

## L5 — Update Checker popup and Queue handoff

**Owner:** Update Checker presentation and entry contract.

**Target files:**

- `module/mangareader/Library/UpdateChecker/UpdateCheckerDialog.xaml`
- `module/mangareader/Library/UpdateChecker/UpdateCheckerDialog.xaml.cs`
- `module/mangareader/Library/UpdateChecker/UpdateCheckerFeature.cs`
- local-detail adapter from L1

**Behavior:**

- `Check Updates` appears below the cover;
- click opens an owned `SettingDialog` surface;
- states: loading, no updates, ready, and local error;
- fetch chapters only for the bound preferred provider group;
- compare remote identities with F3 actual local result;
- display only missing chapters in `SettingTable`;
- checkbox header selects only currently visible filtered/sorted rows;
- `Download selected` sends immutable selected chapter identities to the
  existing Queue contract, then reports the Queue result;
- no direct download, Library scan/refresh, Auto Cover, or completion chain.

**Acceptance criteria:**

- [ ] Existing chapters never appear in the update result table.
- [ ] No remote request occurs until the user presses `Check Updates`.
- [ ] One title/provider failure stays inside the dialog.
- [ ] Queue receives only explicitly selected missing chapters.

**Verification:** focused matcher/selection handoff tests; MangaReader build;
manual no-update and has-update popup using a disposable title.

**Dependencies:** L1 and L4.

### Library checkpoint

- [ ] All Library tests pass.
- [ ] MangaReader builds Release.
- [ ] Manual L1-L5 flows pass with disposable data.
- [ ] No Downloader or History persistence file was modified by Library flows.

---

# Update for History

## H1 — History action-bar host

**Owner:** History presentation only.

**Target files:**

- `module/mangareader/History/HistoryView.xaml`
- `module/mangareader/History/HistoryView.xaml.cs`

**Behavior:**

- add a compact action bar in the same visual position/pattern as Library;
- compose it from `SettingActionCard` and shared buttons;
- reserve command slots for Clear History and the Grid/List selector;
- do not put clear, pin, retention, or persistence rules in the view.

**Acceptance criteria:** action bar remains fluid and History cards continue to
open the same title/chapter.

**Verification:** MangaReader build and manual normal/minimum/maximized check.

**Dependencies:** F2.

## H2 — Clear History feature

**Owner:** `History/ClearHistoryFeature.cs`.

**Target files:**

- `module/mangareader/History/ClearHistoryFeature.cs`
- minimal command integration in `HistoryView.xaml.cs`
- `tests/Module.Mangareader.History.Tests/ClearHistoryFeatureTests.cs`

**Behavior:**

- remove only unpinned history;
- preserve pinned entries, manga files, and Library entries;
- disable when nothing is removable or while running;
- failure appears locally and leaves the durable file intact.

**Acceptance criteria:** one owner performs the mutation and one atomic History
store remains authoritative.

**Verification:** focused clear/preserve/failure test; manual button state.

**Dependencies:** H1.

## H3 — Pinned History feature

**Owner:** `History/PinnedHistoryFeature.cs`.

**Target files:**

- `module/mangareader/History/PinnedHistoryFeature.cs`
- `module/mangareader/History/HistoryCardModel.cs`
- existing card/list bindings in History view
- `tests/Module.Mangareader.History.Tests/PinnedHistoryFeatureTests.cs`

**Behavior:**

- small shared pin icon appears on every History card/row;
- pin/unpin autosaves through the one History store;
- pinned entries display above ordinary entries;
- pinned entries survive Clear History and do not count toward retention;
- unpin returns the entry to ordinary history;
- retain at most 15 unpinned recent entries.

**Acceptance criteria:** Clear and Pin remain independent commands without
competing persistence writers.

**Verification:** focused ordering, retention, clear-survival, and unpin test;
manual icon/focus check.

**Dependencies:** H2.

## H4 — History Grid/List View feature

**Owner:** History view-mode state and presentation adapter.

**Target files:**

- `module/mangareader/History/HistoryViewModeFeature.cs`
- `module/mangareader/History/HistoryView.xaml`
- `module/mangareader/History/HistoryView.xaml.cs`
- `tests/Module.Mangareader.History.Tests/HistoryViewModeFeatureTests.cs`

**Behavior:**

- default Grid;
- autosave only `History.ViewMode`;
- Grid keeps the existing shared manga cards;
- List uses `SettingTable` with History-owned columns, pin icon, and open action;
- switching mode retains data, order, pin state, sort, and logical position.

**Acceptance criteria:** Library View Mode remains independent and switching
view causes no history mutation.

**Verification:** one preference/isolation test; MangaReader build; manual
Grid/List switch with pinned and unpinned entries.

**Dependencies:** F2 and H3.

### History checkpoint

- [ ] History focused tests pass.
- [ ] MangaReader builds Release.
- [ ] Manual action bar, Clear, Pin, and Grid/List flows pass.
- [ ] Library and Downloader behavior remain untouched.

---

# Update for Downloader

## D1 — Catalog action bar and Comix filters

**Owner:** Catalog presentation plus Comix filter feature. Provider URL/query
construction remains inside `ComixSource`.

**Target files:**

- `module/mangareader/Features/Downloader/Catalog/CatalogScreen.xaml`
- `module/mangareader/Features/Downloader/Catalog/CatalogScreen.xaml.cs`
- `module/mangareader/Features/Downloader/Sources/Comix/ComixFilterPanel.xaml`
- `module/mangareader/Features/Downloader/Sources/Comix/ComixFilterPanel.xaml.cs`
- `module/mangareader/Features/Downloader/Sources/Comix/ComixSource.cs`

If the checkbox dropdown needs a reusable composition, keep one
`ComixMultiSelectDropDown.xaml(.cs)` beside the Comix filter. It is a combo of
existing primitives, not a new Citadel primitive.

**Action-bar behavior:**

```text
[Provider] [Search] [Sort] [Type] [Status] [Rating] [Genres]
[Advanced Filters] [Start/Stop] [Download List]
```

- use available width and wrap fluidly when narrow;
- remove `Idle - no request`;
- button is `Start` when terminal, `Stop` while Browse is active, disabled
  `Stopping...` while the existing bounded inline PyHost command settles, then
  `Start` again;
- errors/validation remain visible below the bar;
- Advanced Filters is a dropdown/popover, not an inline expanding toggle;
- its content is Demographic, Minimum Chapter, Release Year, Author, Artist,
  genre AND/OR mode, and Reset;
- filter edits and Reset never execute Browse; Start snapshots the draft.

**Multi-select controls:** Type, Content Rating, Release Status, Demographic,
and Genre/Format use checkboxes. Sort is single-select. Numeric fields remain
numeric. Author/Artist search only on Enter or explicit Search.

**Exact live query contract:**

| Input | Serialization |
|---|---|
| Search | `keyword` |
| Type | repeated `types[]` |
| Rating | repeated `content_rating[]` |
| Status | repeated `statuses[]` |
| Demographic | repeated `demographics[]=<id>` |
| Genre/format | repeated `genres_in[]=<id>` plus `genres_mode=and|or` |
| Minimum chapter | `min_chap` |
| Year | `year_from`, `year_to` |
| Author/artist | repeated `authors[]=<id>`, `artists[]=<id>` |
| Pagination | `page`, `limit=28` |

Author/Artist options use the captured `tags/search` and `tags/by-ids` routes.
Use all 13 exact sort mappings already recorded in
`.docs/PLAN-mangareader-downloader.md`.

`Manga`, `Manhwa`, and `Manhua` are types. `Pornographic` is a rating.
`Adult`, `Hentai`, `Mature`, and `Smut` are genre/tag choices. Do not invent an
`Uncensored` filter.

**Acceptance criteria:**

- [ ] Captured filters serialize repeated values exactly.
- [ ] A filter edit makes zero Browse calls; Start makes one query snapshot.
- [ ] Stop cannot let a late response replace newer/terminal state.
- [ ] Dropdowns expose checked/unchecked state and accessible option names.

**Verification:** extend only current query/network-trigger tests; Downloader
focused suite; MangaReader build; live logged-out multi-filter Start/Stop check.

**Dependencies:** F4.

## D2 — Remote Manga Detail integration

**Owner:** Catalog remote-detail adapter.

**Target files:**

- `module/mangareader/Features/Downloader/Catalog/CatalogScreen.xaml`
- `module/mangareader/Features/Downloader/Catalog/CatalogScreen.xaml.cs`
- optional remote-detail presentation adapter beside `CatalogFeature.cs`

**Behavior:**

- use F1 shared detail combo;
- cover and text detail are side by side;
- provider parser fills typed metadata slots;
- shared scrollbar handles long synopsis/metadata;
- `Fetch Cover` appears below cover;
- source-group dropdown and chapter area remain Catalog-owned;
- Back restores prior results and scroll without refetch.

**Acceptance criteria:** no Library model is used as remote data and cover
failure cannot remove text detail.

**Verification:** current detail contract tests; MangaReader build; live one
remote detail/back check.

**Dependencies:** F1 and D1.

## D3 — Lister feature

**Owner:** `Features/Downloader/Lister/`.

**Target files:**

- `module/mangareader/Features/Downloader/Lister/ListerFeature.cs`
- small presentation adapter in Catalog detail
- focused tests in `Module.Mangareader.Downloader.Tests`

`Title Lister` and `Lister` are the same feature. Chapter availability is an
operation inside it.

**Behavior:**

- invoke F3 for the selected remote title when detail opens, group changes, or
  the user returns to detail;
- expose title-exists and existing chapter/source identities;
- filesystem result, not Library snapshot, controls availability;
- no polling and no full Library scan;
- read-only; never queue, download, fetch cover, or mutate Library.

**Acceptance criteria:** Catalog immediately reflects files added/deleted
outside its stale Library view and distinguishes source variants.

**Verification:** one focused Lister/probe integration test; manual detail
re-entry after adding/removing a disposable archive.

**Dependencies:** F3 and D2.

## D4 — Sortable chapter table and Chapter Selection

**Owner:** Catalog Chapter Selection state; table remains presentation.

**Target files:**

- `module/mangareader/Features/Downloader/Catalog/CatalogFeature.cs`
- `module/mangareader/Features/Downloader/Catalog/CatalogScreen.xaml`
- `module/mangareader/Features/Downloader/Catalog/CatalogScreen.xaml.cs`
- focused selection tests in `Module.Mangareader.Downloader.Tests`

**Behavior:**

- replace bespoke chapter rows with `SettingTable`;
- sortable Chapter and other meaningful data columns; Action/checkbox column
  unsortable;
- checkbox per row and standard header checkbox;
- header state supports checked, unchecked, and indeterminate;
- Select All affects only currently visible rows after filter/sort;
- changing source group clears selection;
- existing local chapters are dimmed but remain selectable;
- re-download warning is one confirmation per batch;
- same source/group re-download replaces atomically; another source/group is a
  distinct filename/identity;
- selection and scroll are session-only.

**Acceptance criteria:** Queue receives the exact immutable visible selection;
sorting never changes identity or durable queue order.

**Verification:** focused select-all/indeterminate/group-reset/re-download
tests; MangaReader build; live table keyboard and sorting check.

**Dependencies:** D3.

## D5 — Auto Cover feature

**Owner:** `Features/Downloader/AutoCover/`.

**Target files:**

- `module/mangareader/Features/Downloader/AutoCover/AutoCoverFeature.cs`
- `module/mangareader/Features/Downloader/AutoCover/AutoCoverStore.cs` only if
  durable pending work is actually required; do not add it speculatively
- remote-detail cover action adapter
- focused tests in `Module.Mangareader.Downloader.Tests`

**Triggers:**

- immutable queue-added candidate whose title is absent according to Lister;
- manual `Fetch Cover` below the remote cover.

Both triggers use one command. Auto Cover is not called by chapter completion.

**Behavior:**

- write `cover.png` to the deterministic downloaded-title folder;
- automatic trigger skips an existing cover;
- manual trigger asks before overwrite;
- fetch to temporary file, validate PNG, then publish atomically;
- may create the deterministic title folder;
- its failure is local and never changes a chapter job.

**Acceptance criteria:** no Library refresh, Cover Builder, Update Checker, or
Queue completion dependency is added.

**Verification:** focused success/skip/manual-overwrite/failure-rollback tests;
manual Fetch Cover into a disposable folder.

**Dependencies:** D2 and D3.

## D6 — Download List action bar and row actions

**Owner:** Download Queue screen adapter; Queue continues to own job commands.

**Target files:**

- `module/mangareader/Features/Downloader/Queue/DownloadListScreen.xaml`
- `module/mangareader/Features/Downloader/Queue/DownloadListScreen.xaml.cs`

**Behavior:**

- use shared action bar, `SettingTable`, and `SettingTableActions`;
- eliminate overlapping controls in the Action column;
- expose only actions valid for each state: Pause, Resume/Retry, choose source
  when required, Open folder when completed;
- keep `Clear completed` as a screen action;
- disable an action while its owning operation is active;
- row error remains local.

**Acceptance criteria:** no new Queue state owner and visual sorting remains
presentation-only.

**Verification:** current queue tests; MangaReader build; manual rows for
queued, active, paused, failed, awaiting-source, and completed states.

**Dependencies:** none after the existing Queue implementation.

## D7 — Terminal publication guard

**Owner:** existing Queue/Publisher contracts. This is a narrow regression
guard, not a new feature.

**Allowed terminal flow:**

```text
download -> validate -> compile CBZ -> atomic publish
-> queue/source-index bookkeeping -> staging cleanup -> Completed
```

Remove or reject any completion route to Library refresh/scan, Cover Builder,
Lister, Auto Cover, Update Checker, or MangaReader parent orchestration.

**Acceptance criteria:** successful CBZ completion ends cleanly; subsequent
features run only from their own approved triggers.

**Verification:** one structural/behavioral regression test plus the current
CBZ publication test; manual successful disposable download while Library is
open must not crash or scan.

**Dependencies:** D5 and D6.

### Downloader checkpoint

- [ ] Downloader focused suite passes.
- [ ] MangaReader builds Release.
- [ ] Live Catalog filters/detail/Lister/selection/Auto Cover/Queue checks pass.
- [ ] Successful download performs no post-completion feature chain.

## 5. Recommended execution order

Execute in this order so shared contracts stabilize before consumers:

```text
F1 -> L1 -> D2
F2 -> L3 and H1 -> H2 -> H3 -> H4
F3 -> D3 -> D4
F4 -> D1 -> L4 -> L5
D2 + D3 -> D5
D6 -> D7
L2 may begin after L1 and does not block Downloader/History
```

Safe independent slices after the Foundation checkpoint:

- L2 Grouping;
- H1-H4 History, sequential within History;
- D1 Catalog filters;
- D6 Download List actions.

Do not run two agents against `CatalogScreen.xaml(.cs)`, `LibraryView.xaml(.cs)`,
or `HistoryView.xaml(.cs)` at the same time. Each agent must re-read the current
diff before editing a shared consumer file.

## 6. Final integration gate

After all area checkpoints pass:

1. Review the combined diff against this plan and the locked SPEC.
2. Confirm no duplicate card, table, scrollbar, dialog, tab, action bar, or
   button primitive was added.
3. Confirm each feature owns one state/persistence path and parent views contain
   routing/composition only.
4. Run all three focused feature suites once.
5. Build MangaReader Release once.
6. Run `dotnet test Citadel.slnx -c Release -m:1` once.
7. Perform one manual end-to-end disposable flow:

```text
Catalog filter -> Detail -> Lister -> select missing chapter -> Queue
-> completed CBZ -> no Library refresh
-> Library manual Scan -> Local Detail -> Check Updates
-> History open -> Pin -> Clear History -> switch Grid/List
```

8. Run `git diff --check` and ensure no temp browser profile, downloaded test
   manga, queue data, cache, secret, installer, or build output is tracked.

## 7. Definition of done

The plan is complete only when:

- all Foundation, Library, History, and Downloader checkpoints pass;
- all product decisions in the locked SPEC are represented in running behavior;
- every failed or unavailable live check is reported as pending rather than
  implied PASS;
- no feature performs work through another feature's completion path;
- no product decision or architecture choice remains for an executor to make;
- source changes remain uncommitted/unreleased unless the user separately asks
  for commit, version bump, installer, push, or release.

## 8. No open product decisions

This plan intentionally leaves no implementation-time product choice. If live
source inspection proves a provider contract has changed, stop that provider
slice, record the exact new evidence, and update the Comix adapter contract. Do
not redesign Library, History, Queue, shared UI, or PyHost to compensate for a
provider-only change.
