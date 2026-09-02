# PLAN: CamoProf Google account health + network guard

Status: **IMPLEMENTED — automated and visual gates pass; linked-account relog smoke awaits LO (2026-09-01)**

> **Partially superseded (2026-09-03) — account pairing rules.** The
> `Detect account` / manual-password `Save profile` pairing flow described in
> §3.1 was replaced by Google Enrollment (type-once capture):
> `module/camoprof/Providers/Google/Enrollment/` + the
> `google.enrollment.*` pyhost commands (see `module/sharedLogic/pyhost/README.md`).
> The health-check, relog, and storage rules of this plan remain authoritative
> EXCEPT where marked [superseded by enrollment] below. The "never captured
> from Google's page" boundary now reads: the password typed into Google's
> login page may be captured ONLY by an explicitly-armed enrollment listener
> (exact `accounts.google.com` origin, password fields only, headed session
> only, one-shot handover into DPAPI storage) — never by any other path.

Depends on: `PLAN-camoprof-ui-refactor.md` and the existing pyhost v1 contract.

Scope: CamoProf, additive pyhost commands used by CamoProf, and the shared UI
controls explicitly adopted by the current UI-refactor plan.

## 1. Goal

Turn the Launcher `Check Google` cell into a real health action for each
persistent browser profile:

1. prove the network is stable enough to make a reliable decision;
2. open or reuse the row's resident profile;
3. determine whether Google is signed in and detect the active email address;
4. show the result in that row;
5. only when the result is genuinely `SignedOut`, open a headed browser and
   attempt relog with the same email/password stored in Credenz;
6. leave challenges such as 2FA or CAPTCHA visible for the user to finish;
7. verify again and persist the refreshed session in the same profile.

The arbitrary profile-name input is removed. The email detected from Google is
the user-facing row identity; a generated internal profile ID remains the
stable filesystem key.

## 2. Locked behavior

- `Check Google` means **account health**, not merely "browser process exists".
- Network instability must never be classified as a dead Google session.
- `SignedOut` is the only state allowed to start relog.
- `Offline`, `Degraded`, `Recovering`, provider-unreachable, DNS failure, and
  navigation timeout never start relog.
- A manual `Check Google` performs at most one automatic relog attempt. There
  is no infinite login loop.
- Relog always becomes headed, even when the health check ran headless.
- Email is detected from the authenticated Google account surface; Google
  display name is not used as the profile name.
- A detected email that differs from the row's saved email produces
  `Wrong account`. It is not silently adopted or overwritten.
- Email/password stay paired with the same generated profile ID in Credenz.
- Existing profile folders are preserved in place. No automatic rename or
  destructive migration is allowed.
- CamoProf navigation-away continues to close all CamoProf-owned browsers and
  pyhost. The persistent profile keeps the refreshed cookies/session.

## 3. User-visible flow

### 3.1 Add a new profile

```text
Launcher
[ Add Profile ]

Browser opens headed. User completes the first Google login.

Pending profile
Detected account: user@gmail.com
Password for future relog: [ ******** ] [ Save profile ]
```

Detailed rules:

1. `Add Profile` generates an internal ID such as `p_<guid>` and opens its
   persistent browser directory. No profile name is requested.
2. The user performs the initial login normally in the headed browser.
3. `Detect account`/`Save profile` asks pyhost for the active Google email.
4. When exactly one active email is identified, the Launcher pairing dialog
   asks for the password once and saves the account record under the generated
   ID.
5. Launcher displays the detected email, not the generated ID.
6. Until the account is detected, the row is `Unlinked profile` and cannot
   auto-relog.

Citadel does not attempt to scrape or recover the password typed into Google's
page. The password for future relog is entered explicitly into CamoProf after
the email has been detected.
[**superseded by enrollment (2026-09-03):** steps 3–4 above are replaced by
the enrollment flow — the password typed into Google's own login page is
captured once by the armed enrollment listener and stored DPAPI-encrypted;
there is no second password prompt. §1's health-check and relog rules are
unchanged.]

