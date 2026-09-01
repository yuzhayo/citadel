# PLAN: CamoProf shared UI — Launcher / Editor / Runtime

Status: **IMPLEMENTED — automated and visual gates pass; awaiting LO review (2026-09-01)**

This document is the current UI contract. It supersedes the earlier local-tab,
Editor-management, placeholder-provider, and CamoProf-only component decisions.

## 1. Goal

CamoProf exposes three internal tabs:

1. **Launcher** — daily profile launch, Google account status, GitHub navigation,
   profile creation/deletion, network state, and check visibility mode.
2. **Editor** — intentionally blank and reserved for future bulk profile work.
3. **Runtime** — inspect and install Python, venv, packages, and Camoufox.

Universal visual and interaction patterns are owned by
`setting/Components/`. A screen supplies feature content and handlers; it does
not reimplement tab, table, toolbar, input, button, or dialog chrome.

## 2. Shared-component contract

The reusable controls are:

- `SettingTabs` — complete rounded normal/hover/selected/focus states, including
  a thin outline in normal state. The left-aligned tab group has balanced
  padding on all sides and is vertically centered in its header band. CamoProf
  and MangaReader use the same control.
- `SettingTable` — proportional declared columns, normal row/column lines,
  themed header/cell/selection states, virtualization, and internal scrolling.
  Its legacy string-column API remains available to Settings and Gallery.
- `SettingActionCard` — compact flexible-content/right-action layout used by
  Launcher and MangaReader.
- `SettingTableActions` — one centered or two edge-balanced table actions.
- `SettingButton`, `SettingToggle`, and `SettingPasswordField` — themed actions,
  check-mode switch, and password entry.
- `SettingDialog` — shared modal chrome and confirmation behavior.
- `SettingCardStyle` — the existing shared card surface; no CamoProf-local card
  template is introduced.

Each new control keeps presentation in `setting/Components/<Name>.xaml` and
behavior in its paired `.xaml.cs`. Feature-specific workflows remain inside the
owning module.

## 3. Launcher layout

Launcher contains two separate cards, not one shared surface.

### Card 1 — toolbar

```text
[ Add Profile ] [ Show browser while checking ]    Network: ... [ Refresh ]
```

- `Add Profile` creates a generated resident profile ID, opens Google headed,
  and opens the CamoProf-specific pairing dialog.
- The toggle selects headed/headless mode for account checks only. Relog is
  always headed.
- Network status appears once at the right side.
- Refresh rescans all resident profiles.
- Error/status text is collapsed when empty; there is no permanent helper copy.

### Card 2 — profile table

```text
Profile              Google                GitHub             Action
email/profile-id     [ one status button ] [ Open ]           [ Launch ] [ Delete ]
```

- Columns are `Profile / Google / GitHub / Action` with proportional star
  widths and minimum usable cell widths.
- The table owns overflow scrolling; it must not force the shell wider.
- Google is one button only. Reason and last-check time live in its tooltip,
  never as a second line under the button.
- GitHub opens `https://github.com/` in the same resident session. If the
  browser is closed it is opened headed; if already open, `session.navigate`
  reuses it.
- Action contains `Launch`/`Close`, with Delete aligned at the far right.
- Delete confirms first, closes the tracked browser, validates and removes the
  resident directory, then removes the paired Credenz record. Errors are shown
  in-screen and may not terminate the shell.
- No Modified, Size, first/last-use, or other diagnostic columns are shown.

## 4. Account pairing

Account pairing is CamoProf behavior hosted inside shared controls:

```text
module/camoprof/Launcher/AccountSetupDialog.xaml(.cs)
```

- An unlinked row's Google button resumes detection/pairing.
- A rejected stored password opens the same dialog to update it.
- Email is detected from the active Google account; it is not a user-entered
  profile label.
- Password is entered through `SettingPasswordField` and stored through the
  DPAPI-backed `GoogleCredentialStore`.
- Closing the dialog clears its password field. Async detection/save completion
  after close must not access or close the dead window again.

## 5. Runtime and lifecycle

- Runtime retains the four existing checks: Python, venv, packages, browser.
- Runtime status is loaded only when its tab is first selected or refreshed.
- Setup is blocked while a CamoProf browser session is open.
- While setup runs, Launcher and Editor tabs are disabled.
- CamoProf owns one `BrowserSessionCoordinator` and one `NetworkMonitor`.
- Leaving the screen unsubscribes Launcher events, stops network monitoring,
  and disposes pyhost/browser ownership through the existing cleanup ladder.

## 6. Ownership

```text
setting/Components/
├── Tabs.xaml(.cs)
├── Table.xaml(.cs)
├── ActionCard.xaml(.cs)
├── TableActions.xaml(.cs)
├── Dialog.xaml(.cs)
└── PasswordField.xaml(.cs)

module/camoprof/
├── CamoprofView.xaml(.cs)              composition and lifetime
├── Launcher/
│   ├── LauncherView.xaml(.cs)          launcher behavior
│   ├── LauncherProfileRow.cs           row presentation state
│   └── AccountSetupDialog.xaml(.cs)    account-pairing workflow
├── Runtime/                             runtime behavior
├── Network/                             connectivity sampling/policy
├── Providers/Google/                    Google health/relog/credentials
└── sharedLogic/                         CamoProf catalog/session coordination
```

`module/sharedLogic/` remains the cross-citizen C#/Python seam. No feature
logic moves into `core/` or the shared UI controls.

The Editor tab is an empty placeholder declared directly in `CamoprofView`;
there is no redundant Editor feature folder until bulk editing is implemented.

## 7. Cleanup rules

- Remove the obsolete Editor view and its profile-management implementation.
- Remove all CamoProf/MangaReader-local tab styles after migration.
- Remove unused coordinator adapters, stale helper copy, and obsolete table
  list templates.
- Keep backward-compatible pyhost commands and the legacy `SettingTable`
  string API when another current consumer still uses them.
- Do not delete Credenz data as cleanup.

## 8. Validation gates

| Gate | Pass condition |
|---|---|
| Shared build | Setting builds with 0 warnings/errors |
| Citizen builds | MangaReader and CamoProf build/deploy with 0 warnings/errors |
| Shell build | current Debug shell builds with 0 warnings/errors |
| Pyhost | protocol regression, including `session.navigate`, passes |
| Full suite | all Core/UI/UIA tests pass once after integration |
| Visual | two cards, dark readable table headers, row/column lines, proportional columns, outlined rounded tabs |
| Delete regression | confirmation can open and cancel without shell termination or data deletion |
| Editor | tab is blank and reserved |
| MangaReader | existing feature behavior remains intact under shared tabs |
| Hygiene | `git diff --check`; no generated/runtime/Credenz data is tracked |

## 9. Done definition

Done means the shared components are the only implementation of the repeated UI
patterns; Launcher matches the two-card/table contract; Editor is blank;
MangaReader uses shared tabs; profile and browser lifecycle behavior remains
safe; automated and live visual gates pass; and LO approves before commit.

## 10. Current evidence

- Setting, MangaReader, CamoProf, Shell, and full solution build with 0 warnings
  and 0 errors.
- Pyhost protocol: 23/23 tests pass with resource warnings treated as errors.
- Citadel suite: 309/309 tests pass (108 Core + 14 UI + 187 UIA).
- Live visual pass: two separate cards, readable dark headers, proportional
  lined table, shared rounded tabs with a thin normal-state outline, and
  responsive layout at normal, 900x600 minimum, and maximized window sizes.
- Delete regression: confirmation opened and Cancel preserved `yuzz.cezh02`;
  the shell remained alive.
