# PLAN: MangaReader Reader-controls repair

Status: **COMPLETE — root repair and every required gate passed on 2026-09-02**  
Date: 2026-09-02  
Behavior contract: `.docs/PLAN-mangareader-reader-controls.md`  
Current base: `main` at `ae450bc` plus the uncommitted Reader/shared-component WIP

## 1. Purpose

Repair the current immersive Reader implementation without changing the locked
product behavior. The repair must first recover the existing continuous-reader
engine, then establish one stable parent/children composition, and only then
wire the immersive features through that contract.

This is not a second product plan. If this document and the locked behavior
contract disagree, the locked behavior contract wins.

## 2. Current verdict

The baseline WIP audited at the start of this repair was **FAIL**: it built and
the old solution suite passed, but the visible Reader engine, composition,
lifecycle, and behavior contracts were disconnected. That verdict remains the
historical starting point recorded in
`.docs/AUDIT-mangareader-reader-controls-2026-09-02.md`.

The repaired worktree is **PASS on available hardware**. Root composition,
ownership, continuous Reader engine, shared Drawer/chrome, feature controls,
persistence, dedicated Reader tests, and disposable live evidence have been
rebuilt and reconciled against the locked behavior contract. Every finding
through L16 is closed in the audit resolution matrix. Automated, Debug/Release
build, deployment isolation, hygiene, and post-fix live gates pass. Live
multi-monitor/high-DPI hardware was unavailable; those policies are covered by
deterministic tests and are not overstated as a live pass.

Confirmed repair groups:

1. **Reader engine is disconnected from the visible UI.** The coordinator
   publishes a different surface collection from the collection it mutates;
   successful loading does not dismiss the blocking loading layer; manual
   scroll no longer drives chapter rotation; and the existing zoom path is not
   connected end to end.
2. **Composition is duplicated.** XAML creates the visual layers once, then
   `ReaderFeatureHost` creates another set at runtime. Controllers attach to
   different instances from the visible controls, nonvisual controllers are
   mounted as visual content, the host is not retained, and feature cleanup is
   not owned by the window.
3. **The parent/children contract is porous.** Feature context exposes the
   concrete `ReaderWindow` and publicly mutable session state. Drawer content is
   hardcoded instead of being supplied by typed feature contributions.
4. **Input and navigation have competing or missing routes.** The central input
   router raises commands that are not consistently subscribed; an alternate
   window key handler is not the authoritative path; overlay hit testing and
   pinned-Drawer behavior do not match the locked rules; chapter jumps are not
   latest-request-wins or guaranteed to land on page one.
5. **Shared Drawer/chrome and Reader controllers are incomplete.** Drawer width
   is derived from itself and collapses to zero; most Drawer actions are not
   wired; fullscreen uses the work area and an invalid native monitor shape;
   chrome visibility/hit testing/lifecycle are incomplete.
6. **Dim, auto-scroll, and preferences do not have a closed state path.** The
   dim layer covers the wrong area, activity origins are not preserved through
   scrolling, live state is not synchronized back to the preference store, and
   the persistence gate/schema/atomic cleanup do not meet the locked contract.

### 2.1 Implementation reconciliation

This table is an execution checkpoint, not a replacement for the locked product
contract or final evidence matrix.

| Audit group | Current disposition | Required before closure |
|---|---|---|
| C1–C4 | **Closed** | One composition tree, explicit status host, retained/reverse-disposed feature host, and drained coordinator lifecycle pass dedicated tests. |
| H1–H10 | **Closed** | Every locked engine, contract, input, navigation, Drawer, fullscreen, persistence, Dim, and auto-scroll behavior has one authoritative path and proof. |
| M1–M4 | **Closed** | Shared lifecycle and false-confidence gaps are covered by 226 UIA tests, 86 Reader tests, stale-symbol search, and live evidence. |
| L1–L5 | **Closed** | Shared presenter, final animation timing, responsive sizes, requested initial chapter, and physical-top trigger all pass regression/live gates. |
| L6–L12 | **Closed** | Async error, resize, actual Drawer boundary, Reset origin, selection reconciliation, chapter layout, and async drain were repaired at their owners. |
| L13–L16 | **Closed** | Real OS click routing, terminal timing, Reset harness synchronization, and readable minimum-width chapter picker now have fail-first and live proof. |
| Final cleanup/evidence | **Closed** | Resolution docs updated; disposable harness/evidence removed; full test/build/isolation/hygiene gates pass. |