### 3.2 Check one Launcher row

```text
Check Google
  -> Network preflight
     -> Offline/Degraded/Recovering: stop, show network state
     -> Stable but Google unreachable: stop, show Provider unavailable
     -> Stable + Google reachable: inspect resident profile
        -> Active + matching email: close temporary check, show Active
        -> Active + different email: show Wrong account
        -> SignedOut: open headed relog flow
        -> Challenge: keep headed browser open, show Action required
```

If the row already owns a launched browser, the check reuses that session. If
not, it opens a temporary session using the selected headed/headless check
mode and closes it after a conclusive healthy result.

### 3.3 Relog

1. Close a temporary headless check context before relog.
2. Open the same profile ID headed on Google's sign-in flow.
3. Read the paired email/password from Credenz.
4. Fill and submit only the ordinary email/password steps.
5. If Google asks for 2FA, CAPTCHA, passkey, device confirmation, recovery, or
   another unknown step, stop automation and expose the browser as
   `Action required`.
6. On successful navigation to the authenticated account surface, detect the
   email again and require it to match the stored email.
7. Keep the browser session open when user action is required; otherwise close
   the temporary browser after success and update the row to `Active`.

## 4. Network Stability feature

Network monitoring is a separate CamoProf feature. It does not know about
profiles, credentials, Google DOM, or relog.

```text
module/camoprof/Network/
├── NetworkMonitor.cs       owns sampling lifetime and the current snapshot
├── NetworkProbe.cs         lightweight HTTP/DNS probes through one HttpClient
├── NetworkPolicy.cs        pure sample-to-state classification
└── NetworkState.cs         Stable/Degraded/Offline/Recovering snapshot
```

Rules:

- Monitoring starts when CamoProf opens and stops when the view is disposed.
- It uses lightweight connectivity probes only; it never starts a browser.
- It keeps a short rolling sample window rather than trusting one request.
- `Offline`: the OS link is unavailable or all recent general probes failed.
- `Degraded`: recent probes alternate between success and failure.
- `Recovering`: connectivity has returned but has not yet produced two
  consecutive successful samples.
- `Stable`: the latest probe and at least one preceding probe succeeded.
- The snapshot separately records general connectivity and Google endpoint
  reachability so a Google outage/block is not called a total network outage.
- A manual provider check may request an immediate fresh sample instead of
  waiting for the next monitor tick.
- Network status is shown once in the Launcher toolbar; row status remains
  provider-specific.

The first implementation uses a modest interval while CamoProf is visible and
an immediate preflight for user-triggered checks. It does not become a
machine-wide background service.

## 5. Account and credential storage

```text
Credenz/google/
├── profiles/
│   └── <profile-id>/               existing Camoufox persistent home
└── accounts/
    └── <profile-id>/
        ├── identity.json           profile ID, email, provider, timestamps
        └── password.dat            password payload for relog
```

- `identity.json` is the catalog record and never contains the password.
- `password.dat` is protected with Windows DPAPI `CurrentUser` and is written
  and read only by one `GoogleCredentialStore`.
- The storage seam owns protection/unprotection; pyhost and UI never open the
  file directly.
- Automation receives the decrypted value only for the active relog command.
- The password is never echoed in a pyhost response or diagnostic message.
  [**superseded by enrollment (2026-09-03):** the single exception is the
  one-shot `google.enrollment.finish` response, which carries email/password
  exactly once after the account is proven active; every other response —
  including all enrollment `status`/error responses — remains secret-free.]
- Storage remains inside the already gitignored Credenz vault.
- DPAPI does not change the automation flow: the store decrypts immediately
  before relog and passes the value to the existing pyhost request pipe. Do not
  add a general vault framework or external dependency.

Existing folders without `identity.json` remain visible using their current
folder name and status `Unlinked`. Their next headed `Detect account` action
creates the account record without moving the browser directory.

## 6. Row state contract

The Google column is non-colour-only and exposes one of these labels:

| State | Meaning | Allowed next action |
|---|---|---|
| `Unknown` | Not checked yet | Check |
| `Checking` | Network/provider check running | Wait |
| `Active` | Google authenticated as saved email | Check again |
| `Signed out` | Conclusive Google signed-out response | Relog |
| `Relogging` | One automated relog attempt running | Wait |
| `Action required` | 2FA/CAPTCHA/passkey/unknown step | Continue in browser |
| `Wrong account` | Active email differs from saved email | Press Google status and correct the resident account |
| `Offline` | No usable network | Retry after recovery |
| `Degraded` | Network result is unreliable | Retry after stable |
| `Provider unavailable` | Internet works but Google cannot be reached | Retry later |
| `Credential rejected` | Ordinary password step rejected | Update password |
| `Unlinked` | Existing profile has no detected account record | Detect account |

Every transition carries a short timestamp (`Last checked`) and a plain-text
reason. Status is not inferred solely from button colour.

## 7. Ownership and target tree

```text
module/camoprof/
├── Launcher/
│   ├── LauncherView.xaml(.cs)          bind row actions and statuses
│   ├── LauncherProfileRow.cs           row presentation state
│   └── AccountSetupDialog.xaml(.cs)    add/link/update-password flow
├── Network/                             independent connectivity feature
│   └── ...
├── Providers/Google/
│   ├── GoogleAccountService.cs         health/relog orchestration
│   ├── GoogleAccountState.cs           provider state model
│   ├── GoogleCredentialStore.cs        Credenz account records
│   └── GoogleAccountRecord.cs
└── sharedLogic/
    ├── BrowserSessionCoordinator.cs     one-host/session serialization
    └── ProfileCatalog.cs                profile ID + display identity scan

module/sharedLogic/
├── cs/PyHost.cs                         additive protocol client methods
├── pyhost/pyhost.py                     browser-level inspect/relog commands
├── pyhost/README.md                     updated command contract
└── tests/test_pyhost.py                 focused command/error regressions
```

Ownership rules:

- Network classification stays in `Network/`; no provider imports there.
- Google state and relog policy stay in `Providers/Google/`.
- Launcher owns account creation, credential changes, account service actions,
  and row presentation; the pairing workflow stays in its own dialog file.
- `BrowserSessionCoordinator` remains the sole serializer of browser
  mutations, so Check, Launch, Close, Delete, and Relog cannot race.
- PyHost remains transport/browser mechanics. It does not decide whether a
  network result permits relog; the CamoProf Google service owns that rule.
- No changes to `core/`, installer, or citizen public contracts. Reusable UI
  changes in `setting/Components/` and MangaReader's shared-tab adoption are
  governed by `PLAN-camoprof-ui-refactor.md`, not by provider logic.

## 8. Additive pyhost contract

Keep protocol v1 backward-compatible:

- `session.open` gains optional `headless` (default remains `false`).
- `google.inspect(session)` returns authenticated state, detected email when
  available, final URL, and a structured classification. It never returns a
  display name as identity.
- `google.relogin(session, email, password)` performs one ordinary credential
  attempt and returns `active`, `credential_rejected`, or
  `action_required` plus the final URL and detected email when available.
- Network/navigation failures remain structured errors and never become
  `signed_out` or `credential_rejected`.
- Credentials are request-only values. They are not logged, echoed, or stored
  by pyhost.

No generic page scripting API, event protocol v2, stealthB, proxy, browser
pool, or periodic account-check scheduler is added by this plan.

## 9. Implementation sequence

### Step 0 — checkpoint and baseline

- Keep the current uncommitted CamoProf UI refactor and shell outline changes
  separate; checkpoint them before provider implementation.
- Confirm existing profile launch/delete and pyhost tests remain green.

### Step 1 — Network feature

- Add `Network/` models, probe, rolling classification, cancellation, and
  Launcher toolbar state.
- Keep provider buttons disconnected until the guard is proven.
- Verify offline, intermittent, recovery, and stable transitions.

### Step 2 — Stable profile identity + Credenz records

