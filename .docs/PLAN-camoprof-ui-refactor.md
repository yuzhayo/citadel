# PLAN: CamoProf UI refactor — Launcher / Editor / Runtime

Status: **IMPLEMENTED — awaiting LO visual approval; provider wiring deferred**  
Scope owner: `module/camoprof/` only. Provider-check wiring is explicitly
deferred.

## 1. Goal

Refactor CamoProf from one long screen into three clear internal tabs:

1. **Launcher** — the default tab; a compact Chrome-launcher-style list of
   saved browser profiles, with reserved Google/GitHub status columns and a
   Launch action on every row.
2. **Editor** — add and remove saved profiles. Destructive profile management
   no longer competes visually with daily launch actions.
3. **Runtime** — inspect and install Python, venv, packages, and the Camoufox
   browser payload.

The refactor must preserve the flows already tested by LO: add/remove profile,
launch, Google verification, runtime setup, safe deletion, and cleanup when
navigating away from CamoProf.

This is a CamoProf refactor, not a shell redesign. `core/`, `setting/`,
MangaReader, the installer, and the public citizen contract stay unchanged.

## 2. Why the current screen is being split

The current `CamoprofView.xaml.cs` owns all of these concerns in one class:

- runtime detection and setup;
- profile filesystem scanning and size calculation;
- add, launch, verify, and delete flows;
- pyhost ownership and the profile-to-session registry;
- UI busy state and every status message.

That implementation works, but its single `_busy` flag and single status area
make unrelated operations look like one workflow. The view is also the wrong
place to prepare two provider columns. Their Google/GitHub verification and
headed/headless behaviour is browser orchestration and will be wired in a
separate follow-up, not hidden inside row-template logic.

## 3. Locked UX contract

### 3.1 Shell and tabs

- The shell content header remains **CamoProf**.
- Internal tab order is **Launcher / Editor / Runtime**.
- **Launcher is selected by default** whenever CamoProf is opened.
- Tab pills use the same screen-local visual treatment as MangaReader:
  complete rounded corners, normal/hover/selected states from Citadel theme
  resources, visible keyboard focus, and no broken half-pill highlight.
- Each tab owns its content and any required scrolling. There is no outer
  ScrollViewer wrapping the complete workspace.
- No new shared UI component is created in `setting/Components/`. Two local
  tab implementations are not enough reason to broaden this refactor into a
  cross-module UI migration.

### 3.2 Launcher tab

Launcher is optimized for daily use, not profile editing:

```text
Check browser: [ Show browser while checking  disabled ]  [ Refresh ]

Profile                    Google          GitHub          Action
work-main                  [ Check* ]      [ Check* ]      [ Launch ] [ Verify Google ]
personal                   [ Check* ]      [ Check* ]      [ Launch ] [ Verify Google ]

* provider status wiring is deferred
```

- One row represents one persistent Camoufox browser profile.
- Columns are **Profile / Google / GitHub / Action**.
- Google and GitHub cells are disabled `Check` buttons in this refactor. A
  nearby helper text says `Provider checks will be connected later`; they do
  not pretend to report account state.
- `Show browser while checking` is rendered as a disabled `SettingToggle` so
  the final toolbar proportion can be reviewed now without giving it a fake
  effect.
- The existing, working Google `session.verify` flow remains available as a
  temporary `Verify Google` action in the row Action area. The later wiring
  replaces that transitional action with the Google status-cell button.
- Opening CamoProf never launches browsers and never checks accounts
  automatically.
- **Launch remains headed**, preserving today's interactive behaviour.
- Refresh reloads the profile names. Provider cells remain disabled `Check`.
- Empty state: `No profiles saved — add one in Editor.`
- A row whose browser is already tracked as open changes its Action button from
  `Launch` to `Close`; a second Launch is never sent. `Close` performs the
  normal `session.close` path and returns the row to `Launch`. If the browser
  was manually closed, the next operation handles `BROWSER_GONE`, clears
  `Running`, and allows Launch again.

### 3.3 Editor tab

Editor owns the existing management flow:

```text
[ profile name                                      ] [ Add & login ]

Saved profiles
profile name             last modified       size             [ Delete ]
```

- `Add & login` retains current semantics: validate the name, open a headed
  persistent browser, add the new profile to the saved list, clear the field,
  and make the profile immediately visible in Launcher.
- Name validation remains `[A-Za-z0-9._-]+`, excluding `.` and `..`.
- Duplicate names remain a loud, non-destructive error.
- Delete retains the proven safety sequence:
  1. confirmation;
  2. close a tracked session first;
  3. resolve the absolute path and prove it remains under the profile root;
  4. delete recursively only after close succeeds or absence is proven;
  5. refresh Editor and Launcher.
- `TIMEOUT` and `BROWSER_CLOSE_FAILED` abort deletion. They do not become
  permission to partially delete a live profile.