### 2.2 Root-repair batch record

The earlier R0–R7 sequence below remains the architectural record. Batches A–E
are complete; their final proof is summarized below and mapped finding-by-
finding in the audit.

#### Batch A — close the two open behavior boundaries

1. Make the viewport-step command own failures from adjacent-chapter boundary
   preparation. Cancellation remains silent; an active non-cancellation load
   failure becomes one non-blocking warning through `IReaderNotifications` and
   cannot escape the Overlay event path as an unobserved task.
2. Add an awaited regression where boundary preparation fails. It must prove
   no viewport mutation, no rethrow, exactly one warning, and normal subsequent
   navigation remains possible.
3. Define one screen-blind `SettingWindowChrome` resize policy and use it for
   both Max/Restore button availability and title-surface double-click.
4. When resize is unavailable, consume double-click without changing native or
   Reader fullscreen state. Treat a native `DragMove` rejection as non-fatal
   while always releasing the chrome hold state.
5. Add shared tests for every `ResizeMode`, the blocked gesture decision, and
   cleanup after a rejected native drag.

#### Batch B — requirement-by-requirement closure audit

1. Walk every behavior in sections 3–9 of the canonical plan against its one
   authoritative runtime path and a test or live-evidence owner.
2. Confirm one instance of each page, Dim, backdrop, Overlay, chrome, Drawer,
   toast, and loading/error layer; one mutable owner per state domain; and one
   preview-input route.
3. Check negative requirements explicitly: Overlay remains hit-test-free,
   Drawer blocks only its own bounds, ordinary scroll never reveals chrome,
   Reset preserves position/Drawer/fullscreen, and no forbidden state is
   persisted.
4. Record any newly confirmed issue in the audit before editing source. Group
   related symptoms under the owning contract and repair that owner once.

#### Batch C — redundancy and documentation closure

1. Remove only demonstrably superseded Reader/shared paths: unused monitor
   snapshot data, stale helpers/events/templates, no-op adapters, duplicate
   routes, and compatibility remnants. Do not perform broad formatting.
2. Search the repository for every replaced Reader/chrome/Drawer symbol and
   justify each surviving reference by ownership.
3. Add an audit resolution matrix mapping C1–C4, H1–H10, M1–M4, and L1–L7 to
   the root fix and proof. Do not rewrite the original findings.
4. Update `SHARED-UI-BEHAVIOR.md`, the canonical Reader plan evidence section,
   and only the Reader-control entries in `TASKLIST.md` after their gates pass.
   Preserve all unrelated Downloader plan/tasklist edits.

#### Batch D — automated/build/isolation gates

Run serially where shared `obj` directories can collide:

1. the complete dedicated Reader test project;
2. the complete shared-component behavior set;
3. Setting and MangaReader Debug and Release builds with zero warnings/errors;
4. the full `Citadel.slnx` regression suite;
5. citizen deployment isolation, proving no shared `Citadel.*.dll` or newly
   bundled third-party dependency;
6. `git diff --check`, stale-symbol search, tracked-temp search, and an exact
   dirty-file ownership review.

A timing-sensitive Core watchdog failure may be classified as an existing
load flake only after the same test passes standalone and a clean rerun proves
the Reader change is not causal. It must never be silently ignored.

#### Batch E — final live WPF evidence and cleanup

1. Rebuild and run the disposable three-chapter Reader harness after Batch A,
   not against `D:\[ MANGA ]`.
2. Verify normal, maximized, minimum-size, and fullscreen states; visible-top
   chrome reveal; final Drawer geometry; selector/active chapter agreement;
   page-only Dim; elapsed auto-scroll and stop rules; Reset; persistence; and
   the three-step Escape chain.
3. Wait for final animation geometry before captures. Restore cursor and
   foreground-window state in `finally` even if the harness fails.
4. Delete `.reader-smoke-harness/` and `.tmp-reader-evidence/` after extracting
   the final result into documentation. No disposable CBZ, screenshots,
   preference files, runtime data, or generated fixture may remain in the
   deliverable worktree.
5. Only after Batches A–E pass may the verdict become **PASS** and the repair
   plan/tasklist be marked complete. Commit, push, version bump, packaging, and
   release remain separately authorized actions.