- Add generated profile IDs and account metadata lookup.
- Remove arbitrary profile-name input from the new-profile flow.
- Preserve existing folders as `Unlinked`; do not rename or delete them.
- Add explicit password-save/update path after email detection.

### Step 3 — Google inspection

- Add the backward-compatible pyhost command and C# client method.
- Detect the active email and implement matching/mismatch rules.
- Wire the Google row button and the headed/headless check toggle.
- A temporary check session is always closed after a conclusive non-action
  result.

### Step 4 — Guarded relog

- Add `GoogleAccountService` orchestration and one-attempt relog.
- Require a fresh Stable network snapshot and Google reachability.
- Handle credential rejection and manual challenge without retry loops.
- Verify the resulting email before declaring the row Active.

### Step 5 — lifecycle and visual pass

- Ensure navigation-away cancels probes and closes pyhost/browser sessions.
- Ensure Delete closes the session before removing both profile and account
  record.
- Verify row state text, disabled/busy states, keyboard access, and compact
  Launcher proportions.

## 10. Validation gates

Keep validation proportional; no broad new test framework is required.

| Gate | Pass condition |
|---|---|
| Network policy | deterministic samples produce Stable/Degraded/Offline/Recovering correctly |
| Pyhost regression | existing tests plus inspect/relog classifications pass without resource warnings |
| Module build | CamoProf builds/deploys with 0 warnings and 0 errors |
| Full suite | current Citadel Core/UI/UIA suite remains green once after integration |
| Offline smoke | Check reports Offline and opens no browser/relog |
| Degraded smoke | failed/alternating probes never start relog |
| Healthy smoke | saved resident profile reports Active with the expected email |
| Signed-out smoke | stable network + signed-out profile opens one headed relog attempt |
| Challenge smoke | manual challenge stays open as Action required; no retry loop |
| Mismatch smoke | different active email reports Wrong account and does not overwrite metadata |
| Lifecycle smoke | leaving CamoProf stops monitor and leaves no owned pyhost/browser process |
| Hygiene | `git diff --check`; no credential or profile content becomes tracked |

## 11. Done definition

The feature is complete when:

- new profiles need no arbitrary name and display the detected Google email;
- existing profiles can be linked without moving their directories;
- `Check Google` distinguishes account, provider, and network failures;
- only a conclusive SignedOut state can trigger one headed relog attempt;
- the saved email/password pair is reused from Credenz;
- manual Google challenges remain visible and recoverable;
- Network monitoring is independently owned and stops with CamoProf;
- all browser mutations stay serialized and cleanup remains deterministic;
- automated gates and the live profile/network smoke pass; and
- LO reviews the visible result before commit.

## 12. Explicit non-goals

- GitHub account checking or GitHub relog wiring.
- Bulk profile import/add (the Editor blank area remains reserved for it).
- Background account checking when CamoProf is closed.
- Bypassing CAPTCHA, 2FA, passkey, recovery, or device confirmation.
- Moving profiles between Windows machines.
- General password-manager UI or multi-provider vault abstraction.
- Changes to Citadel core, Settings, MangaReader, installer, or release flow.

## 13. Implementation evidence

- Network policy smoke: `Offline`, `Degraded`, `Recovering`, and `Stable` pass.
- Credenz smoke: DPAPI `CurrentUser` round trip passes and the stored payload
  does not contain plaintext.
- Pyhost regression: 23 tests pass with resource warnings treated as errors.
- CamoProf module build/deploy: 0 warnings and 0 errors.
- Full Citadel suite: 306 tests pass (108 Core + 14 UI + 184 UIA).
- Visual smoke: Launcher renders two separate cards, dark readable table
  headers, proportional columns, and shared outlined tabs; Editor is reserved.
- Live unlinked smoke: Google check returns `Unlinked`, starts no relog, and
  navigation-away leaves no CamoProf-owned pyhost process.
- Pending LO smoke: link a real account, then exercise the saved-session,
  signed-out relog, challenge, and wrong-account branches with that account.
