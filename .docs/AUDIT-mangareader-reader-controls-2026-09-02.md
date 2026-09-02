# Audit: MangaReader immersive Reader WIP

Date: 2026-09-02  
Scope: current uncommitted Reader/shared-component implementation  
Contract: `.docs/PLAN-mangareader-reader-controls.md`  
Repair plan: `.docs/PLAN-mangareader-reader-controls-repair.md`  
Verdict before repair: **FAIL**
Verdict after repair: **PASS on available hardware; multi-monitor/high-DPI live
coverage remains an explicitly reported hardware limitation**

## Baseline evidence

- `dotnet build module\mangareader\Module.Mangareader.csproj -c Release --no-restore -m:1`
  passes with 0 warnings and 0 errors.
- `dotnet test Citadel.slnx -c Release --no-restore -m:1` passes 382/382
  existing tests: Core 108, UI 14, UIA 204, Archive 32, Library 24.
- There is no `tests/Module.Mangareader.Reader.Tests` project. The four new
  shared UI assertions cover only Drawer default/open intent/backdrop and chrome
  title binding. They do not execute the Reader engine or feature integration.
- No live WPF PASS is claimed. Static inspection and the missing behavioral
  coverage contradict completion even though the existing suite is green.

## Confirmed findings

### C1 — loaded chapters are never published to the bound collection

- Severity: Critical
- Evidence: `ReaderChapterCoordinator` mutates `_surfaces`, but its constructor
  assigns `Surfaces = new(_surfaces)`, which creates a separate copied
  `ObservableCollection`. `ChapterList` binds to `Surfaces`.
- Impact: decoded chapters can exist internally while the visible Reader stays
  empty.
- Root fix: expose the coordinator-owned collection itself through a read-only
  contract; never copy it.

### C2 — successful load leaves a blocking loading layer in place

- Severity: Critical
- Evidence: `FrameContentHost.HideLoading` and `SetStatusDetail` are no-ops;
  `ReaderWindow` supplies a no-op progress callback and never collapses
  `LoadingPanel` after `StartLoadAsync` succeeds.
- Impact: the full-window loading layer remains visible and hit-testable, so the
  primary Reader workflow is unusable.
- Root fix: make loading/progress/error an explicit host/state contract and let
  the coordinator close the successful transition exactly once.

### C3 — two independent visual composition trees exist

- Severity: Critical
- Evidence: `ReaderWindow.xaml` instantiates pages, Dim, backdrop, Overlay,
  chrome, Drawer, toast, and loading layers. `ReaderFeatureHost` then iterates
  every `ReaderLayer`, creates another presenter set, and appends it to
  `RootGrid`. It mounts newly created feature instances rather than the visible
  XAML instances.
- Impact: controllers subscribe to controls/state that are not necessarily the
  controls the user sees. Multiple features also overwrite the same Toast
  presenter content.
- Root fix: one set of stable named XAML hosts; the host consumes them and never
  creates a second tree. Visual and nonvisual registrations must be distinct.

### C4 — feature host and coordinator lifetimes are not owned

- Severity: Critical
- Evidence: `ReaderWindow` discards the new `ReaderFeatureHost`; therefore
  Overlay, Drawer, chrome, auto-scroll, and other feature subscriptions are not
  disposed. The host itself disposes in forward rather than reverse order.
  `ReaderWindow` manually disposes only selected controllers and calls
  `ReaderChapterCoordinator.Close`, while coordinator `Dispose` returns early
  after close and therefore cannot release its semaphore/token resources.
- Impact: duplicate handlers, timers, stale windows, and resource leaks can
  survive close/reopen.
- Root fix: retain one host, cancel one root lifetime, reverse-dispose all
  features, flush/dispose preferences, then dispose the coordinator once via an
  idempotent close path.

### H1 — original rolling-reader behavior regressed

- Severity: High
- Evidence: `ReaderScroller_ScrollChanged` reports a generic mouse activity but
  never calls coordinator `NotifyScroll`, so viewport-center chapter rotation
  and rolling preload are disconnected. The previous maximized startup contract
  was also removed from XAML.
- Impact: natural continuous chapter progression does not work as before and
  Reader startup behavior changes outside the feature scope.
- Root fix: restore the baseline engine first, route every viewport mutation
  with an explicit origin, and retain maximized startup.