### 2.3 Final closure evidence

- Batch A: boundary failure and shared resize/drag ownership pass fail-first
  regressions.
- Batch B: sections 3–9 of the canonical contract were traced to one runtime
  owner and proof; newly discovered L8–L16 were recorded before editing and
  repaired at the owning contract.
- Batch C: duplicate input/composition/helper paths were removed; stale-symbol,
  direct-parent-reach-back, preview-handler, dependency, and dirty-scope
  searches pass; the audit contains the complete C/H/M/L resolution matrix.
- Batch D: Reader 91/91, UIA 226/226, complete solution 495/495; Setting and
  MangaReader Debug/Release each build with zero warnings/errors; both citizen
  deployments are isolated and declare only the MangaReader payload.
- Batch E: disposable WPF harness passes 54/54 with six inspected captures;
  real OS clicks exercise uncovered left/center/right zones through the central
  router; maximized/fullscreen/normal/minimum, Reset, persistence, and Escape
  gates pass. Cursor and foreground state are restored, and both temporary QA
  directories were deleted after evidence extraction.

Live QA used one 2560x1440 96-DPI monitor. Multi-monitor/high-DPI behavior is
covered by deterministic geometry tests, not claimed as live evidence. No
`D:\[ MANGA ]` content or user preference store was used or modified.

## 3. Non-negotiable repair rules

1. Keep the locked Reader behavior unchanged. Do not add fit modes, reading
   direction, downloader, streaming, or new persistence.
2. Preserve the existing natural-width rendering, render cache, rolling
   previous/active/next surfaces, history event, and shared scrollbar.
3. There is exactly one instance of every visual layer and one owner of every
   mutable state domain.
4. `ReaderWindow` is a composition root, not a feature implementation file.
5. A future Reader feature requires its feature unit plus one catalog entry;
   it must not require editing `ReaderWindow` composition.
6. Shared components stay screen-blind. Manga/chapter semantics remain under
   `module/mangareader/Reader/`.
7. Characterization and fail-first tests precede destructive extraction.
8. No compatibility wrappers, alternate event paths, duplicate templates, or
   no-op adapters remain after migration.
9. Do not touch Downloader, Archive/Cover Builder, Library, History, CBZ decode,
   or `D:\[ MANGA ]` data.
10. Builds/tests do not count as visual PASS. Live WPF evidence is a separate
    final gate.

## 4. Target ownership and composition

```text
ReaderWindow
├── stable named layer hosts declared once in XAML
├── ReaderFeatureHost                   attach/mount/reverse-dispose owner
├── ReaderChapterCoordinator            only rolling/load/navigation owner
├── ReaderInputRouter                    only window preview-input owner
├── ReaderActivityHub                    typed activity origin pipeline
├── ReaderStateProjection                read-only state exposed to children
└── ReaderFeatureCatalog
    ├── viewport navigation
    ├── chapter navigation
    ├── Drawer + Pin
    ├── fullscreen + chrome + toast
    ├── zoom
    ├── Dim Pages
    ├── auto-scroll
    └── Reset
```

### 4.1 One composition path

- XAML declares the stable page surface and named hosts only.
- `ReaderFeatureHost` receives those existing hosts; it must never append a
  second host set to `RootGrid`.
- Visual features contribute one view to one compatible host.
- Nonvisual controllers attach to contracts but are never assigned to a
  `ContentPresenter.Content` property.
- `ReaderWindow` stores the host in a field and disposes it once, in reverse
  catalog order, before coordinator teardown.
- A host rejects duplicate occupancy and wrong visual/nonvisual registration
  during construction so composition errors fail early.

### 4.2 Stable child contract

Replace concrete parent access with focused interfaces:

- `IReaderStateView`: read-only snapshots and change notifications;
- `IReaderCommands`: validated state mutations accepted by the owning
  controller/coordinator;
- `IReaderViewport`: offset, extent, viewport size, programmatic scrolling, and
  viewport change notification;
- `IReaderChapterNavigation`: chapter list, active chapter, load/jump, and
  boundary preparation;
- `IReaderInputEvents`: typed input requests from the single router;
- `ReaderActivityHub`: manual/programmatic activity with an explicit origin;
- root lifetime token and UI dispatcher access.

