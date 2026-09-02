# PLAN: MangaReader immersive Reader controls

Status: **IMPLEMENTED — automated, isolation, hygiene, and available-hardware
live gates passed on 2026-09-02**
Baseline: `main` at `e792b14` / release `v1.3.0`, clean worktree on
2026-09-01.

This plan replaces the four vague Reader items under
`module/mangareader/TASKLIST.md`. It is the complete implementation contract for
the standalone Reader window. Downloader work remains a separate, unapproved
scope.

## 1. Goal

Turn the existing continuous vertical Reader into an immersive local reader
with one stable parent/children architecture. The delivered Reader must have:

1. a three-zone click overlay for viewport navigation;
2. a left sliding Drawer containing chapter and reading controls;
3. true fullscreen and a themed auto-fading top chrome;
4. page-only dimming;
5. configurable auto-scroll;
6. session pin/zoom/fullscreen state and safe persisted preferences; and
7. one extension contract so a later Reader feature is added through its own
   feature file plus one explicit catalog entry, without reopening
   `ReaderWindow` composition.

The current natural-width pages, render cache, continuous chapter surface,
three-chapter rolling set, history event, and shared scrollbar remain the
reader engine. This plan extends that engine; it does not replace it.

## 2. Verified current baseline

The live implementation currently contains only:

```text
module/mangareader/Reader/
├── ReaderWindow.xaml
├── ReaderWindow.xaml.cs       463 lines
└── ReaderZoomController.cs
```

`ReaderWindow.xaml.cs` currently owns window composition, chapter loading, the
rolling previous/active/next surfaces, active-chapter detection, input routing,
zoom lifecycle, loading/error state, and cleanup. The window starts maximized
with a native title bar; no Drawer, click Overlay, fullscreen mode, dim layer,
auto-scroll, pin, custom chrome, or Reader preference store exists.

Reusable `SettingButton`, `SettingSlider`, ComboBox style,
`SettingScrollViewerStyle`, theme tokens, and auto-fading scrollbars already
exist. A reusable Drawer and custom window chrome do not yet exist.

## 3. Locked product behavior

### 3.1 Overlay

- `ReaderOverlay` is a child surface inside `ReaderWindow`, not another OS
  window.
- It defines three equal-width, full-height logical zones:
  `Previous | Menu | Next`.
- The zone visuals stay hidden. Automation names and hit geometry remain
  available.
- `Previous` smoothly scrolls upward by `90%` of the current viewport height.
- `Next` smoothly scrolls downward by `90%` of the current viewport height.
- Overlay Previous/Next never mean previous/next chapter. They are click
  substitutes for manual scrolling.
- `Menu` toggles the Drawer.
- One scroll step lasts `180 ms` with ease-out. When Windows client-area
  animation is disabled, the step is immediate.
- Repeated clicks coalesce into one moving target rather than creating
  concurrent animations.
- At the absolute beginning/end of the title, the unavailable direction is
  disabled and never wraps.
- When the rolling surface does not yet contain the adjacent chapter, the
  chapter coordinator prepares that surface once, preserves the viewport
  anchor, then completes the requested step.
- During initial load, chapter transition, or error state, all three zones are
  disabled.
- The Overlay itself does not steal wheel, scrollbar drag, touch/pan, or
  `Ctrl+wheel`. Click activation is recognized by the central input router only
  after a mouse-up with movement below the system drag threshold.

### 3.2 Drawer

- `ReaderDrawer` is a separate child surface inside the Reader.
- It slides from the left over the pages; it never reflows, shrinks, or
  re-zooms page content.
- Width is exactly `25%` of the current Reader client width. There is no fixed
  DIP width. Its contents adapt, trim, or stack inside that proportional width
  and never create horizontal scrolling.
- Open/close animation is `200 ms`, ease-out, and respects the Windows
  animation preference.
- The backdrop is visual only (`IsHitTestVisible=false`). It cannot consume
  Overlay clicks.
- The Drawer itself receives input only inside its actual left-side bounds.
  The uncovered part of the left zone and all of the right Next zone remain
  usable; the center zone remains available to close the Drawer.
- Pin applies only to the Drawer. Pin keeps it open through ordinary Reader
  actions, but an explicit Menu toggle or `Esc` still closes it.
- Drawer open and Pin are session-only and start `false` for every Reader
  window.

Drawer content order is fixed:

