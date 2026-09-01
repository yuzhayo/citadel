# PLAN: Fluid desktop-first layout and bounded window startup

Status: **IMPLEMENTED — automated and available-hardware visual gates pass; release v1.2.0 (2026-09-01)**

Supersedes the uncommitted `ChromeFit.IsWindowViewport` / `ChromeFitCoordinator`
direction. Existing shared-component behavior remains authoritative except where
this plan explicitly replaces outer-spacing and screen-viewport ownership.

## 1. Goal

Citadel uses one fluid desktop-first layout contract from the main window down
to every screen. The window supplies a bounded viewport; screens and shared
components consume that viewport. Feature content must not resize the Shell to
hide a rigid local layout.

The delivered behavior is:

1. the main Shell opens at its configured desktop size, clamped to the current
   monitor work area;
2. Shell host, Router layers, tab content and screen roots fill all available
   width and height;
3. standard cards fill the available content width with one shared inset;
4. controls use `Auto` for content-sized regions and `*` for flexible regions;
5. no accidental horizontal scrollbar appears in the Shell or a standard
   screen;
6. document-like pages may use one outer vertical scrollbar only when their
   content genuinely exceeds the available height;
7. tables, libraries, chapter lists and the manga reader retain their own
   intentional internal scrolling;
8. navigating, selecting a tab, loading async data or editing a field never
   changes the main window size.

This is a global behavior contract for current and future citizen screens, not
a collection of MangaReader/CamoProf-specific fixes.

## 2. Evidence and current diagnosis

The Shell grid in `core/Citadel.Shell/MainWindow.xaml` already uses `Auto` and
`*`, so the top-level structure is capable of fluid layout. The contract is
currently interrupted below it:

- `Host` does not explicitly require horizontal and vertical content stretch.
- Router transition layers are plain `ContentPresenter` instances without an
  explicit fill contract.
- `SettingTabs` does not explicitly make selected content fill its presenter.
- `SettingScrollViewerStyle` does not forward horizontal/vertical content
  alignment to its `ScrollContentPresenter`.
- screens choose raw root `Grid`, `ScrollViewer` and margins independently, so
  outer inset and scroll ownership can drift.
- MangaReader Cover Builder caps its entire content at `MaxWidth="920"` and
  aligns it left; increasing the window therefore cannot make its cards fill.
- the current uncommitted ChromeFit implementation grows the window from a
  viewport's scrollbar overflow. It can hide a sizing symptom but cannot fix a
  capped or non-stretching child.

The current issue is therefore an incomplete layout chain, not a shortage of
per-screen resize handlers.

## 3. Locked architecture

```text
Current monitor work area
└── MainWindow (preferred token size, startup clamp only)
    └── Shell workspace (`Auto` sidebar + `*` content)
        └── Router host (Stretch)
            └── transition layer (Stretch)
                └── module/built-in root view (Stretch)
                    └── SettingTabs when present (Stretch)
                        └── SettingViewport
                            ├── shared outer inset
                            ├── standard cards (Stretch)
                            └── feature-owned internal scroll regions
```

### 3.1 Ownership

| Owner | Responsibility |
|---|---|
| Shell | choose/clamp initial window bounds; provide a finite content viewport |
| Router | make each active transition layer fill the host; never inspect feature content size |
| `setting/Components` | own viewport, inset, stretch, card and overflow behavior shared by screens |
| built-in/module screen | choose the correct viewport mode and compose feature content |
| feature collection/table/reader | own intentional scrolling inside its finite region |
| standalone feature window | own its own chrome and sizing policy; never resize the main Shell |

A citizen module may reference shared viewport/components through
`Citadel.Setting`; it does not reference or call `Citadel.Shell`.

### 3.2 Shell window policy

- `WindowW` and `WindowH` remain the user's preferred startup size.
- Change the fresh-install default from `1180x720` to `1180x900`; retain
  `900x560` as the resizable minimum.
- At startup, clamp the preferred size and centered position to the monitor work
  area containing the launch point. Bounds are calculated in WPF DIPs after
  per-monitor DPI conversion.
- The clamp is a one-time chrome operation. Navigation, tabs, collection
  changes, async status updates and scrollbar events never resize the window.
- A monitor too small for the preferred size gets the largest legal window; its
  document viewport may scroll vertically.
- User resize, maximize and restore remain authoritative for the rest of the
  session. No auto-shrink or auto-grow follows them.

Do not use `SizeToContent`: module content and async state would make the Shell
jump during navigation and could exceed the monitor.

