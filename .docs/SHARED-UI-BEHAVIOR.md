# Shared UI behavior contract

Status: implemented; automated and normal/minimum/maximum-window visual gates
pass through 2026-09-02. High-DPI and negative-coordinate monitor bounds are
covered by deterministic policy tests; live QA hardware was one 96-DPI monitor. This is
the default contract for every built-in screen and citizen view. Feature screens
compose these controls; they do not copy their templates or restate universal
interaction behavior.

## Ownership

| Shared item | Behavior owned in `setting/Components` |
|---|---|
| `SettingButton` | centered label, content-fit action/table placement, hover/press/focus/disabled states |
| `SettingField` | left-aligned text and placeholder, vertical centering, focus border, disabled state |
| `SettingPasswordField` | the same input alignment/focus/disabled behavior without exposing the password as a dependency property |
| `SettingToggle` | centered track/label, mouse/focus/disabled states, two-state keyboard behavior |
| `SettingSlider` | shared track/thumb, keyboard focus indication, disabled state, step snapping |
| `SettingTabs` | left-aligned tab group, equal outer inset, rounded normal/hover/selected/focus states |
| `SettingViewport` | finite screen root, shared inset, explicit `Contained`/`Document` overflow ownership |
| `SettingActionCard` | compact fill-width surface with flexible content and right-side actions |
| `SettingTable` | equal star columns by default, centered headers, optional interactive-column header sorting with direction indicator, left text cells, grid lines, virtualization and scrolling |
| `SettingTableActions` | one action centered; two actions balanced against the cell edges |
| `SettingDialog` | modal chrome, owner centering and reusable confirmation behavior |
| `SettingCardStyle` | ordinary shared card background, border, radius, padding and row spacing |
| `SettingScrollBar` | **auto-fade scrollbar with reveal/hide behavior, vertical/horizontal orientation support** |

Only the Gallery-editable primitives (`Button`, `Field`, `Toggle`, `Slider`,
and `Table`) have `.presets.json` files. Fixed layout composites do not publish
empty presets: their behavior is the contract above.

## ScrollBar Auto-Fade

**Owner:** `setting/Components/ScrollBar.xaml(.cs)`
**Styles:** `SettingScrollBarStyle`, `SettingScrollViewerStyle`
**Attached behavior:** `ScrollBarAutoFade.IsEnabled` (default: `true`)

### Behavior Contract

- Scrollbar appears only when content overflows the viewport.
- **Reveal** (fade to opacity 1.0) on:
  - Mouse wheel or touch scroll
  - Keyboard scrolling (arrows, Page Up/Down, Home/End)
  - `ScrollViewer.ScrollChanged` event
  - Pointer enters ScrollBar
  - Thumb drag starts
  - ScrollBar receives keyboard focus
- **Hide** (fade to opacity 0) after **1.5s idle** when:
  - No drag in progress
  - Pointer outside ScrollBar
  - ScrollBar does not have keyboard focus
- **Layout stable:** Rail width/height always reserved (10px); opacity changes do not shift content.
- **Orientation:** Vertical and horizontal both supported; track direction correct per axis.
- **Animation:** Respects `SystemParameters.ClientAreaAnimation`; transitions (150ms in, 250ms out) disabled when system animations off.
- **Cleanup:** Timers, storyboards, event handlers detached on `Unloaded`; `ConditionalWeakTable` prevents leaks.
- **Disabled state:** Respects `IsEnabled=false` and `DisabledOpacity` token.
- **Thumb minimum:** 24px along the active scroll axis; the cross-axis rail remains 10px.

### Consumer Migration

All scroll surfaces use `SettingScrollViewerStyle`:
- `SettingViewport` Document mode
- `SettingTable` internal DataGrid
- `SettingList` / `SettingCombo` dropdowns
- MangaReader Library, History, Chapter Selector, Reader Window

No per-screen scrollbar templates. Behavior consistent across app.

---

## Drawer

**Owner:** `setting/Components/Drawer.xaml(.cs)`
**Control:** `SettingDrawer`

- `WidthFraction` is measured against the full host, not the Drawer itself.
  The Reader uses `0.25`, so the final panel is exactly 25% of current client
  width at maximized, normal, and minimum sizes.
- The surface slides from the left over 200 ms with ease-out. System-disabled
  client animation snaps directly to final geometry.
- The root clips horizontally and hit testing exists only inside the actual
  translated panel. Backdrops and outside space remain consumer-owned.