1. previous chapter button, dynamic chapter dropdown, next chapter button;
2. fullscreen;
3. auto-scroll toggle and speed slider;
4. pin;
5. zoom out, current percentage, zoom in;
6. Dim Pages slider and local Dim reset; and
7. global Reset.

All buttons use `SettingButton`, sliders use `SettingSlider`, the dropdown uses
the shared ComboBox style, and Drawer scrolling uses
`SettingScrollViewerStyle`. No Reader-local copy of those templates is allowed.

### 3.3 Chapter controls

- Drawer Previous/Next are chapter-level actions, unlike Overlay
  Previous/Next.
- The chapter picker occupies its own first row; equal-width Previous/Next
  buttons occupy the second row. Drawer Previous/Next and dropdown selection
  jump directly to the first page
  of the target chapter.
- The dropdown lists every chapter in the title's existing natural order and
  follows the currently active chapter.
- The first/last chapter disables the corresponding button; chapter
  navigation never wraps.
- A jump reuses the existing cache and rolling-surface loader. It does not
  create a second chapter-loading path.
- Rapid selection is latest-request-wins: stale loads may finish cleanup but
  may not commit UI state, history, title, or active chapter.
- The active chapter event fires exactly once after a successful committed
  jump.

### 3.4 Fullscreen and top chrome

- Reader windowed/maximized mode uses themed custom chrome instead of the
  current white native caption.
- The chrome overlays the pages and reserves no page-layout height.
- It shows the current manga/chapter title and standard minimize,
  maximize/restore, and close actions.
- It appears on first open, then hides after `500 ms` idle.
- Fade duration is `180 ms`.
- Only entering a full-width `6 DIP` trigger strip at the top, entering the
  visible chrome, keyboard focus inside the chrome, or an explicit chrome
  action reveals/holds it. Ordinary page scrolling does not reveal it.
- A drag or pressed system action holds the chrome visible until completion.
- The chrome uses Citadel theme resources (`BgRail`, `Fg`, `Border`, `Hover`,
  `Selected`) and never renders a white fallback bar.
- `F11` and the Drawer button toggle true fullscreen.
- True fullscreen covers the current monitor including the taskbar, but does
  not set `Topmost`; `Alt+Tab` continues to work.
- Entering fullscreen stores exact prior bounds, state, resize mode, and monitor
  context. Exiting restores them without changing chapter, viewport position,
  zoom, dim, Drawer state, or auto-scroll state.
- Entering fullscreen shows a short Reader toast: `To exit full screen, press
  Esc`. The toast auto-hides after two seconds.
- Fullscreen is session-only and starts `false`.

### 3.5 Escape priority

`ReaderInputRouter` handles `Esc` at the window preview boundary in this exact
order:

1. if fullscreen, leave fullscreen and stop;
2. otherwise, if Drawer is open, close it even when pinned and stop;
3. otherwise, close the Reader window.

No child control installs a competing `Esc` handler.

### 3.6 Zoom

- Keep the existing `50%–300%`, `10%` step, pointer-anchored zoom contract.
- `Ctrl+wheel` and `Ctrl+0` remain supported.
- Drawer Zoom Out/Zoom In and the percentage label use the same
  `ReaderZoomController`; there is no second zoom state.
- Zoom stays session-only and defaults to `100%`.
- Zoom never scales Drawer, Overlay, top chrome, toast, loading, or error UI.

### 3.7 Dim Pages

- Range: `0%–80%`.
- Default: `0%`.
- Step: `5%`.
- `Alt+Up` decreases dim by one step, `Alt+Down` increases it, and `Alt+0`
  resets it to `0%`.
- The Drawer has a local Dim reset button that changes only Dim Pages.
- One black, hit-test-free layer covers only the Reader content viewport. Its
  dimensions follow `ReaderScroller.ViewportWidth/ViewportHeight`, so it does
  not dim the shared scrollbar rail.
- Drawer, Overlay, top chrome, toast, loading, and error UI are never dimmed.
- Images are not re-decoded and no per-image effect is created.
- Dim value is persisted.

### 3.8 Auto-scroll

- Auto-scroll moves one viewport height per configured number of seconds.
- Default is `5 seconds / viewport`.
- The Auto-scroll card exposes separate, equal-width `Start` and `Stop`
  buttons. They issue distinct commands; no toggle command is used.
- Configurable range is `1–30 seconds / viewport`, one-second steps. Older
  persisted values above `30` are normalized to `30` on load.