### 3.3 Shared `SettingViewport`

Add one reusable control in `setting/Components/Viewport.xaml(.cs)` with an
explicit `Mode`:

- `Contained` — default. Content gets finite available width/height; the
  viewport adds no scrollbar. Use this when a table, collection or overlay
  owns scrolling.
- `Document` — content fills the available width; horizontal scrolling is
  disabled; one outer vertical scrollbar appears only if the full document is
  taller than the viewport.

Both modes own:

- `HorizontalContentAlignment=Stretch`;
- `VerticalContentAlignment=Stretch` for `Contained` and top alignment for
  `Document`;
- one shared content inset matching the `SettingTabs` header inset;
- clipping at the viewport boundary;
- keyboard/mouse-wheel routing that does not steal input from an intentional
  inner scroller.

The paired `.xaml.cs` owns dependency properties and mode behavior. Screens do
not copy its template or restate its alignment/scroll settings.

### 3.4 Spacing and cards

- Add shared `SettingViewportInset` and `SettingSectionGap` resources.
- The viewport owns outer left/right/top/bottom inset.
- `SettingTabs` owns only its tab-header inset and divider.
- `SettingActionCard` and `SettingCardStyle` fill the width supplied by their
  parent and do not own screen-level horizontal margins.
- vertical separation between sibling sections uses `SettingSectionGap`.
- remove compensating local `4`, `5`, `14`, and `16` outer margins when a
  migrated viewport already owns that space.

This prevents double inset and keeps Library, Launcher, Runtime, Cover Builder
and future modules aligned without screen-local pixel corrections.

### 3.5 Sizing rules for child content

- `Grid` flexible content columns use `*`; labels/actions use `Auto` or a small
  semantic width where alignment across rows requires it.
- buttons are content-fit by default. A minimum width is allowed only when it
  protects a repeated action rhythm.
- text fields/search/path inputs consume `*` and keep left/vertical-center text
  behavior from `SettingField`.
- long paths, errors and status values wrap or trim with a tooltip; they may not
  increase the screen's desired width.
- fixed dimensions remain valid for semantic visuals: manga cover cards,
  cover preview, icons, toggles, progress tracks and the dedicated reader
  surface.
- `MaxWidth` is allowed for readable empty-state text, not for a standard screen
  or its primary card column.

### 3.6 Scroll ownership

| Surface | Owner | Expected overflow |
|---|---|---|
| Settings/Appearance/Gallery documents | `SettingViewport.Document` | vertical fallback only |
| Cover Builder/Runtime documents | `SettingViewport.Document` | vertical fallback only |
| Launcher profile table | `SettingTable` | internal horizontal/vertical when column minimums require it |
| Manga Library/History | collection `ScrollViewer` | internal vertical only |
| Chapter selector | chapter list | internal vertical only |
| Reader window | reader surface | reader-owned horizontal/vertical and zoom |

Never put an enabled outer vertical `ScrollViewer` around a `Grid` whose `*`
row contains a table or collection. WPF would measure that child with unbounded
height and transfer scroll ownership to the wrong layer.

### 3.7 Complete current-screen classification

| Current surface | Viewport classification |
|---|---|
| built-in Settings, Appearance, Module Layout and Gallery | `Document` |
| Blank citizen template | `Contained` |
| MangaReader Library and History | `Contained` |
| MangaReader Cover Builder | `Document` |
| MangaReader Chapter Selector | overlay inside Library; chapter list owns scroll |
| MangaReader ReaderWindow | standalone-window exception |
| CamoProf Launcher and reserved Editor | `Contained` |
| CamoProf Runtime | `Document` |

No current screen is left to choose an undocumented third root behavior.

## 4. Target file tree

```text
core/Citadel.Shell/
├── MainWindow.xaml(.cs)                 stretch host + startup bounds use
├── Router.cs                            stretch transition layers
└── WindowBoundsPolicy.cs                pure preferred-size/work-area clamp

setting/
├── SettingResources.xaml                viewport inset/gap and fill contract
├── Components/
│   ├── Viewport.xaml(.cs)               Contained/Document screen root
│   ├── Tabs.xaml(.cs)                   selected-content stretch
│   ├── ActionCard.xaml(.cs)             fill parent; no outer-screen margin
│   └── ...                              existing primitives unchanged unless required
└── Screens/
    └── SettingScreen.cs                 shared Document viewport composition

module/mangareader/
├── MangaReaderView.xaml                 shared tab host
├── Library/LibraryView.xaml             Contained viewport + collection scroll
├── History/HistoryView.xaml             Contained viewport + collection scroll
├── CoverBuilder/CoverBuilderView.xaml   Document viewport; no 920px cap
└── Reader/ReaderWindow.xaml             explicit standalone exception

module/camoprof/
├── CamoprofView.xaml                    stretched lazy tab hosts
├── Launcher/LauncherView.xaml           Contained viewport + table scroll
└── Runtime/RuntimeView.xaml             Document viewport

module/blank/
└── BlankView.xaml                       canonical Contained citizen template

tests/Citadel.Uia/
├── FluidLayoutTests.cs                  rendered size/overflow behavior
├── MainWindowTests.cs                   startup bounds and user-resize policy
└── SharedComponentBehaviorTests.cs      viewport/tab/card contracts
```