### H2 — zoom has multiple incomplete state paths

- Severity: High
- Evidence: no `ReaderZoomController` is instantiated. Ctrl+wheel is only
  reported as activity, Ctrl+0 writes a DP directly through an alternate window
  handler, Drawer buttons mutate session state directly, coordinator
  `ZoomScale` is initialized once, and `FrameContentHost.ZoomScale` normally
  falls back to 1 because it checks an unrelated resource lookup.
- Impact: pointer anchoring/coalescing are lost and controls disagree about the
  effective zoom.
- Root fix: instantiate exactly one existing zoom controller and route
  Ctrl+wheel, Ctrl+0, Drawer buttons, layout, and coordinator calculations
  through it.

### H3 — chapter navigation is not a real load/jump transaction

- Severity: High
- Evidence: `NavigateToChapter` is `async void` without awaited loading. It
  immediately changes the active index and emits history, but does not load the
  target surface or scroll to page one. There is no navigation generation.
  Drawer Next uses the rolling `Surfaces.Count` instead of total title chapters,
  and the dropdown is never populated.
- Impact: chapter actions can point at missing surfaces, emit false history,
  use the wrong boundary, and allow stale loads to win.
- Root fix: one coordinator transaction with monotonic generation, shared load
  gate/cache, newest-only commit, page-one positioning, one history event, then
  neighbor preparation.

### H4 — the advertised stable child contract is not stable

- Severity: High
- Evidence: `ReaderFeatureContext` exposes concrete `ReaderWindow`, the mutable
  `ReaderSessionState`, concrete coordinator, and input router. Every session
  property has a public setter. `ReaderDrawerView` claims typed contributions
  but hardcodes every feature control and directly mutates shared state.
- Impact: adding a feature still couples it to parent internals and Drawer
  source; ownership cannot be enforced or tested.
- Root fix: expose read-only state plus typed command/viewport/chapter/input
  interfaces; render ordered Drawer contribution descriptors gathered from
  catalog features.

### H5 — central input routing is neither central nor safe

- Severity: High
- Evidence: `ReaderInputRouter` and an alternate
  `ReaderWindow_PreviewKeyDown` both implement shortcuts. Router close/fullscreen
  events are not consistently subscribed. Click recognition uses a fixed
  3-DIP Manhattan threshold, has no valid-down tracking, ignores neither
  interactive descendants nor scrollbar/Drawer bounds, and returns for every
  click when a pinned Drawer is open.
- Impact: Esc/F11/hotkeys may do nothing or run competing paths; ordinary
  control clicks and drag gestures can become Overlay navigation.
- Root fix: one preview router using system drag thresholds, valid pointer
  capture state, interactive-descendant exclusion, viewport coordinates, and
  typed commands.

### H6 — Overlay violates pass-through and navigation behavior

- Severity: High
- Evidence: the full-window `ReaderOverlay` is hit-test-visible with transparent
  Borders, blocking underlying interaction. `SetEnabled` is never driven and
  ignores beginning/end arguments. Menu always opens instead of toggling.
  Previous/Next scroll immediately rather than through a 180 ms coalesced
  navigator.
- Impact: wheel/scrollbar/touch/control use can be obstructed; boundaries,
  pinned Drawer behavior, and navigation animation do not match the contract.
- Root fix: hit-test-free geometry plus central click resolution and one
  elapsed-time/coalesced viewport navigator integrated with boundary loading.

### H7 — Drawer layout and actions are incomplete

- Severity: High
- Evidence: Drawer width binds its own `ActualWidth` through
  `QuarterWidthConverter`, producing zero at initial measure instead of 25% of
  Reader width. The shared Drawer root and surface share one width, so it cannot
  provide a full visual-only backdrop. Only Zoom In/Out have XAML Click wiring;
  chapter, fullscreen, auto-scroll, Pin, Dim reset, and global Reset do not.
  The chapter ComboBox occupies a fixed 40-DIP column while the star column is
  unused. The local Dim reset is absent, several button labels are blank, speed
  direction is reversed, and nested ScrollViewers duplicate scrolling.
- Impact: Drawer may be invisible and most required features are unreachable.
- Root fix: parent-relative shared Drawer fraction, one scroller, typed
  contributions, complete bindings/actions, and the locked ordered layout.

### H8 — fullscreen native interop and restore are invalid

