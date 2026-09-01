# Shared UI behavior contract

Status: implemented; automated and normal/minimum/maximum-window visual gates
pass on 2026-09-01. This is the default contract for every built-in screen and
citizen view. Feature screens compose these controls; they do not copy their
templates or restate universal interaction behavior.

## Ownership

| Shared item | Behavior owned in `setting/Components` |
|---|---|
| `SettingButton` | centered label, content-fit action/table placement, hover/press/focus/disabled states |
| `SettingField` | left-aligned text and placeholder, vertical centering, focus border, disabled state |
| `SettingPasswordField` | the same input alignment/focus/disabled behavior without exposing the password as a dependency property |
| `SettingToggle` | centered track/label, mouse/focus/disabled states, two-state keyboard behavior |
| `SettingSlider` | shared track/thumb, keyboard focus indication, disabled state, step snapping |
| `SettingTabs` | left-aligned tab group, equal outer inset, rounded normal/hover/selected/focus states |
| `SettingActionCard` | compact shared surface, flexible content, right-side actions, equal outer inset |
| `SettingTable` | equal star columns by default, centered headers, left text cells, grid lines, virtualization and scrolling |
| `SettingTableActions` | one action centered; two actions balanced against the cell edges |
| `SettingDialog` | modal chrome, owner centering and reusable confirmation behavior |
| `SettingCardStyle` | ordinary shared card background, border, radius, padding and row spacing |

Only the Gallery-editable primitives (`Button`, `Field`, `Toggle`, `Slider`,
and `Table`) have `.presets.json` files. Fixed layout composites do not publish
empty presets: their behavior is the contract above.

## Screen responsibilities

A screen owns its content, data bindings, commands/event handlers, automation
names and feature-specific visuals. It may set a minimum size needed by its own
content. It must not replace shared templates, hard-code shared surface chrome,
or locally redefine tab, input, table-header, table-action, focus, hover or
disabled behavior.

Custom visuals remain local when their shape is part of the feature rather than
general application chrome. Current examples are MangaReader cover cards,
chapter selection overlays and the reader window overlays.

## Review gate

Before adding screen-local UI code, check whether the pattern already exists in
`setting/Components`. If a genuinely reusable pattern is needed by more than
one screen, add it there with presentation in `.xaml`, behavior in `.xaml.cs`,
and a small behavior regression test. Do not add a preset unless Gallery can
actually edit meaningful values for that control.