`ChromeFitCoordinator.cs`, `ChromeFit.cs`, and
`ChromeFitCoordinatorTests.cs` are removed rather than retained beside the new
contract.

## 5. Implementation sequence

### Phase 0 — preserve baseline and replace the abandoned direction

1. Record the current worktree and preserve unrelated changes.
2. Reverse only the uncommitted ChromeFit additions:
   - Shell coordinator field, construction, source hook and test seam;
   - attached viewport markers in Settings, Runtime and Cover Builder;
   - ChromeFit documentation section;
   - new coordinator, attached-property and coordinator-test files.
3. Do not reset or rewrite commit `01fe606`; it remains the shared-component
   baseline.
4. Run `git diff --check` before beginning the replacement so accidental
   overlap is visible.

### Phase 1 — make the host chain fluid

1. Set horizontal and vertical content alignment to `Stretch` on `Host` and
   the Settings popup host.
2. Set both alignments on every Router transition `ContentPresenter`.
3. Update `SettingTabs` so its selected content presenter fills row 2 and a
   selected `UserControl` receives the full available size.
4. Add rendered behavior tests proving that an active view equals the host's
   available content bounds at normal, minimum and enlarged sizes.

### Phase 2 — introduce shared viewport and spacing contracts

1. Implement `SettingViewport` and its two modes.
2. Forward alignment into its presenter instead of relying on WPF defaults.
3. Add shared inset/gap resources.
4. Make standard card surfaces stretch; move screen-level outer spacing out of
   `SettingActionCard` and into the viewport.
5. Add tests for:
   - contained content receiving finite height;
   - document content filling width;
   - document vertical fallback;
   - no document horizontal overflow from wrapping text;
   - no double inset between tabs, action cards and ordinary cards.

### Phase 3 — implement bounded startup chrome

1. Add a pure `WindowBoundsPolicy` operating on preferred bounds and current
   monitor work area.
2. Apply it once during initial Shell placement, before the first visible
   frame.
3. update fresh defaults to `1180x900` and keep Appearance's wording clear:
   values are preferred next-launch size and may be monitor-clamped.
4. Test centered clamp, negative-coordinate monitor, high-DPI conversion,
   smaller work area, minimum relationship, and no mutation after user resize.

### Phase 4 — migrate built-in Settings and the citizen template

1. Replace the raw screen `ScrollViewer` in `SettingScreen` with
   `SettingViewport.Document`.
2. Remove the old body margin and consume shared viewport inset/gap.
3. Keep control-semantic fixed widths in Appearance/Gallery, but remove fixed
   widths used merely to compensate for the old viewport.
4. Make problem/path/status text wrap or trim within the viewport.
5. Confirm the default Settings screen has no outer scrollbar at `1180x900`;
   at `900x560`, a vertical document scrollbar is allowed and horizontal
   overflow is not.
6. Migrate `module/blank/BlankView.xaml` to `SettingViewport.Contained` so every
   copied future citizen starts inside the global contract without editing
   Shell or shared code.

### Phase 5 — migrate MangaReader

1. Keep `MangaReaderView` as tab composition only.
2. Wrap Library and History content in `SettingViewport.Contained`; their title
   collections retain the only enabled scroller.
3. Remove duplicate outer margins from Library toolbar/status and History.
4. Use `SettingViewport.Document` for Cover Builder.
5. Remove Cover Builder's screen-level `MaxWidth=920` and left alignment; both
   primary cards fill the viewport.
6. Preserve fixed cover-preview geometry and readable empty-state maximums.
7. Keep Chapter Selector as a full-content overlay with its internal list
   scroll.
8. Do not alter ReaderWindow loading, three-chapter working set, zoom, overlays
   or reader scroll ownership.

### Phase 6 — migrate CamoProf