`ReaderFeatureContext` must not expose `ReaderWindow`, named controls, or public
setters on `ReaderSessionState`.

### 4.3 Drawer extension contract

`IReaderFeature` retains explicit `Attach`/`Dispose`. A feature may additionally
implement a typed Drawer-contribution provider. Each feature owns one card
composition made from existing shared controls. `ReaderDrawer` only orders and
hosts those opaque cards; it does not hardcode chapter, fullscreen, auto-scroll,
Pin, zoom, Dim, Reset behavior, or their visual templates.

Adding an ordinary future feature is therefore limited to:

1. its own feature file or genuine XAML/code-behind pair; and
2. one explicit `ReaderFeatureCatalog` registration.

## 5. Repair sequence

### Phase R0 — preserve evidence and add fail-first coverage

1. Record the exact dirty-file inventory and preserve the Downloader plan and
   every unrelated user/agent change.
2. Create `tests/Module.Mangareader.Reader.Tests/` following the Library and
   Archive linked-source pattern; do not reference the citizen assembly as a
   normal solution dependency.
3. Add characterization tests for the pre-immersive engine contract:
   surface publication/order, active-chapter rotation, offset preservation,
   close cancellation, history emission, and existing zoom clamping/anchor.
4. Add failing regression tests for the confirmed defects: one visible layer
   per role, successful load removes the blocker, current surface collection
   receives loaded chapters, reverse cleanup, and latest-request-wins commit.
5. Use disposable CBZ fixtures only.

**Exit gate:** failures identify the real regressions; unrelated baseline tests
remain green. Do not begin feature wiring while the base engine is still
uncharacterized.

### Phase R1 — restore the Reader engine before immersive UI

1. Make `ReaderChapterCoordinator.Surfaces` expose the same observable
   collection that the coordinator mutates.
2. Replace `FrameContentHost` no-ops with an explicit host contract for loading,
   progress, status/error, visibility, viewport metrics, and scrolling.
3. Restore the successful-load transition: pages become interactive and the
   loading/error layer stops blocking input.
4. Route every `ScrollChanged` event to the coordinator with its activity
   origin so rolling rotation/preload and anchor preservation work again.
5. Restore exactly one `ReaderZoomController`; route Ctrl+wheel, Ctrl+0, and
   later Drawer controls through it.
6. Add a monotonic navigation generation to the coordinator. Only the newest
   initial load, boundary preparation, or chapter jump may commit visible
   state, title/history, and active chapter.
7. Make a chapter jump reuse the existing load/cache gate, commit page one,
   publish the active event exactly once, then prepare neighbors.

**Exit gate:** the plain Reader works again with pages visible, loading hidden,
manual forward/reverse rolling, zoom, history, and close cancellation before
any immersive control is enabled.

### Phase R2 — collapse to one parent/children composition

1. Replace instantiated duplicate XAML controls/runtime-created layers with one
   named-host topology in final Z order.
2. Refactor `ReaderFeatureHost` to consume those hosts, distinguish visual and
   nonvisual features, reject duplicate mounts, and reverse-dispose.
3. Retain the host and preference lifetime in `ReaderWindow`; perform one
   idempotent close path: cancel root token, stop input, reverse-dispose
   features, flush/dispose preferences, then clear coordinator surfaces.
4. Introduce the focused read-only state/command/viewport/chapter interfaces.
5. Remove direct feature access to `ReaderWindow`, named controls, and mutable
   state setters.
6. Reduce `ReaderWindow.xaml.cs` to construction, context assembly, accepted
   parent commands, load/close lifecycle, and title projection.
7. Prove a catalog-only dummy feature can mount/unmount without editing parent
   composition.

**Exit gate:** one RootGrid layer per role, one visual instance per feature,
one retained/disposed host, no concrete parent reach-back, and the R1 engine
tests remain green.

### Phase R3 — harden shared Drawer and window chrome

1. Give `SettingDrawer` a parent-relative width contract. At runtime its width
   is exactly 25% of the Reader client area; remove the self-width converter.
2. Keep the Drawer overlay-only, horizontally clipped, and hit-testable only
   inside its actual bounds. Its backdrop stays visual and hit-test-free.
3. Implement 200 ms ease-out slide and the system-animation immediate fallback;
   detach storyboard/timer/event state on unload and allow safe reload.