- Severity: High
- Evidence: fullscreen uses `WorkArea` rather than monitor bounds; native
  monitor fields are declared as WPF `Rect` doubles instead of Win32 integer
  `RECT`; Right/Bottom are passed as Rect Width/Height; `ResizeMode` and monitor
  context are not saved; state becomes fullscreen even if monitor lookup fails.
- Impact: taskbar coverage, geometry, DPI conversion, and exact restoration are
  unreliable and may corrupt window state.
- Root fix: correct native structs and `rcMonitor`, explicit pixel-to-DIP
  conversion, complete restore snapshot, and failure rollback before state
  commit.

### H9 — preferences are neither correctly validated nor persisted from state

- Severity: High
- Evidence: schema version is serialized as string, Dim snaps to integers rather
  than 5, input is unbounded, malformed individual fields cannot be recovered
  independently through the current typed deserialize, and temp cleanup is not
  guaranteed. Each store creates a random named Mutex already owned by its
  constructing thread, so it is neither keyed by path nor safe for background
  debounce writes. Reader state changes are never synchronized back into the
  store; close starts an unawaited save and never disposes it.
- Impact: settings can fail to save, deadlock/throw during ownership release,
  overwrite incorrectly, or silently keep stale values.
- Root fix: numeric schema v1, bounded field-level parse, clamp/snap, normalized
  path process gate, unique-temp atomic replacement with cleanup, explicit state
  synchronization, flush, and disposal.

### H10 — Dim and auto-scroll are attached to the wrong abstractions

- Severity: High
- Evidence: `DimOverlay` covers `RootGrid`, is not bound to the content viewport,
  and remains hit-testable. Auto-scroll accesses the concrete window/coordinator,
  uses a private boolean that cannot survive asynchronous `ScrollChanged`, does
  not receive viewport resize, Drawer/loading/deactivation/chapter/zoom stop
  events reliably, and treats any reported activity identically.
- Impact: UI and scrollbar can be dimmed/blocked; auto-scroll can stop from its
  own movement or continue through states that must stop it.
- Root fix: viewport-scoped hit-test-free Dim host and end-to-end typed activity
  origins for every scroll mutation and stop condition.

### M1 — shared Drawer animation lifecycle is incomplete

- Severity: Medium
- Evidence: `SettingDrawer` has no unload/reload or size-change cleanup and uses
  the control's `ActualWidth` as panel width. Backdrop opacity is always zero.
- Impact: resize while closed/open can leave incorrect translation; animation
  clocks can outlive a visual lifecycle; shared behavior cannot satisfy its own
  contract.
- Root fix: explicit fraction DP, template-part ownership, size recomputation,
  backdrop state, animation cancellation, and reload-safe lifecycle.

### M2 — shared chrome starts invisible and blocks while invisible

- Severity: Medium
- Evidence: XAML begins at `Opacity=0` and `IsHitTestVisible=true`; Loaded again
  forces hidden rather than showing first. The 36-DIP surface remains an input
  shield while transparent. Keyboard focus and pressed/drag hold are absent.
  Unload sets `_disposed=true`, but reload never resets it and can subscribe a
  second time while future reveal/dispose calls no-op.
- Impact: first-open behavior is wrong, page input is blocked at the top, and
  unload/reload can leak handlers or disable fading.
- Root fix: visible-first state, opacity-linked hit testing, 6-DIP trigger-only
  behavior, hold reasons, and idempotent attach/detach that supports reload.

### M3 — toast and miscellaneous adapters have incomplete cleanup/dead code

- Severity: Medium
- Evidence: toast timers are stopped but Tick handlers are not consistently
  detached. Coordinator contains dead `UpdateTitle`, an unused initial-index
  field after construction, and `async void` wrappers that swallow errors.
  Activity hub owns an idle timer that never starts and reports mouse-leave as
  manual movement. `QuarterWidthConverter` exists only to support the incorrect
  self-width binding.
- Impact: lifecycle reasoning stays ambiguous and redundant paths make future
  regressions likely.
- Root fix: remove the dead paths after their authoritative replacements pass;
  keep one owner per timer/event/behavior.

### M4 — current tests are false confidence for the Reader scope