- The Drawer uses one slider, not a numeric input. Its physical direction is
  `Slow 30s` on the left to `Fast 1s` on the right, while the stored value
  remains the unambiguous seconds-per-viewport number.
- The current value is displayed as, for example, `5 s / screen`.
- Movement is elapsed-time based, independent of refresh rate and DPI.
- Changing the slider while running changes speed immediately.
- Interacting with Drawer buttons, picker, or speed slider is control input,
  not manual Reader navigation, and therefore does not stop auto-scroll.
- Persist only the configured speed. Running state is session-only and always
  starts stopped.
- The following manual intent stops auto-scroll and it never resumes by
  itself: wheel/touch/pan, scrollbar drag, keyboard page navigation, Overlay
  Previous/Next, chapter navigation, or zoom.
- Opening the Drawer, losing window activation, entering a loading/error state,
  or reaching the absolute end also stops auto-scroll.
- Auto-scroll's own `ScrollChanged` events are tagged as programmatic and do not
  stop itself.

### 3.9 Global Reset

Global Reset performs one atomic Reader-state command:

- zoom → `100%`;
- dim → `0%`;
- auto-scroll speed → `5 seconds / viewport`;
- auto-scroll running → stopped; and
- Pin → released.

It deliberately does **not** change the active chapter, viewport/reading
position, Drawer open state, or fullscreen state. Persisted Dim and speed are
saved at their reset defaults.

## 4. Parent/children architecture

```text
ReaderWindow (parent/composition root)
├── ReaderChapterCoordinator       existing loader + rolling surface owner
├── ReaderSessionState             canonical observable state
├── ReaderInputRouter              one input priority boundary
├── ReaderActivityHub              typed manual/programmatic activity
└── ReaderFeatureHost
    └── ReaderFeatureCatalog       explicit registration order
        ├── viewport navigation / Overlay
        ├── chapter navigation
        ├── Drawer + Pin
        ├── fullscreen + chrome + toast
        ├── zoom
        ├── Dim Pages
        ├── auto-scroll
        └── global Reset
```

### 4.1 Stable contract

`ReaderWindow` owns and supplies one `ReaderFeatureContext` containing only:

- read-only Reader state snapshots and change notifications;
- typed Reader commands;
- a viewport adapter for offset, extent, and viewport metrics;
- chapter navigation operations owned by `ReaderChapterCoordinator`;
- the activity hub; and
- lifecycle cancellation/dispatch access.

Children never call one another and never reach back into named fields on
`ReaderWindow`. They issue typed commands through the context. The parent owns
the canonical state and accepts or rejects each mutation.

`IReaderFeature` has an explicit `Attach(context)` / `Dispose()` lifecycle and
may publish zero or more typed Drawer card contributions. Each feature owns its
card composition and reuses the existing shared card, button, slider, and
picker controls. `ReaderDrawer` only orders and hosts those opaque cards; it has
no feature-specific template or control layout. A later feature is
therefore introduced by:

1. adding its own feature file (or one XAML/code-behind visual pair when it
   truly owns presentation); and
2. adding one explicit entry to `ReaderFeatureCatalog`.

No reflection, assembly scanning, hidden auto-discovery, service locator, or
runtime dependency injection framework is added. `ReaderWindow` is refactored
once to install the host and named layers; later features do not edit its
composition.

### 4.2 State ownership

| State | Sole mutation owner | Persistence |
|---|---|---|
| active chapter, loading, rolling surfaces | `ReaderChapterCoordinator` | existing History event only |
| viewport offset/extents | Reader viewport adapter | session only |
| zoom | `ReaderZoomController` | session only |
| Drawer open | Drawer feature | session only |
| Pin | `ReaderPinController` | session only |
| fullscreen/restore bounds | `ReaderFullscreenController` | session only |
| dim percent | `ReaderDimController` | persisted |
| auto-scroll seconds | `ReaderAutoScrollController` | persisted |
| auto-scroll running | `ReaderAutoScrollController` | session only |

`ReaderSessionState` is the observable projection of these owners, not a bag of
public setters.

### 4.3 Layer and hit-test order

The one-time Reader XAML composition installs these named layers:

