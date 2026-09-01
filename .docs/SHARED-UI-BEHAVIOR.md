# Shared UI behavior contract

Status: implemented; automated and normal/minimum/maximum-window visual gates
pass on 2026-09-01. High-DPI and negative-coordinate monitor bounds are covered
by deterministic policy tests; live QA hardware was one 96-DPI monitor. This is
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
| `SettingTable` | equal star columns by default, centered headers, left text cells, grid lines, virtualization and scrolling |
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