- Severity: Medium
- Evidence: 382/382 passes without loading `ReaderWindow` or exercising the
  coordinator, host, input router, fullscreen, Dim, auto-scroll, preferences,
  or Reader lifecycle. The Drawer animation assertion accepts either any
  animation clock or a negative base value and does not prove final geometry.
- Impact: severe runtime breakage appears green.
- Root fix: add linked-source Reader behavior tests and stronger shared WPF
  tests whose assertions fail if the protected behavior is removed.

## Strong decisions to preserve

- The original Reader engine has a coherent cache/rolling/offset model and can
  be extracted rather than rewritten.
- Feature files are already separated by behavior, which is a useful ownership
  starting point.
- The Shell-private DWM helper migration to the shared Setting assembly is the
  correct direction and currently builds.
- No third-party dependency or Downloader/Archive scope was introduced.
- Citizen deployment isolation remains intact in the baseline build.

## Root repair order

1. Add fail-first Reader coverage and restore the original engine.
2. Collapse composition/lifecycle and close the typed contract.
3. Repair shared Drawer/chrome in isolation.
4. Connect the single input/activity/navigation paths.
5. Wire Drawer contributions and remaining features through owners.
6. Remove all superseded paths, then run full automated and live WPF gates.

## Live evidence findings discovered during repair

These findings were recorded before their repair. They extend the pre-edit
audit above; a later resolution section must map each item to its root fix and
proof.

### L1 — shared ComboBox selected-item rendering ignores the display contract

- Severity: High
- Evidence: the live Reader Drawer screenshot renders
  `ReaderChapterChoice { Index = 0, Title = Chapter 1 }` in the closed chapter
  selector even though the control declares `DisplayMemberPath="Title"`.
- Impact: the chapter selector is not usable as a chapter-title control, and
  any other shared styled ComboBox with object items can expose an implementation
  representation instead of its requested display property.
- Root fix: characterize the shared ComboBox with an object-item display test,
  then repair the shared selected-item presenter contract. Do not special-case
  the Reader with a hard-coded chapter string.

### L2 — the Dim screenshot was captured before Drawer animation completion

- Severity: Evidence blocker
- Evidence: the live harness waited only 80 ms after an ordinary action closed
  the Drawer, then another 40 ms before capture, while the locked close
  animation lasts 200 ms. The resulting screenshot contains a partially
  translated Drawer sliver.
- Impact: state assertions can pass while visual evidence still depicts a
  transient frame; this screenshot cannot support a visual PASS.
- Root fix: make the live harness wait for final visual geometry, not only the
  logical `IsDrawerOpen` state, and recapture Dim after the Drawer has fully
  closed.

### L3 — the first live pass omitted normal and minimum Reader sizes

- Severity: Evidence blocker
- Evidence: the first harness pass covered maximized and fullscreen states but
  did not exercise the locked normal/minimum-size cases or prove that the
  proportional Drawer avoids horizontal overflow at those sizes.
- Impact: a 25% Drawer can pass on a wide monitor while clipping its contents
  in the narrower layouts it was specifically designed to support.
- Root fix: add disposable live normal/minimum-size checks, verify exact 25%
  width and no horizontal Drawer scrolling, and capture the minimum-size
  Drawer before claiming visual completion.

### L4 — the first live Drawer image does not reflect the requested initial chapter

- Severity: High until isolated
- Evidence: the disposable Reader was constructed for Chapter 2, while the
  first Drawer image displayed the Chapter 1 choice.
- Impact: this can indicate either a selected-item binding lifecycle bug or an
  unintended initial rolling-surface commit; both would make Drawer navigation
  misleading even after its label renderer is fixed.
- Root fix: add a live assertion that the coordinator and visible selector both
  remain on the requested initial chapter after neighbor preparation. If the
  coordinator is correct, close the selection binding lifecycle at the Drawer
  contribution boundary; if it is not, repair initial anchor preservation in
  the single chapter coordinator. Do not mask the mismatch by formatting the
  wrong item.

### L5 — top-trigger geometry is measured against the wrong coordinate space

- Severity: High
- Evidence: live maximized Reader QA placed the pointer inside the intended
  top strip, but `MouseEventArgs.GetPosition(Window)` reported Y=7 because the
  maximized/custom-chrome non-client inset participates in Window coordinates.
  The locked `<= 6 DIP` comparison therefore did not reveal the chrome.
- Impact: the only allowed hidden-chrome reveal path can be unreachable in the
  Reader's default maximized state.