| Z | Layer | Hit-test rule |
|---:|---|---|
| 0 | pages and shared Reader scrollbar | ordinary Reader input |
| 10 | Dim Pages | always false |
| 20 | Drawer backdrop | always false |
| 30 | Overlay zone geometry | visual false; click resolved by input router |
| 40 | top chrome | true only while visible/triggered |
| 50 | Drawer | true only inside Drawer bounds |
| 60 | toast | false |
| 100 | loading/error | blocks Reader feature input |

This ordering is why opening the Drawer cannot disable Next: only the actual
left Drawer bounds intercept input, while its backdrop remains visual-only.

## 5. Shared-component boundary

Add only the missing general UI primitives:

```text
setting/Components/
├── Drawer.xaml(.cs)                    SettingDrawer presentation + slide lifecycle
├── WindowChrome.xaml(.cs)              SettingWindowChrome presentation + auto-hide lifecycle
└── NativeWindowChromeBehavior.cs       shared DWM dark caption fallback
```

- `SettingDrawer` owns proportional width, clipping, slide animation,
  open/close state transition, and cleanup. It knows nothing about manga,
  chapters, Pin, or Reader commands.
- `SettingWindowChrome` owns themed caption presentation, drag/double-click,
  system window buttons, top trigger, fade timing, system-animation fallback,
  and cleanup. Reader title/fullscreen state is supplied by the Reader
  adapter.
- Move the existing Shell-only DWM coloring helper into
  `NativeWindowChromeBehavior` and migrate MainWindow/SettingsWindow to it.
  Delete the redundant `core/Citadel.Shell/NativeWindowChrome.cs` after all
  consumers use the shared behavior.
- Continue using existing shared button, slider, ComboBox, scrollbar, and theme
  resources. Do not create Reader-local copies or add Gallery presets for
  Drawer/window chrome; their behavior is fixed, not user-style data.
- Reader-specific Overlay geometry, chapter controls, dim semantics, and
  auto-scroll remain under `module/mangareader/Reader/`.

No third-party dependency is required. WPF, `System.Windows.Shell.WindowChrome`,
DWM APIs, and the existing Citadel assemblies are sufficient.

## 6. Target file tree

The Reader folder stays flat. A folder is introduced only after one feature
actually owns multiple cohesive implementation files; it is never created just
to hold one file.

```text
module/mangareader/Reader/
├── ReaderWindow.xaml(.cs)              parent composition only after one refactor
├── ReaderFeatureContract.cs            context, feature and Drawer-contribution contracts
├── ReaderFeatureHost.cs                attach/dispose ownership
├── ReaderFeatureCatalog.cs             one explicit registration list
├── ReaderSessionState.cs               canonical observable snapshot
├── ReaderInputRouter.cs                Esc/F11/hotkey/pointer priority
├── ReaderActivityHub.cs                typed manual/programmatic activity
├── ReaderChapterCoordinator.cs         extracted existing rolling chapter engine
├── ReaderViewportNavigator.cs          90% click-scroll and boundary policy
├── ReaderOverlay.xaml(.cs)             three-zone child surface
├── ReaderDrawer.xaml(.cs)              contribution renderer over SettingDrawer
├── ReaderChapterNavigation.cs          picker + balanced Previous/Next card
├── ReaderFullscreenController.cs       monitor fullscreen + exact restore
├── ReaderChromeController.cs           Reader adapter for SettingWindowChrome
├── ReaderToast.xaml(.cs)               transient fullscreen hint
├── ReaderDimController.cs              page-only dim + hotkeys
├── ReaderAutoScrollController.cs       elapsed-time scroll + stop rules
├── ReaderPinController.cs              Drawer-only Pin
├── ReaderZoomController.cs             existing zoom, adapted to the feature contract
├── ReaderResetController.cs            one global reset command
└── ReaderPreferencesStore.cs           validated atomic preference persistence

tests/
├── Module.Mangareader.Reader.Tests/    linked pure Reader policies/controllers
└── Citadel.Uia/                         shared-component + live automation contracts
```

The test project may link pure Reader sources exactly as the existing Library
and Archive test projects do. It must not add the citizen assembly as a normal
solution dependency.

## 7. Persistence contract

Store Reader preferences at:

```text
%LocalAppData%\Citadel\MangaReader\reader-preferences.json
```

Schema v1 contains only:

```json
{
  "version": 1,
  "dimPercent": 0,
  "autoScrollSecondsPerViewport": 5
}
```

Rules:

- load before feature attachment so the first visible frame uses final values;
- validate fields independently;
- clamp Dim to `0–80` and snap to `5`;
- clamp speed to `1–30` and snap to whole seconds;
- missing, empty, oversized, malformed, unknown-version, or unreadable data
  falls back safely to defaults without propagating an exception;
- an invalid field does not discard a valid sibling field;
- save through a unique same-folder temporary file followed by atomic move;
- serialize concurrent instances by normalized storage path;
- debounce slider saves so thumb movement does not write every frame;
- a save failure leaves live Reader state usable and reports a non-blocking
  warning; and
- never store preferences in the install/module folder, so app updates cannot
  remove them.

## 8. Input and activity contract

`ReaderInputRouter` is the only window-level preview handler.

| Input | Result |
|---|---|
| `Esc` | fullscreen → Drawer → close Reader priority |
| `F11` | toggle fullscreen |
| `Ctrl+wheel` | existing pointer-anchored zoom; stop auto-scroll |
| `Ctrl+0` | zoom 100%; stop auto-scroll |
| `Alt+Up` | dim -5% |
| `Alt+Down` | dim +5% |
| `Alt+0` | dim 0% |
| ordinary wheel/touch/drag/scroll keys | normal scrolling; stop auto-scroll |
| background click in left/center/right zone | -90% / Drawer toggle / +90% viewport |

The activity hub identifies origin (`Manual`, `OverlayStep`, `AutoScroll`,
`ChapterJump`, `Zoom`, `LayoutRestore`). Programmatic scroll changes cannot be
mistaken for manual intent. Chrome receives only top-edge/chrome activity, so
ordinary scroll never reveals it.

## 9. Lifecycle, concurrency, and performance

- `ReaderFeatureHost` attaches after the Window and named surfaces are ready.
- Close cancels one root lifetime token, then disposes features in reverse
  catalog order before clearing chapter surfaces.
- Every timer, rendering callback, storyboard, event subscription, pointer
  capture, and linked cancellation source has an explicit detach path.
- `ReaderChapterCoordinator` keeps a monotonic navigation generation. Only the
  current generation may commit a loaded target.
- Initial load, rolling preload, Overlay boundary preparation, and Drawer
  chapter jumps share the existing chapter-load gate and cache.
- A chapter jump is an atomic visible transition: stop auto-scroll, enter
  loading state, prepare target, commit target at page one, publish the active
  chapter once, then prepare neighbors.
- Overlay step animations and auto-scroll use elapsed time; neither queues one
  Dispatcher operation per pixel.
- The existing page bitmaps are reused. Dim, Drawer, chrome, Overlay, and zoom
  do not trigger image decoding.
- When the window deactivates, auto-scroll stops; chrome/Drawer state remains
  otherwise unchanged.

## 10. Implementation sequence

### Phase 0 — baseline and characterization

1. Confirm the implementation starts from a clean `v1.3.0`-equivalent
   baseline and preserve unrelated future worktree changes.
2. Add behavior tests around current zoom, active-chapter transition, rolling
   surface ordering, offset preservation, close cancellation, and history
   notification before extraction.
3. Record a disposable temporary-CBZ smoke fixture. Never test mutations or
   navigation against `D:\[ MANGA ]`.

### Phase 1 — shared UI primitives

1. Implement and test `SettingDrawer`.
2. Implement and test `SettingWindowChrome` and shared native DWM fallback.
3. Migrate Shell MainWindow and SettingsWindow from the redundant
   Shell-private native helper, then delete that helper.
4. Keep shared components screen-blind and document them as planned until live
   gates pass.

### Phase 2 — one-time Reader parent refactor

1. Extract the current loading/rolling-surface behavior into
   `ReaderChapterCoordinator` without changing observed behavior.
2. Introduce state, feature context, feature host/catalog, input router, and
   activity hub.
3. Install all named XAML layer hosts in their final Z order.
4. Mount empty `ReaderOverlay`, `ReaderDrawer`, shared chrome, and toast child
   surfaces once.
5. Reduce `ReaderWindow.xaml.cs` to construction, parent command acceptance,
   Loaded/Closed lifecycle, and composition.
6. Re-run characterization tests before adding new behavior. From this point,
   later feature phases may edit the catalog and their feature files, but not
   reopen parent composition.

### Phase 3 — Overlay viewport navigation