1. Make Launcher/Runtime lazy `ContentControl` hosts stretch explicitly.
2. Use `SettingViewport.Contained` for Launcher; the table retains internal
   overflow and finite height.
3. Align the toolbar and table to the same viewport edges; remove duplicate
   local five-pixel margins.
4. Use `SettingViewport.Document` for Runtime and remove its compensating root
   margins.
5. Ensure long runtime paths wrap without widening the screen.
6. Leave Editor visually blank but host it in the same contained viewport so
   future bulk UI starts from the global contract.
7. Do not change profile, browser, network, credential or runtime business
   logic.

### Phase 7 — cleanup and documentation

1. Delete abandoned ChromeFit code/tests and all remaining attached-property
   references.
2. Search current screens for raw outer `ScrollViewer`, top-level `MaxWidth`,
   `HorizontalAlignment="Left"`, and copied outer inset values; classify every
   remaining match as semantic or remove it.
3. Update `SHARED-UI-BEHAVIOR.md` only after the implementation and visual gates
   pass; until then it must not claim fluid behavior is implemented.
4. Update `module/README.md` so a new citizen starts with
   `SettingViewport.Contained` or `.Document`, including scroll-ownership rules.
5. Remove redundant styles/helpers made obsolete by the shared viewport. Do
   not delete feature logic, persistence, Credenz data or reader code as
   cleanup.

## 6. Potential bugs within scope

The following are part of implementation review, not deferred surprises.

| Severity | Potential bug / evidence | Big-picture disposition |
|---|---|---|
| High | ChromeFit can enlarge the window while a child remains capped at 920px, leaving blank space and preserving the real defect. | Remove overflow-driven window resizing; make the entire host chain fluid. |
| High | Measuring every installed module at startup would instantiate lazy views, trigger Runtime/network/filesystem work and violate module lifecycle. | Never scan inactive modules for desired size; enforce a shared viewport contract instead. |
| High | Wrapping table/library screens in an outer enabled `ScrollViewer` gives children unbounded height and can break virtualization or move scrolling to the page. | Use `Contained` mode; keep scrolling inside the feature region. |
| Medium | Async content, tab selection or error text can repeatedly change overflow and make ChromeFit grow the Shell after startup. | Window bounds are startup-only; dynamic content adapts inside the viewport. |
| Medium | Host/Router/tab content currently relies on framework defaults, so one template/default change can make an entire module render at desired size instead of filling. | Explicit stretch at every composition boundary plus rendered tests. |
| Medium | Cover Builder's top-level `MaxWidth=920` prevents fill on larger windows. | Remove the cap; keep only semantic preview sizing. |
| Medium | Long folder paths, runtime paths and status/error strings can create horizontal desired-size overflow. | Shared wrapping/trimming rules; horizontal document scroll remains disabled. |
| Medium | Toolbar left/right content can collide at minimum width or high DPI. | Verify at 900px/150% DPI; if combined content cannot fit, implement one adaptive layout in `SettingActionCard`, never a screen-local rearrangement. |
| Medium | Work-area math can be wrong on a secondary monitor with negative coordinates or different DPI. | Pure monitor-bound policy, pixel-to-DIP conversion and targeted tests. |
| Medium | Grow-only ChromeFit leaves a large window and blank space after navigating to a smaller screen. | Remove navigation-driven growth entirely. |
| Low | Existing local 4/5/14/16px margins can double the new viewport inset and recreate visual drift. | One migration search and explicit classification of every remaining root margin. |
| Low | `layout.json` size overrides can intentionally make a named feature child wider than its viewport. | Preserve user overrides, contain them inside the owning screen, and never let them resize Shell chrome; report/scroll inside that feature if necessary. |
| Low | Fixed manga cards and reader surfaces may be incorrectly "fluidized" and distort cover/page ratios. | Record them as semantic exceptions and protect with regression tests. |

If the minimum-width toolbar check fails, the shared adaptive ActionCard work is
part of this plan. It is not authorized as a CamoProf-only patch.

## 7. Validation matrix

### 7.1 Automated gates

| Gate | Pass condition |
|---|---|
| Shared build | `Citadel.Setting` builds with 0 warnings/errors |
| Shell build | current Debug Shell builds with 0 warnings/errors |
| Citizen builds | Blank, MangaReader and CamoProf build/deploy with 0 warnings/errors |
| Fluid host test | routed view and selected tab content equal available host bounds |
| Viewport test | Contained and Document modes obey stretch/scroll ownership |
| Bounds test | startup size clamps on normal, small, negative-coordinate and DPI-scaled work areas |
| Regression suite | full current Core/UI/UIA suite passes once after integration |
| Hygiene | `git diff --check`; no generated/runtime/user data tracked |