- Root fix: calculate the trigger position relative to the shared chrome
  control's own client surface, where 0–6 DIP has one stable meaning across
  normal and maximized states. Keep the locked six-DIP size; do not compensate
  with a larger screen-specific threshold.

### L6 — Overlay boundary failures can escape a fire-and-forget click

- Severity: High
- Evidence: `ReaderOverlay` intentionally does not await its click command,
  while `ReaderViewportNavigator.StepAsync` catches cancellation only. A CBZ
  neighbor failure from `PrepareBoundaryAsync` therefore faults the abandoned
  task rather than surfacing a non-blocking Reader warning.
- Impact: a recoverable adjacent-chapter problem can become an unobserved task
  failure and gives the user no actionable feedback.
- Root fix: keep boundary preparation in the single coordinator, but terminate
  expected boundary exceptions inside the navigator and route one warning
  through the Reader notification owner. Add an awaited regression proving the
  click path does not rethrow.

### L7 — shared chrome double-click bypasses fullscreen resize policy

- Severity: High
- Evidence: the Max/Restore button correctly disables for `NoResize`, but the
  chrome surface double-click changes `WindowState` without checking
  `ResizeMode`. Reader fullscreen uses `NoResize` while retaining the shared
  chrome.
- Impact: double-click can put the native window into maximized state while
  Reader state still says fullscreen, breaking exact monitor bounds and the
  subsequent restore contract.
- Root fix: define one shared `CanResize` policy used by both button state and
  surface double-click; consume the gesture without changing state when resize
  is unavailable. Also make native drag failure non-fatal.

### L8 — input routing derives a second Drawer boundary instead of using the real one

- Severity: High
- Evidence: `SettingDrawer.PanelWidth` is calculated from the Drawer host's
  `ActualWidth`, while `ReaderInputRouter` independently suppresses mouse-down
  and mouse-up when `point.X <= ViewportWidth * 0.25`. A `ScrollViewer` viewport
  excludes its visible rail and is not the same geometry owner as the Drawer
  host. The router already excludes events whose visual ancestry contains the
  real `SettingDrawer`.
- Impact: a narrow strip outside the real panel can be treated as covered, so
  the uncovered part of the left Overlay zone is not guaranteed usable. The
  two formulas can drift if shared Drawer sizing changes.
- Root fix: make the actual shared Drawer visual tree the only input boundary.
  Remove the screen-local fraction calculation and prove that real Drawer
  descendants are excluded while background events remain eligible even when
  Drawer state is open.

### L9 — global Reset can close an unpinned open Drawer

- Severity: High
- Evidence: `ReaderResetController` calls the ordinary `ResetZoom` command.
  `ReaderZoomController` reports and scrolls with `ReaderActivityOrigin.Zoom`,
  and `ReaderDrawerPolicy` closes an unpinned Drawer for that origin. The Reset
  regression starts with `IsDrawerPinned=true` and does not attach
  `ReaderDrawer`; the live harness runs Reset only after explicitly closing the
  Drawer.
- Impact: Reset violates its explicit non-effect: Drawer-open state can change
  when zoom was not already 100 percent.
- Root fix: model global Reset as its own typed activity origin/transaction.
  Reset still applies the same zoom anchor correction, but its viewport changes
  must not be interpreted as an ordinary zoom action by Drawer policy. Add an
  integration regression with an open, unpinned Drawer.

### L10 — failed chapter navigation can leave the selector ahead of committed state

- Severity: High
- Evidence: the feature-owned chapter card picker updates immediately and
  invokes an `Action<int>`. `ReaderChapterNavigation.Select` abandons the
  returned navigation task, and refreshes selection only on
  `ActiveChapterChanged`. A failed or cancelled request that never commits that
  event therefore leaves the contribution on the requested item rather than
  `IReaderChapterNavigation.ActiveChapterIndex`.
- Impact: the chapter selector can disagree with the coordinator, undermining
  the rule that it follows the currently committed chapter.
- Root fix: give the chapter feature one owned async command boundary. Await the
  coordinator transaction and reconcile the contribution from committed state
  in `finally`; report only an exception that escaped the coordinator through
  the notification owner. Prove failure reconciliation and subsequent success.

### L11 — chapter controls do not preserve their locked visual order

