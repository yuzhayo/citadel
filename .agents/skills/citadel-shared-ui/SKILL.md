---
name: citadel-shared-ui
description: Plan, review, or implement Citadel screens and UI while preserving the repository's shared-component ownership and approval boundary. Use for every task in C:\VSCODE\citadel that may touch a screen, module view, control, style, template, or UI behavior.
---

# Citadel shared UI contract

Use Citadel's existing shared UI system as the default and authoritative UI
surface. A module owns feature semantics and composition; `setting/` owns
reusable controls, styles, tokens, and their universal behavior.

## Required discovery before UI work

Read the relevant current files before proposing or editing UI:

1. `setting/Components/` for shared controls and each control's behavior pair.
2. `setting/SettingResources.xaml` for shared styles, tokens, and templates.
3. `.docs/SHARED-UI-BEHAVIOR.md` for the documented behavior contract.
4. The owning module's current view, feature contract, plan, and tests.

Search by behavior and visual role, not only by the requested label. Confirm
whether an existing component, style, or composition already provides the
needed behavior. Do not infer absence from one screen.

## Mandatory reuse and ownership

- Reuse an existing shared component whenever it covers the required role or
  behavior. Configure and compose it; do not copy its XAML, template, style, or
  behavior into a module.
- Screen/module code may own domain data, commands, routing, and arrangement of
  shared controls. Universal visual or interaction behavior stays in
  `setting/` and must not be hardcoded per screen.
- Keep parent screens composition-only. A feature owns its own state and its
  own visual composition; adding a normal feature must not require teaching the
  parent that feature's controls or semantics.
- When a shared component has a documented behavior pair, update or extend that
  owner once. Do not add a second behavior path in a consumer.

## New-component approval boundary

Do not create a new primitive component, shared style, template, or parallel
screen-local replacement without explicit user approval. When the current
inventory cannot satisfy a requirement, stop before creating it and report:

- the shared components and behavior contracts inspected;
- the exact capability gap;
- whether an existing component can be extended safely; and
- the smallest proposed owner and API.

The only standing exception is a **combo component**: a reusable composition of
existing shared primitives. It may be created without another approval when it:

- introduces no new low-level rendering or interaction behavior;
- delegates control behavior to the shared primitives it contains;
- has a clear reusable role rather than one-screen hardcoding; and
- documents and tests the combination's layout/behavior contract at its owner.

A combo component is not permission to clone a primitive or bypass an existing
shared behavior.

## Implementation and review gate

Before editing, state which existing shared components/styles will be reused
and which file owns each feature composition. During cleanup, remove superseded
screen-local templates, renderers, and compatibility paths that the new owner
makes redundant, while preserving unrelated worktree changes.

Validation must distinguish build/test evidence from live visual evidence.
Never call a UI visually correct solely because it compiled or unit tests
passed.