4. Make `SettingWindowChrome` visible on first open, then apply 500 ms idle and
   180 ms fade. Only the 6-DIP trigger and visible chrome are hit-testable.
5. Hold chrome for pointer entry, keyboard focus, drag, and pressed system
   actions. Keep ordinary page scrolling unrelated.
6. Finish shared minimize/maximize/restore/close, drag, double-click, theme, and
   unload/reload cleanup behavior.
7. Complete migration to `NativeWindowChromeBehavior`; delete the old
   Shell-private helper only after all consumers compile and behave correctly.

**Exit gate:** focused shared-component tests cover proportion, animation
fallback, hit testing, initial reveal/fade, system actions, cleanup, and reload.

### Phase R4 — central input, Overlay, Drawer contributions, and zoom

1. Make `ReaderInputRouter` the only preview-input subscriber. Remove the
   alternate window handler after every shortcut is routed centrally.
2. Use Windows system drag thresholds and ignore interactive descendants,
   Drawer bounds, scrollbar/thumb activity, wheel, touch/pan, and Ctrl+wheel
   when resolving background clicks.
3. Keep Overlay geometry hit-test-free. Resolve equal Previous/Menu/Next zones
   from the content viewport, not from a transparent full-window input surface.
4. Implement coalesced 90%-viewport movement over 180 ms with elapsed time,
   ease-out, animation fallback, absolute-boundary disable, and coordinator
   boundary preparation.
5. Menu toggles Drawer; pinned Drawer still closes from explicit Menu/Esc.
   Opening a Drawer blocks only its own left bounds, leaving uncovered center
   and right interactions available as locked.
6. Render typed Drawer contributions in the locked order using shared buttons,
   slider, ComboBox, and scrollbar styles.
7. Populate the chapter dropdown with the entire title, track active chapter,
   disable first/last boundaries, and route buttons/dropdown through the R1
   latest-request-wins jump path.
8. Route Drawer zoom buttons/label through the same controller as Ctrl+wheel
   and Ctrl+0.

**Exit gate:** Overlay does not block ordinary Reader input; Drawer actions are
functional; chapter jumps land at page one; zoom has one state/controller.

### Phase R5 — fullscreen, chrome adapter, toast, and Escape priority

1. Use a native integer `RECT`/`MONITORINFO` definition with correct `cbSize`.
2. Enter fullscreen with current monitor bounds (`rcMonitor`, not work area),
   cover the taskbar without `Topmost`, and convert pixels/DIPs correctly.
3. Save and restore exact bounds, window state, window style, resize mode, and
   monitor context. Do not mark state fullscreen if monitor transition fails.
4. Route F11 and the Drawer contribution through one fullscreen command.
5. Show the two-second fullscreen hint through one toast owner.
6. Enforce one Escape chain in the router: leave fullscreen, then close Drawer
   even when pinned, then close Reader on the next press.

**Exit gate:** deterministic geometry/state tests pass; live current-monitor
fullscreen covers the taskbar and restores exact prior state.

### Phase R6 — Dim, auto-scroll, preferences, and Reset

1. Bind one hit-test-free black Dim layer only to the page content viewport,
   below scrollbar/UI layers. Clamp 0–80 and snap to 5.
2. Route Drawer slider/local reset and Alt+Up/Down/0 through one Dim owner.
3. Make auto-scroll elapsed-time based at one viewport per configured 1–30
   seconds and recompute immediately after speed or viewport-size changes.
4. Carry explicit activity origins through viewport mutations. Auto-scroll may
   ignore only its own origin; manual wheel/touch/drag/keys, Overlay, chapter
   jump, zoom, Drawer open, deactivation, loading/error, and end stop it.
5. Rebuild `ReaderPreferencesStore` around schema v1 with numeric version,
   bounded input, independent field validation, Dim snapping, same-folder
   unique temp + atomic move, temp cleanup, and a process-wide gate keyed by
   normalized storage path.
6. Synchronize owner state changes to the store; load before feature attach;
   debounce slider writes; explicitly flush and dispose on close while keeping
   failures non-blocking.
7. Implement one atomic Reset command: zoom 100, Dim 0, speed 5, running false,
   Pin false; preserve chapter, viewport, Drawer open, and fullscreen.

**Exit gate:** persistence survives restart/update, damaged fields fall back
independently, session-only values reset, and every auto-scroll stop rule is
covered.