- Severity: Medium
- Evidence: the locked order is previous button, dynamic dropdown, next button.
  `ReaderDrawer.xaml` currently renders the dropdown first, followed by a second
  row containing Previous and Next. Existing minimum-width tests check overlap
  and clipping but never assert the chapter-control order.
- Impact: the implemented Drawer differs from the agreed control model and the
  test suite can remain green after reordering it incorrectly.
- Root fix: render one responsive `Previous | dropdown | Next` grid using the
  existing shared button and ComboBox styles. Columns must fit the 25-percent
  minimum-size Drawer without horizontal scrolling; assert grid columns and
  visual order rather than pixel snapshots alone.

### L12 — coordinator async primitives still have no explicit terminal owner

- Severity: Medium
- Evidence: `ReaderChapterCoordinator.Dispose` cancels work and detaches
  viewport events, then explicitly leaves its lifetime `CancellationTokenSource`
  and load `SemaphoreSlim` for GC because operations may still unwind. The
  locked lifecycle requires an explicit detach path for linked cancellation
  and no active render/load callback after close.
- Impact: cancellation behavior is safe in the current test, but terminal
  resource ownership is unverifiable and repeated Reader sessions rely on GC
  to finish cleanup.
- Root fix: add a small operation-drain owner around coordinator async entry
  points. Dispose rejects new work and cancels immediately; the final in-flight
  operation disposes the lifetime source and load gate exactly once. Test both
  idle disposal and delayed-load disposal without use-after-dispose.

### L13 — current Overlay/input evidence does not execute the central click route

- Severity: Evidence blocker
- Evidence: policy tests call `ResolveOverlayZone` and
  `IsInteractiveSource` directly. The live harness opens Drawer through
  `commands.ToggleDrawer()` and inspects `SettingDrawer` hit testing, but never
  sends a background mouse-down/mouse-up pair through `ReaderInputRouter`.
- Impact: duplicate/incorrect pointer gating or an invisible full-width shield
  can survive while both automated and live reports appear green.
- Root fix: add a deterministic router regression using the viewport pointer
  seam for click, drag, interactive descendant, and Drawer-open background
  cases. The final live harness must drive uncovered left/center/right events
  through the router and verify only actual Drawer bounds intercept them.

### L14 — the coalesced-step test exits before the terminal animation frame

- Severity: Evidence blocker
- Evidence: `RepeatedSteps_CoalesceIntoOneMovingNinetyPercentTarget` waits only
  until the offset is within `0.5` DIP of the target, then immediately asserts
  equality to one decimal place, which requires a materially tighter terminal
  tolerance. A loaded run exited at `1079.818...` for target `1080` before the
  180 ms animation delivered its final exact-offset tick.
- Impact: the full Reader suite can fail nondeterministically even though the
  navigator has not finished, obscuring real regressions and making the final
  gate load-dependent.
- Root fix: keep the production duration and exact terminal snap unchanged.
  Make the test wait for the same terminal precision it asserts, so it observes
  the completed animation rather than an acceptable in-flight frame.

### L15 — the Reset live setup does not wait for its new zoom transaction

- Severity: Evidence blocker
- Evidence: the harness queues one additional zoom step, then waits only for
  `ZoomScale > 1`. That condition is already true from earlier Pin/zoom checks,
  so it opens the unpinned Drawer before the queued render-priority zoom tick.
  The delayed Zoom activity then closes the Drawer by the locked policy and the
  harness times out while waiting for it to remain open.
- Impact: the intended Reset-with-open-Drawer regression is never reached, and
  a correct asynchronous zoom owner appears broken.
- Root fix: capture the pre-command scale and wait for the newly requested step
  to commit before opening the Drawer. Keep the production coalescing and the
  ordinary-Zoom Drawer-close policy unchanged.

### L16 — the minimum-width Drawer hides the selected chapter label

- Severity: High
- Evidence: final-size live capture at the locked 640 DIP Reader minimum shows
  the chapter ComboBox reduced to its arrow glyph; the selected `Chapter 2`
  label is not visible. The existing regression proves order and outer bounds
  only, so it passes even when the middle control has no usable text area.
- Impact: chapter navigation is visually ambiguous at the exact minimum layout
  that the responsive Drawer contract must support.