- Size changes retarget final geometry; unload/reload detaches animation state
  and leaves no stale clock or event route.
- Drawer content is composed by the feature. Shared buttons, slider, ComboBox,
  and ScrollViewer templates remain owned here in Setting.
- MangaReader publishes one opaque, feature-owned card composition per Drawer
  feature. The Drawer only orders and hosts those cards; chapter, fullscreen,
  auto-scroll, Pin, zoom, Dim, and Reset layouts are not templates in the
  parent. Each card composes `SettingCardStyle`/`SettingActionCard` with the
  existing shared button, slider, and picker controls.
- Drawer control interaction remains inside the owning feature route. It does
  not publish Reader page-pointer activity; page input and Reader scrollbar
  movement retain their own manual-navigation route.

## Window chrome

**Owner:** `setting/Components/WindowChrome.xaml(.cs)`
**Native behavior:** `NativeWindowChromeBehavior`

- The chrome is visible on first frame, idles for 500 ms, fades over 180 ms,
  and becomes non-hit-testable when hidden.
- A full-width six-DIP physical-top trigger reveals it. Pointer entry, keyboard
  focus, title drag, and pressed system actions hold it visible; ordinary
  document scrolling does not.
- Minimize, maximize/restore, close, drag, and title double-click use one shared
  resize policy. `NoResize` consumes the double-click without mutating window
  state, and a rejected native `DragMove` is non-fatal while its hold is always
  released.
- Colors are dynamic theme resources; native DWM fallback is shared by Shell,
  Settings, and standalone Reader rather than copied into each window.
- Timers, capture, focus, animation, and event state detach on unload and can
  attach safely again.

## Picker and slider refinements

- `SettingComboBoxStyle` honors `DisplayMemberPath` for selected object items
  and binds the selected presenter margin to the ComboBox `Padding`. Consumers
  can therefore use compact responsive padding without copying the template.
- `SettingSlider` carries minimum, maximum, value, and direction into its track;
  reversed direction remains valid for inverse speed semantics.

## 2026-09-02 evidence

- Complete `Citadel.Uia` suite: 226/226.
- MangaReader Reader suite: 91/91. The 2026-09-02 feature-owned card layout and
  render-frame auto-scroll correction are covered automatically; fresh live
  user judgment for those two usability changes remains pending.
- Disposable WPF Reader: 54/54 live checks with inspected maximized,
  fullscreen, normal 1180x760, and minimum 640x480 captures.
- The minimum Reader Drawer keeps its selected chapter label readable without
  horizontal scrolling, and the shared chrome reveals at the physical top edge
  after its hidden state.

---

## Screen responsibilities

A screen owns its content, data bindings, commands/event handlers, automation
names and feature-specific visuals. Its root must use `SettingViewport` in the
mode matching its scroll owner. It must not set a screen-level preferred/minimum
width, replace shared templates, hard-code shared surface chrome, or locally
redefine tab, input, table-header, table-action, focus, hover or disabled
behavior.

Custom visuals remain local when their shape is part of the feature rather than
general application chrome. Current examples are MangaReader cover cards,
chapter selection overlays and the reader window overlays.

## Fluid desktop layout

The Shell opens at the preferred `WindowW`/`WindowH`, centered and clamped once
to the active monitor work area. It never follows navigation, tab selection,
async data, or feature overflow. `Host`, Router transition layers, selected tab
content, and `SettingViewport` explicitly stretch through the full available
content area.

Use `SettingViewport.Mode="Contained"` when a table, collection, overlay, or
reader owns scrolling. Use `Mode="Document"` for a top-aligned document that may
need one vertical fallback scrollbar. Document mode disables horizontal
scrolling and stretches content to the available width. Do not wrap contained
tables or collections in another enabled outer `ScrollViewer`.

The viewport owns the common screen inset. Shared cards use flexible width and
only vertical section spacing; screen-local outer margins and primary-column
`MaxWidth` caps are not part of the contract. Fixed sizes remain valid for
semantic visuals such as covers, icons, toggles, progress tracks, and the
standalone MangaReader surface.

## Review gate

Before adding screen-local UI code, check whether the pattern already exists in
`setting/Components`. If a genuinely reusable pattern is needed by more than
one screen, add it there with presentation in `.xaml`, behavior in `.xaml.cs`,
and a small behavior regression test. Do not add a preset unless Gallery can
actually edit meaningful values for that control.