- Profile size remains an Editor-only diagnostic. Launcher does not recursively
  enumerate files merely to render its daily-use rows.
- When there are no profiles, the saved-profile header, table, helper copy, and
  empty-state label are all hidden. The area below Add & login remains blank and
  reserved for the future **Add bulk** feature.

### 3.4 Runtime tab

- The existing four rows remain: Python, venv, packages, browser.
- `Setup runtime`, indeterminate progress, progress text, idempotent skipping,
  the cross-process setup lock, pinned Python package/hash, and retry behaviour
  remain unchanged.
- Runtime includes a small `Refresh status` action. Status is checked when
  Runtime is first opened or explicitly refreshed, not during initial Launcher
  rendering.
- Setup may not begin while a CamoProf browser session is open. The UI explains
  that the user must close it first; it does not silently kill an active login.
- While setup is running, Launcher and Editor are disabled so they cannot spawn
  pyhost from a venv being modified.

## 4. Target ownership and file tree

`module/sharedLogic/` remains the cross-citizen Python/C# bridge. The new
`module/camoprof/sharedLogic/` is intentionally narrower: code reused by two
CamoProf tabs, invisible to every other citizen.

```text
module/camoprof/
├── CamoprofModule.cs                  unchanged citizen entry
├── CamoprofView.xaml                  tab shell and screen-local tab styles
├── CamoprofView.xaml.cs               composition + cross-tab refresh only
├── layout.json                        root tab-panel slots
├── module.json                        unchanged identity
├── Module.Camoprof.csproj             unchanged project boundary
├── Launcher/
│   ├── LauncherView.xaml
│   ├── LauncherView.xaml.cs           row actions and Launcher view state
│   └── LauncherProfileRow.cs          presentation state for one profile row
├── Editor/
│   ├── EditorView.xaml
│   └── EditorView.xaml.cs             add/delete flow and Editor-only status
├── Runtime/
│   ├── RuntimeView.xaml
│   └── RuntimeView.xaml.cs            status/setup flow only
└── sharedLogic/
    ├── BrowserSessionCoordinator.cs   one PyHost + session ownership/gating
    ├── ProfileCatalog.cs              scan/validate/safe-delete filesystem API
    └── ProfileEntry.cs                neutral saved-profile record
```

Rules:

- `CamoprofView` composes children and owns their shared dependencies; it does
  not absorb tab-specific handlers.
- Logic used by only one tab stays in that tab folder.
- `ProfileCatalog` is shared inside CamoProf because Launcher reads profiles
  and Editor mutates them.
- `BrowserSessionCoordinator` is shared inside CamoProf because Launcher and
  Editor both act on the same pyhost/session registry.
- `RuntimeSetup`, `PyHost`, and `CredenzPath` remain in root
  `module/sharedLogic/cs` because future citizens can consume them.
- No MVVM framework, dependency-injection container, new NuGet dependency, or
  speculative generic repository is added.

## 5. State and event flow

```text
CamoprofView
├─ owns ProfileCatalog
├─ owns BrowserSessionCoordinator ── owns one PyHost and session map
├─ LauncherView ── reads catalog, launches/checks through coordinator
├─ EditorView   ── adds/deletes through catalog + coordinator
└─ RuntimeView  ── calls RuntimeSetup; reports setup busy to parent

Editor ProfilesChanged ──> CamoprofView ──> Launcher.RefreshAsync()
Runtime SetupBusy       ──> CamoprofView ──> disable Launcher + Editor tabs
Lifetime ends           ──> BrowserSessionCoordinator.Dispose()
```

- There is one pyhost owner per CamoProf view, exactly as today.
- Browser operations are serialized by the coordinator. Launch, legacy Google
  verify, and profile deletion can never race on the same persistent directory.
- Runtime busy state is separate from each tab's presentation state; the old
  screen-wide `_busy` flag disappears.
- Async continuations update WPF controls only on their owning Dispatcher.
- Navigation away disposes the coordinator once. Graceful `shutdown` → stdin
  EOF → process-tree kill remains the cleanup ladder.

## 6. Explicitly deferred provider wiring

The following controls are layout placeholders only in this refactor:

- Google row `Check` button;
- GitHub row `Check` button;
- headed/headless `Show browser while checking` toggle.

They remain visibly disabled and carry accessible names/tooltips explaining
that provider checks are not connected yet. No code in `module/sharedLogic/`,
no pyhost command, and no provider URL/heuristic changes in this plan.

The separate wiring plan will decide the provider contract, headed/headless
session lifecycle, error mapping, and whether an existing user-launched session
or a temporary session performs each check. Those decisions are deliberately
not pre-baked here.

The existing physical profile root `Credenz/google/profiles/<name>` remains
unchanged. Renaming it would require a user-data migration and is outside this
UI refactor.

## 7. Layout and theme contract

- All colours, font sizes, radii, padding, hover, selected, disabled, and border
  values come from existing dynamic Citadel resources.