- Root fix: keep the proportional 25% Drawer and shared ComboBox template. Make
  the shared picker honor its native per-instance `Padding`, then give the
  chapter row compact local padding/margins and a tested minimum readable
  picker width. Do not introduce a Reader-local ComboBox template or horizontal
  Drawer scrolling.

## Repair resolution matrix

This matrix closes the findings without rewriting their original evidence.
Every production issue was repaired at its owning contract; evidence-only
findings changed the verifier rather than product behavior.

| Finding | Root resolution | Authoritative proof |
|---|---|---|
| C1 | `ReaderChapterCoordinator.Surfaces` is the one collection it mutates and publishes. | `StartLoad_PublishesIntoStableCollectionAndRemovesBlocker`; disposable CBZ live load shows three rolling surfaces. |
| C2 | `ReaderStatusHost` owns loading/error/blocker visibility and explicitly unblocks the page surface after success. | `StatusHost_ExplicitlyBlocksUnblocksAndSurfacesNonBlockingWarnings`; live `real CBZ load unblocked without error`. |
| C3 | XAML declares one named layer per role; `ReaderFeatureHost` mounts into those hosts and never creates a parallel tree. | `CatalogOnlyFeature_MountsOnceAndDisposesInReverseOrder`, duplicate-layer rejection, and live one-chrome/Drawer/Overlay/toast count. |
| C4 | `ReaderWindow` retains and reverse-disposes the host; coordinator operations use an explicit drain that disposes async primitives after the final operation. | feature-host reverse-disposal tests and both coordinator disposal tests. |
| H1 | One coordinator restores rolling previous/active/next surfaces, offset preservation, render-cache reuse, and history commit. | rolling-window, boundary-anchor, resize, real-CBZ, and history-once tests. |
| H2 | One `ReaderZoomController` owns Ctrl+wheel, Ctrl+0, Drawer steps, anchoring, clamping, and state projection. | `ZoomCommands_UseOneStateAndPreservePointerAnchor` plus normalization tests. |
| H3 | Chapter selection is an awaited, latest-request-wins coordinator transaction that lands on page one and emits once. | rapid-selection coordinator test, chapter contribution retry test, and live newest-jump checks. |
| H4 | Feature context exposes focused state/commands/viewport/chapter/input contracts; it exposes neither `ReaderWindow` nor named controls nor public state setters. | linked-source compile boundary, catalog-only feature test, and repository reach-back search. |
| H5 | `ReaderInputRouter` is the only preview-input subscriber and carries typed activity origin. | input-router drag/Drawer-open tests and repository preview-handler search. |
| H6 | Overlay visuals are hit-test-free; the router recognizes only a background click under the system drag threshold; navigator coalesces a 90% step. | policy/navigator tests plus OS-generated live left/center/right clicks. |
| H7 | Shared `SettingDrawer` owns width/animation/hit bounds; typed contributions supply all controls in locked order. | Drawer shared tests, contribution tests, and normal/minimum/maximized live captures. |
| H8 | Fullscreen uses correctly shaped Win32 monitor data and `rcMonitor`, remains non-topmost, rolls back on failure, and restores exact state/bounds. | fullscreen geometry/controller tests and current-monitor live fullscreen/restore checks. |
| H9 | Schema-v1 preferences validate fields independently, write atomically under a normalized path gate, debounce, flush on close, and warn without blocking. | seven preference-store tests and isolated live reload/reset proof. |
| H10 | Dim is viewport-only and hit-test-free; auto-scroll is elapsed-time based and uses typed stop origins. | Dim/auto controller and policy tests plus live Dim width and elapsed movement checks. |
| M1 | Shared Drawer owns final geometry, animation fallback, reload/size-change cleanup, and actual-bounds hit testing. | complete shared-component suite and final-geometry live waits. |
| M2 | Shared chrome begins visible, fades after 500 ms over 180 ms, releases hit testing, and uses one resize/drag/action-hold policy. | shared chrome fade/reload/action/resize tests and live physical-top reveal. |
| M3 | Toast duration/timer cleanup is single-owner; dead adapters/events and the Shell-private native helper were removed. | toast lifecycle test, zero stale-symbol result, and Debug/Release builds. |
| M4 | A linked-source Reader test project now covers engine, composition, controllers, policy, persistence, lifecycle, and real disposable CBZ integration. | 91/91 Reader tests, 226/226 UIA tests, and 54-check disposable live WPF pass. |
| L1 | Shared ComboBox selected presenter honors `DisplayMemberPath`. | `ComboBox_SelectedObjectHonorsDisplayMemberPath`; visible `Chapter 2`. |
| L2 | Harness waits for terminal Drawer transform before state/capture assertions. | six final captures contain no transient Drawer sliver. |
| L3 | Live harness covers maximized, fullscreen, normal 1180x760, and minimum 640x480. | exact 25% width, no horizontal Drawer scroll, controls-in-bounds checks at each available size. |
| L4 | Initial Reader readiness is committed only after neighbor preparation, preventing a reentrant neighbor from replacing the requested chapter. | reentrant initial-load test and live selector/coordinator agreement on Chapter 2. |
| L5 | Shared chrome calculates the six-DIP trigger in its own visible client/monitor coordinate contract, including maximized overscan and DPI conversion. | deterministic top-trigger tests and live physical top-edge reveal. |
| L6 | Navigator terminates cancellation/failure at its async boundary and emits one non-blocking warning. | `BoundaryFailure_IsReportedOnceAndDoesNotPoisonTheNextStep`. |
| L7 | `SettingWindowChrome.CanResize` governs both button and double-click; rejected native drag is non-fatal and always releases its hold. | resize-mode and rejected-drag shared tests. |
| L8 | Actual `SettingDrawer` visual ancestry is the only Drawer input boundary; the duplicate viewport-fraction shield was removed. | router regression and OS-generated uncovered Previous/Next clicks while Drawer is open. |
| L9 | Global Reset uses `ControlsReset` activity origin, so its zoom correction does not trigger ordinary Zoom Drawer policy. | `GlobalReset_PreservesAnOpenUnpinnedDrawer` and live Reset with open Drawer/fullscreen. |
| L10 | Chapter feature awaits navigation and always reconciles selected choice from committed coordinator state in `finally`. | `ChapterContribution_FailedRequestReconcilesToCommittedChapterAndCanRetry`. |
| L11 | Chapter controls render in one responsive Previous / picker / Next grid. | minimum-width order/geometry/readability regression and final live capture. |
| L12 | Coordinator rejects new work after disposal, cancels immediately, counts active operations, and disposes CTS/semaphore exactly once after drain. | idle and delayed-load disposal tests. |
| L13 | Router has deterministic pointer-seam tests; final harness uses real OS mouse down/up so WPF performs actual hit testing. | live Drawer-bound interception and uncovered left/center/right route checks. |
| L14 | Navigator keeps its 180 ms exact terminal snap; tests wait for the same precision they assert. | repeatable full Reader and full-solution passes. |
| L15 | Harness waits for the newly requested zoom scale to commit before opening the Reset test Drawer. | live Reset reaches and passes the intended open-unpinned-Drawer state. |
| L16 | Shared ComboBox honors per-instance `Padding`; the feature-owned chapter card uses a full-width picker above balanced Previous/Next shared buttons. | fail-first shared padding test plus minimum picker-width, readable-label, row-order, and button-balance regressions. |