### Phase R7 — remove redundancy and prove integration

1. Delete runtime-created duplicate hosts, the self-width converter, unused
   handlers/events, no-op host methods, direct state setters, redundant feature
   instances, dead compatibility paths, and duplicate templates after their
   replacements pass tests.
2. Search the whole repository for old Reader/chrome/Drawer symbols and prove
   every remaining reference has one owner.
3. Update `SHARED-UI-BEHAVIOR.md`, the canonical plan evidence section, and
   `TASKLIST.md` only after the relevant automated and live gates pass.
4. Do not commit, push, bump version, package, or release unless separately
   requested.

## 6. Validation matrix

### 6.1 Automated

| Gate | Required proof |
|---|---|
| Reader engine | surfaces publish correctly; load unblocks; rolling forward/reverse; offset stable; history once; cancellation |
| composition | exact named layer count; no runtime duplicate; visual/nonvisual mount rules; reverse idempotent disposal |
| contract | features cannot mutate state or reach named parent controls; dummy feature is catalog-only |
| input/Overlay | system drag threshold; interactive-descendant exclusion; wheel/drag/touch pass-through; coalesced 90% step; boundaries |
| chapter/zoom | latest request wins; page-one jump; whole-title dropdown; one zoom controller and state |
| shared UI | Drawer 25%, hit-test bounds, animation fallback/cleanup; chrome reveal/fade/actions/reload |
| fullscreen | native struct layout, monitor bounds, DPI conversion, failure rollback, exact restore, Esc order |
| Dim/auto-scroll | viewport-only dim; clamp/snap/hotkeys; elapsed speed; resize update; every stop origin |
| preferences/Reset | bounded field-level fallback; atomic concurrent save; temp cleanup; close flush; exact reset/non-effects |
| build/isolation | Setting and MangaReader Debug/Release zero warnings; citizen deploy has no shared `Citadel.*.dll` |
| regression/hygiene | full `Citadel.slnx`; `git diff --check`; no fixture/runtime/temp data tracked |

Existing green UIA tests do not satisfy these gates unless their assertions
would fail when the claimed Reader behavior is removed.

### 6.2 Live WPF

Use one disposable three-chapter title with multiple overflowing pages. Verify:

- normal, maximized, and minimum-size Reader without new outer scrolling;
- pages visible and loading/error blocker removed after success;
- rolling chapters and zoom still behave like the baseline;
- one chrome, one Drawer, one Overlay, one toast, and no invisible input shield;
- shared scrollbar drag/wheel/touch and Ctrl+wheel remain usable;
- Drawer is 25% at each size, never reflows pages, and right-side navigation
  remains usable while it is open;
- rapid chapter selections commit only the newest selection at page one;
- fullscreen covers the taskbar and restores exact prior geometry/state;
- the three-step Escape chain;
- page-only Dim, auto-scroll speed/stop rules, Reset, restart persistence, and
  session-only defaults.

Visual status was **UNVERIFIED** until this evidence was captured. The final
available-hardware pass is recorded in section 2.3; lack of live
multi-monitor/high-DPI hardware remains reported as a limitation, not PASS.

## 7. Suggested implementation batches

Keep changes reviewable and do not combine them with Downloader work:

1. Reader fail-first tests and engine recovery (R0–R1).
2. Single composition and contract migration (R2).
3. Shared Drawer/chrome repair (R3).
4. Input, Overlay, Drawer contributions, chapter, and zoom (R4).
5. Fullscreen/chrome/toast (R5).
6. Dim, auto-scroll, preferences, and Reset (R6).
7. Redundancy cleanup, full regression, and live QA evidence (R7).

Each batch must leave the narrow tests green. No batch may claim completion
solely because the project builds.

## 8. Done definition

Repair is complete only when:

1. every locked behavior is reachable through one authoritative path;
2. the original continuous-reader engine has no semantic regression;
3. every visual layer has one instance and every state domain has one owner;
4. `ReaderWindow` stays a stable composition root and a future ordinary
   feature needs only its unit plus one catalog entry;
5. redundant WIP paths are deleted, not hidden behind wrappers;
6. dedicated Reader tests, shared tests, build/isolation, full regression, and
   hygiene gates pass; and
7. live WPF evidence passes on available hardware without overstating untested
   monitor/DPI cases.