- Use `SettingButton`, `SettingField`, and `SettingToggle`; the selectable
  profile rows remain screen-local because `SettingTable` has no selection or
  per-cell action contract.
- Parent-name-scope wrappers remain available to `LayoutApplier.FindName`:
  `LauncherPanel`, `EditorPanel`, and `RuntimePanel`.
- `layout.json` replaces stale inner-control slots with these root wrappers.
  Existing user overrides for `ProfileList`/`StatusText` become inert by the
  documented fail-soft rule; no core token migration is added.
- Minimum usable width must keep Profile + two provider buttons + Launch visible
  without text overlap. At narrower widths, the tab owns horizontal scrolling;
  the shell card is never stretched or clipped by hard-coded screen width.

## 8. Implementation sequence

### Step 1 — Extract shared CamoProf ownership without visual change

- Create `ProfileCatalog`, `ProfileEntry`, and
  `BrowserSessionCoordinator` from the proven current code.
- Keep path validation, deletion order, host startup, structured errors, and
  disposal semantics unchanged.
- Build CamoProf before proceeding.

### Step 2 — Split the three views

- Move runtime UI/handlers into `Runtime/`.
- Move profile management into `Editor/`.
- Build Launcher list and move launch behaviour into `Launcher/`.
- Reduce `CamoprofView` to tab composition, child events, and lifetime ownership.
- Update `layout.json` wrappers.

### Step 3 — Prepare provider controls without wiring

- Add disabled Google/GitHub row buttons and the disabled headed/headless
  toggle, including honest helper text and automation names.
- Keep current Google verification functional through the transitional
  `Verify Google` row action.
- Do not edit the C# pyhost client, Python host, protocol README, or tests.

### Step 4 — Interaction hardening

- Synchronize Editor changes into Launcher without recreating the complete
  CamoProf screen.
- Prevent setup/session conflicts.
- Preserve the intentionally blank Editor reservation when empty; handle
  runtime-missing, browser-gone, timeout, and delete-failure states visibly.
- Verify keyboard tab order, focus, automation names, and non-colour statuses.

### Step 5 — Validate and show LO the result

- Rebuild the CamoProf citizen and verify its deployed module payload.
- Run the unchanged pyhost regression suite and the full Citadel suite.
- Launch the rebuilt app and perform the visual/interaction checklist below.
- Do not commit until LO has seen and approved the result.

## 9. Validation gates

| Gate | Evidence | Pass condition |
|---|---|---|
| V1 | `dotnet build module/camoprof/Module.Camoprof.csproj` | 0 warnings, 0 errors |
| V2 | `python -W error::ResourceWarning -m unittest module.sharedLogic.tests.test_pyhost -v` | existing suite remains green, no resource warnings |
| V3 | `dotnet test Citadel.slnx --no-build` after rebuild | all Core/UI/UIA tests pass |
| V4 | module deployment check | CamoProf DLL and sibling sharedLogic payload are current; no stale files |
| V5 | visual tab pass | Launcher/Editor/Runtime render; selected tab pill is fully rounded in normal, hover, and selected states |
| V6 | profile pass | existing profile launches; Add & login appears in both Editor and Launcher; Delete removes it safely |
| V7 | deferred-control pass | Google/GitHub Check buttons and headed/headless toggle are visibly disabled, explain why, and invoke no backend action; transitional Verify Google still works |
| V8 | lifecycle pass | navigation away closes all CamoProf-owned browsers/pyhost; no orphan process remains |
| V9 | runtime pass | setup remains idempotent; active browser blocks setup with a clear message; setup disables other tabs |
| V10 | shell pass | Settings → PROBLEMS is empty and MangaReader/Blank still open normally |
| V11 | hygiene | `git diff --check` passes; no changes outside the allowed file set |

## 10. Allowed change set

Allowed:

- `module/camoprof/**`
- this plan file

Not allowed in this plan:

- `core/**`, `setting/**`, `module/mangareader/**`, `module/blank/**`
- `module/Citizen.targets`, solution/project discovery, installer/release workflow
- Credenz directory migration or deletion of existing profiles
- pyhost/client/protocol/provider-check changes
- stealthB, proxy/FTF, automatic periodic login checks, background browser pool
- new shared UI primitives or external dependencies

## 11. Done definition

The refactor is done only when:

- the screen visibly matches the three-tab contract;
- `CamoprofView` is a small composition root rather than the implementation of
  all three features;
- daily Launch actions, destructive Editor actions, and Runtime setup have
  separate UI and separate busy/status state;
- Google/GitHub Check buttons and the headed/headless toggle occupy their final
  proportional layout but are honestly disabled pending the separate wiring
  task;
- the current Google Verify action remains functional during that transition;
- all current safety/lifecycle behaviour remains intact;
- the full automated and live visual gates pass; and
- LO approves the visible result before commit.