## Final gate evidence

- `Module.Mangareader.Reader.Tests`: **91/91**.
- `Citadel.Uia`: **226/226**.
- `Citadel.slnx` Debug regression: **495/495**, exit code 0 (Core 108,
  UI 14, UIA 226, Archive 32, Library 24, Reader 91).
- `Citadel.Setting` and `Module.Mangareader` Debug/Release builds: zero
  warnings and zero errors.
- Debug and Release citizen deployment each contain only `layout.json`,
  `module.json`, `Module.Mangareader.deps.json`, `Module.Mangareader.dll`, and
  its PDB. The dependency manifest contains only `Module.Mangareader/1.3.0`.
- Disposable three-chapter WPF harness: **54/54** checks. Six captures were
  inspected at maximized, fullscreen, normal 1180x760, and minimum 640x480.
  These captures predate the later feature-owned card arrangement; fresh user
  visual judgment for that arrangement remains pending.
- Available live hardware was one 2560x1440, 96-DPI monitor. Negative monitor
  coordinates and DPI transforms are covered deterministically; they are not
  misreported as live multi-monitor/high-DPI evidence.
- `git diff --check` exits successfully; no harness, screenshot, disposable
  CBZ, preference, or cache fixture remains, and no build/runtime artifact is
  tracked in the deliverable worktree.