### 7.2 Live visual gates

Test the real WPF window, not screenshots inferred from unit tests:

| State | Expected behavior |
|---|---|
| default on a 1080p-or-taller work area | approximately `1180x900`; Settings launch has no accidental outer scrollbar |
| configured `1180x720` | content still fills width; long Settings documents may show only a vertical fallback scrollbar |
| minimum `900x560` | no Shell/screen horizontal scrollbar; document vertical fallback and table internal overflow are allowed |
| maximized | cards and tables expand to their viewport; no 920px content island |
| 125% and 150% DPI | no clipped tabs/buttons, toolbar collision or off-monitor startup |
| secondary monitor | centered/clamped within that monitor's work area |
| route/tab navigation | window bounds stay unchanged |
| async status/path/error updates | window bounds stay unchanged; text wraps/trims |
| Manga Library/History | collection scrolling remains internal and cards preserve size/ratio |
| CamoProf Launcher | two cards align to the same viewport edges; table remains finite and scrollable |
| Cover Builder/Runtime | ordinary cards fill width; vertical fallback appears only when genuinely required |
| ReaderWindow | fullscreen/zoom/continuous chapter behavior is unchanged |

Visual PASS requires captured live evidence at default, minimum and maximized
sizes on the available hardware. Monitor/DPI configurations unavailable on that
hardware require deterministic policy coverage and must not be claimed as live
visual proof.

## 8. Completion audit

Before declaring implementation complete:

1. map every current root view to `Contained`, `Document`, or standalone-window
   exception;
2. prove the host/Router/tab/viewport stretch chain through rendered sizes;
3. prove there are no ChromeFit references or overflow-driven Shell resize
   handlers left;
4. inspect every remaining top-level fixed width/max width/root margin and
   record why it is semantic;
5. prove internal collection/table/reader scrolling still owns its region;
6. verify startup bounds and unchanged bounds after navigation and async load;
7. run the automated and live visual matrices;
8. update the shared behavior document from `planned` to `implemented` only
   after all gates pass.

## 9. Done definition

Done means Citadel has one documented and implemented fluid desktop-first
contract; current and future screens receive a finite shared viewport; standard
cards fill consistently; intentional inner scrolling remains intact; the Shell
uses one monitor-bounded startup size and never follows feature overflow; the
abandoned ChromeFit code is removed; all in-scope risks above are closed or
explicitly proven semantic exceptions; and automated plus live visual gates
pass before commit.

## 10. Non-goals

- changing MangaReader decoding/cache/three-chapter/zoom behavior;
- changing CamoProf profile, Google, GitHub, Credenz, network or runtime flows;
- changing module discovery or the public `IModule` contract;
- persisting window position/state across launches;
- making arbitrary user `layout.json` overrides responsive;
- eliminating intentional scrolling from tables, collections or the reader;
- redesigning feature visuals unrelated to viewport, spacing or overflow.

## 11. Implementation evidence

- `ChromeFitCoordinator`, its attached property, event hooks and tests were
  removed; no `ChromeFit` reference remains in Core, Setting, modules or tests.
- Shell host, Router layers, Settings popup host and selected tab content now
  state `Stretch` explicitly.
- `SettingViewport` owns `Contained` and `Document` modes, common five-pixel
  inset, horizontal containment and vertical document fallback.
- Settings, Blank, MangaReader Library/History/Cover Builder, and CamoProf
  Launcher/Editor/Runtime use the classified viewport mode; ReaderWindow and
  feature-owned inner scrollers remain unchanged.
- startup bounds use the preferred `1180x900` default, the active monitor's
  effective DPI/work area, and a one-time pure clamp policy.
- seven focused fluid-layout tests cover rendered stretch, overflow ownership,
  lazy module creation, async growth, negative monitor coordinates and 150%
  DPI conversion.
- full suite: 108 Core + 14 UI + 194 UIA = 316 tests pass.
- live 96-DPI pass: `1180x900`, `900x560`, and maximized; Settings,
  MangaReader Library/Cover Builder, and CamoProf Launcher/Runtime were
  inspected. Cards fill, no horizontal screen scrollbar appears, and document
  overflow remains vertical.
- the available machine exposes one 2560x1440 monitor at 96 DPI. Secondary
  monitor and 125%/150% behavior are therefore policy-tested rather than
  claimed as live visual evidence.