1. Implement three-zone geometry and click-vs-drag recognition.
2. Implement the coalesced `90%` viewport navigator.
3. Integrate boundary preparation with the chapter coordinator.
4. Prove wheel, scrollbar drag, touch/pan, and `Ctrl+wheel` remain owned by the
   Reader surface.

### Phase 4 — Drawer and chapter/Pin/zoom controls

1. Render typed contributions through shared controls.
2. Implement proportional Drawer animation and visual-only backdrop.
3. Implement chapter buttons/dropdown with latest-request-wins commit.
4. Adapt existing zoom to Drawer controls without duplicating state.
5. Implement Drawer-only Pin and the exact open/close rules.

### Phase 5 — chrome, fullscreen, and toast

1. Replace the visible native caption with the shared themed custom chrome.
2. Implement top-trigger reveal and `500/180 ms` idle/fade behavior.
3. Implement current-monitor true fullscreen and exact restore.
4. Wire F11, Drawer action, toast, and the locked Esc priority.

### Phase 6 — Dim, auto-scroll, preferences, and Reset

1. Implement validated atomic preference load/save.
2. Implement page-only dim layer, slider, reset, and Alt hotkeys.
3. Implement elapsed-time auto-scroll, speed mapping, persistence, and all stop
   rules.
4. Implement one global Reset command and verify its explicit non-effects.

### Phase 7 — integration, cleanup, and documentation

1. Delete old Reader event handlers/helpers made redundant by the host,
   coordinator, or input router; do not retain compatibility wrappers.
2. Search for duplicate button/slider/combo/chrome/Drawer templates and remove
   every screen-local copy.
3. Update `SHARED-UI-BEHAVIOR.md` only after shared and live visual gates pass.
4. Update this plan with implementation evidence and mark tasklist items
   complete only after their own gates pass.
5. Do not commit, bump a version, build an installer, or publish a release
   unless separately requested.

## 11. Validation matrix

### 11.1 Automated gates

| Gate | Pass condition |
|---|---|
| shared component tests | Drawer width/animation/hit testing and chrome fade/cleanup/system actions pass |
| Reader policy tests | overlay step, boundary, Esc priority, state ownership, reset, clamp/fallback and activity-origin rules pass |
| chapter transition tests | latest request wins; stale load cannot commit; history fires once; first-page jump is exact |
| persistence tests | defaults, field-level damage, range snapping, atomic replacement, concurrent instances and cleanup pass |
| lifecycle tests | no active timer/render callback/event handler after close or unload |
| shared build | `Citadel.Setting` builds with zero warnings/errors |
| citizen builds | MangaReader Debug and Release build/deploy with zero warnings/errors |
| isolation | deployed MangaReader contains no shared `Citadel.*.dll` |
| full regression | current `Citadel.slnx` suite passes after integration |
| hygiene | `git diff --check`; no temp CBZ, preference, cache, build, or runtime data tracked |

### 11.2 Live WPF gates

Use a disposable title with at least three chapters and enough pages to overflow
several viewports.

| State | Required evidence |
|---|---|
| normal, maximized, minimum-size Reader | pages retain natural proportions; no new outer scrollbar or clipped controls |
| custom chrome | no white native header; correct theme; initial fade, top-edge reveal, hover/drag hold |
| scrolling | ordinary scroll does not reveal chrome; shared scrollbar still auto-fades independently |
| Overlay | 90% up/down, Menu toggle, click coalescing, drag/wheel pass-through, absolute-boundary disable |
| Drawer | left slide, exactly 25% current width, no page reflow, visual-only backdrop, Next usable while open |
| chapter controls | dropdown and buttons jump to page one; first/last disabled; rapid change commits only the last target |
| fullscreen | covers taskbar on current monitor, stays non-topmost, F11 toggles, exact bounds/state restore |
| Esc chain | first Esc exits fullscreen, second closes pinned Drawer, third closes Reader |
| Dim | page viewport alone dims; 0–80/5 steps; Alt shortcuts and local reset work |
| auto-scroll | smooth render-frame movement, 5-second default, 1–30 slider mapping, live speed change, manual/Drawer/deactivation/load/end stop rules |
| Reset | resets only zoom/dim/speed/running/Pin and preserves chapter, position, Drawer open, fullscreen |
| restart/update-safe data | Dim and speed restore; every session-only state returns to default |

Available-monitor visual evidence must be reported honestly. Multi-monitor and
high-DPI restore policies require deterministic tests when that hardware is not
available; they may not be reported as a live visual PASS.

## 12. In-scope failure modes

| Risk | Required disposition |
|---|---|
| transparent Overlay blocks drag/wheel | Overlay visual is hit-test-free; central click recognition ignores interactive descendants and drag gestures |
| Drawer backdrop disables Next | backdrop is visual-only; only actual Drawer bounds intercept |
| auto-scroll stops from its own event | every viewport mutation carries an activity origin |
| smooth scroll races chapter rotation | one coordinator/generation owns surface preparation and commit |
| rapid dropdown changes show stale chapter | latest-request-wins token checked before every visible commit |
| fullscreen restores to wrong monitor/state | capture exact pre-fullscreen bounds/state and test pixel↔DIP monitor conversion |
| custom chrome leaks timers/handlers | shared component unload/dispose tests and reverse-order feature cleanup |
| Dim also darkens UI/scrollbar | Dim bounds bind only to content viewport and stay below all controls |
| corrupt preferences crash Reader | bounded fail-soft parse plus field-level defaults |
| reset unexpectedly loses reading place | one tested reset state transition with explicit preserved fields |
| parent grows again for each feature | all stable layers/context are installed once; catalog is the only registration point |

## 13. Done definition

Done means every behavior in section 3 works through the architecture in
section 4; shared primitives are reused instead of copied; the original reader
engine has no semantic regression; adding a normal future Drawer/Reader feature
requires only its feature unit and one catalog entry; persisted values survive
updates and damaged data falls back safely; all automated gates pass; and live
WPF evidence covers the available hardware before any visual PASS is claimed.

## 14. Explicit non-goals

- reading-direction modes;
- fit-width, fit-height, or other fit modes;
- downloader, Comix, APK, Camoufox, stealthB, or pyhost changes;
- streaming/remote reading;
- changing CBZ decode, archive, Cover Builder, Library, or History behavior;
- persisting zoom, fullscreen, Drawer open, Pin, auto-scroll running, chapter,
  or viewport position;
- replacing the existing shared scrollbar; and
- adding third-party UI or archive dependencies.

There are no remaining open product decisions for this Reader-control scope.

## 15. Implementation evidence — 2026-09-02

The locked behavior remains the authority. Implementation was repaired against
it through `.docs/PLAN-mangareader-reader-controls-repair.md`; original and
live-discovered findings plus their root resolution are retained in
`.docs/AUDIT-mangareader-reader-controls-2026-09-02.md`.

### Delivered ownership

- `ReaderWindow` is one composition root with fixed named layer hosts.
- `ReaderFeatureHost` mounts the explicit catalog and reverse-disposes it.
- `ReaderChapterCoordinator` is the only rolling/load/jump owner and drains
  async operations explicitly on close.
- `ReaderInputRouter` is the only window preview-input owner.
- State, commands, viewport, chapter navigation, input, activity, and
  notification contracts are focused; children do not reach into named parent
  controls or public mutable state.
- Shared `SettingDrawer`, `SettingWindowChrome`, `SettingSlider`, shared
  ComboBox style, and shared ScrollViewer style own universal behavior; Reader
  files own only manga semantics and feature contribution layout.

### Final proof

| Gate | Result |
|---|---|
| dedicated Reader suite | 91/91 |
| complete UIA/shared suite | 226/226 |
| complete Debug solution regression | 495/495, exit code 0 |
| Setting Debug/Release | 0 warnings, 0 errors |
| MangaReader Debug/Release | 0 warnings, 0 errors |
| citizen isolation | Debug/Release contain only MangaReader payload; no `Citadel.*.dll` or third-party dependency |
| disposable live WPF | 54/54 checks; six captures inspected |
| sizes | maximized, fullscreen, normal 1180x760, minimum 640x480 |
| hygiene | diff check, stale-symbol, tracked-temp, dependency, and dirty-scope review pass |

The live machine provided one 2560x1440 96-DPI monitor. Current-monitor
fullscreen, taskbar coverage, non-topmost behavior, exact restore, physical-top
chrome reveal, proportional Drawer, real routed Overlay clicks, Reset, and Esc
priority passed live. Negative monitor coordinates and DPI conversions pass
deterministic tests; multi-monitor/high-DPI hardware was not available and is
not claimed as a live visual pass.

No Reader preference, disposable CBZ, harness, screenshot, or cache fixture is
part of the deliverable worktree. No commit, version bump, package, or release
is implied by this implementation status.
