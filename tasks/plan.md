# Implementation Plan: CamoProf Add Profile vertical feature

## Goal

Refactor Add Profile into one end-to-end feature whose public entry point is the
only operation called by Launcher. The feature owns profile enrollment, Google
navigation, one-shot credential capture, persistence, cancellation, and its
result. Pyhost supplies generic command transport and browser-session leases;
it does not contain Add Profile or Google business rules.

The finished flow is:

```text
Launcher
  -> AddProfileFeature.ExecuteAsync(request)
    -> AddProfilePyHostClient
      -> camoprof.add_profile.* commands
        -> AddProfilePlugin
          -> SessionHost lease
            -> one resident context + one primary page owner
  <- non-secret AddProfileResult
```

No Launcher, coordinator, provider, or pyhost-core code may bypass that path
for Add Profile.

## Scope

In scope:

- the Add Profile button flow and the existing enrollment path it invokes;
- existing-profile credential enrollment that currently reuses the same flow;
- pyhost command registration, session/page ownership, and lifecycle needed by
  this feature;
- migration of the current Google enrollment implementation into the feature;
- removal of the procedural double-window workaround after replacement;
- regression, architecture, protocol, and live-smoke verification.

Out of scope:

- Google account health rules, ordinary Launch, GitHub navigation, Runtime,
  Manga Reader, downloader, and shared UI redesign;
- new shared visual primitives;
- changing DPAPI storage format or profile directory layout;
- changing the existing one-shot credential-capture security contract.

## Locked Architecture Decisions

### 1. Feature owns semantics end to end

`LauncherView` may invoke Add Profile and display a non-secret result. It may
not open a browser session, navigate Google, poll enrollment, save credentials,
or repair pages. All of those operations belong to Add Profile.

### 2. SharedLogic is infrastructure only

Shared pyhost code may own:

- command registration and dispatch;
- process lifecycle and cancellation;
- browser context/session registry;
- a generic primary-page lease and owner token;
- generic structured protocol errors.

Shared pyhost code must not contain or import concepts named Add Profile,
Google, enrollment, email, password, credential capture, or account health.
Dependency direction is always `AddProfile -> SharedLogic`, never the reverse.

### 3. Primary page has one explicit owner

For every registered session:

- the context is live;
- the primary page reference is live;
- exactly one owner token controls that page;
- a feature cannot access the mutable session registry directly;
- commands incompatible with the current owner return `SESSION_BUSY` rather
  than operating on a stale or foreign page.

Add Profile claims the existing resident page, arms capture on that page, and
then navigates it. It does not call `ctx.new_page()` during start. Teardown
rotates to a clean resident page through `SessionHost`, transfers ownership,
and only then closes the capture page.

### 4. Add Profile is a pyhost plugin, not a core branch

The feature registers its own command namespace:

```text
camoprof.add_profile.start
camoprof.add_profile.status
camoprof.add_profile.finish
camoprof.add_profile.cancel
```

Core dispatch does not gain four new hardcoded feature branches. CamoProf's
composition root supplies the plugin descriptor when its shared pyhost process
is created. Adding another feature later must not require editing Add Profile.

### 5. Preserve the proven security and cancellation behavior

- capture is armed before navigation;
- `start` returns once armed; navigation remains cancellable and non-blocking;
- only the exact Google password field/origin is accepted;
- emptying the field clears the candidate;
- `finish` releases the password once to the feature service;
- the feature DPAPI-saves it before returning a non-secret result;
- cancel, expiry, browser close, session death, and app shutdown disarm capture;
- a secret is never returned through Launcher-visible types or logs.

### 6. Existing shared UI is reused

`GoogleEnrollmentDialog` continues to use the existing shared `SettingDialog`
and shared controls. Moving/renaming it into the feature folder does not
authorize a new primitive, style, template, or screen-local replacement.

## Target Ownership

```text
module/sharedLogic/
├── cs/
│   └── PyHost.cs                         # generic transport/process only
└── pyhost/
    └── core/
        ├── command_registry.py           # generic plugin registration
        ├── session_host.py               # private session registry
        └── session_lease.py              # primary-page owner contract

module/camoprof/Features/AddProfile/
├── AddProfileFeature.cs                  # only Launcher-facing contract
├── AddProfileCoordinator.cs              # end-to-end C# orchestration
├── AddProfilePyHostClient.cs             # typed feature protocol adapter
├── AddProfileRequest.cs
├── AddProfileResult.cs                   # contains no password
├── AddProfileDialog.xaml(.cs)            # shared UI composition
└── PyHost/
    ├── plugin.py                         # registration entry point
    ├── commands.py                       # feature command boundary
    ├── enrollment.py                     # feature state machine
    └── password_capture.py               # Google-specific capture
```

File names may follow existing repository casing, but ownership may not move
back into `module/sharedLogic/pyhost/providers/google/`.

## Contracts

### Shared session lease

The core contract must make invalid ownership unrepresentable:

```text
SessionHost.open(profile, headed)
SessionHost.claim_primary(session, owner) -> SessionLease
SessionLease.page                         -> live page
SessionLease.rotate_to_resident()         -> live replacement + ownership transfer
SessionLease.drop()                       -> context/session cleanup
```

Only `SessionHost` reads or writes the registry. A lease is idempotently
releasable and cannot operate after release/session death.

### Add Profile feature contract

Input contains profile intent and optional expected email. Output is a
discriminated non-secret result: completed, active-without-password,
cancelled, expired, browser-gone, wrong-account, or failed. Protocol errors
remain structured and stable.

Initial Add and existing-profile credential repair may use the same feature
contract, but neither caller gains access to enrollment internals.

## Implementation Plan

### Phase 0 — Prove the current structural defect

1. Add RED protocol tests proving that current enrollment leaves
   `sess["page"]` closed while enrollment is active.
2. Add RED tests for Add Profile's required one-way call path: Launcher must
   not call `BrowserSessionCoordinator.OpenAsync` before invoking the feature.
3. Add RED coverage for manual capture-window close removing the session and
   releasing profile ownership.

Checkpoint: tests fail for the intended invariant, not due to setup errors.

### Phase 1 — Extract generic pyhost ownership infrastructure

4. Introduce the generic command registry and migrate existing command dispatch
   behavior without changing observable commands.
5. Encapsulate the session dictionary behind `SessionHost`; introduce the
   primary-page lease/owner contract.
6. Migrate existing ordinary session commands to the new core contract and
   preserve Launch, GitHub, inspect, relogin, close, shutdown, and EOF cleanup.

Checkpoint: pre-existing Python tests pass unchanged; no Add Profile semantics
exist in the new core; deployed pyhost payload imports successfully.

### Phase 2 — Move Add Profile pyhost behavior into its plugin

7. Register the `camoprof.add_profile.*` namespace from the feature plugin.
8. Move the enrollment state machine and password listener into the Add Profile
   feature folder without changing security/state outcomes.
9. Start enrollment by claiming and arming the existing resident primary page;
   do not create a second page.
10. Route teardown, navigation failure, manual browser close, expiry, and
    cancellation through the lease so session and page ownership remain valid.

Checkpoint: exactly one live primary page owner at every externally observable
state; feature tests cover every terminal path and stale commands are rejected.

### Phase 3 — Make C# Add Profile one directional

11. Add the typed feature client/coordinator and retain secret handling solely
    inside the feature boundary.
12. Change Launcher Add Profile to one call to `AddProfileFeature.ExecuteAsync`;
    remove its direct session open/navigation/poll/storage responsibilities.
13. Route the existing credential-repair invocation through the same public
    feature contract without exposing its internals.
14. Move the existing dialog/result/policy implementation into the feature
    owner and continue composing shared UI controls.

Checkpoint: static/behavior tests prove Launcher cannot open a session as part
of Add Profile and UI-visible types cannot carry a password.

### Phase 4 — Delete superseded paths

15. Remove old `google.enrollment.*` routing and feature-specific methods from
    generic `PyHost`/`BrowserSessionCoordinator` after all callers migrate.
16. Delete `took_over_window`, the host backlink,
    `_restore_resident_page`, duplicate session-opening code, stale comments,
    and obsolete tests that assert the workaround instead of the invariant.
17. Update pyhost protocol documentation and task history to distinguish the
    superseded workaround from the final ownership contract.

Checkpoint: searches find no Add Profile/Google semantics in shared core and no
feature code accessing mutable session internals.

### Phase 5 — End-to-end verification

18. Run focused Python and CamoProf tests, then the full Release suite/build.
19. Run the disposable-profile real Camoufox harness with an actual OS-window
    count while enrollment is active; protocol success alone is insufficient.
20. Operator smoke: Add Profile, complete login, cancel mid-navigation, close
    browser mid-enrollment, restart app, and verify Check Google/relogin.
21. Verify no password appears in stdout/stderr/status/results and no orphan
    pyhost/Camoufox process or stale `PROFILE_BUSY` remains.

## Architecture Enforcement

Add tests that fail when:

- sharedLogic imports a CamoProf feature;
- Add Profile accesses the session registry rather than a lease;
- command namespaces collide;
- two owners claim one primary page;
- a registered session exposes a closed primary page;
- Launcher opens/navigates a session during Add Profile;
- a new feature modifies Add Profile internals instead of registering its own
  command namespace.

Repository guidance must state: a new feature consumes the current shared
contract. If the contract is insufficient, work stops for explicit owner
approval before shared core is modified.

## Definition of Done

- One click produces one headed Camoufox window.
- Add Profile has one public entry contract and one direction of control.
- Session registry never exposes a dead primary page while registered.
- Manual close/cancel/failure leaves no stale session or profile lock.
- SharedLogic contains infrastructure only; Add Profile owns all semantics.
- The procedural workaround and redundant paths are deleted.
- Focused and full automated gates pass.
- Real OS-window and operator smoke evidence pass; compilation alone is not a
  visual PASS.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Core session refactor regresses existing Launch/check flows | High | Behavior-preserving Phase 1 with unchanged tests before feature migration |
| Page replacement kills persistent context | High | Lease creates/transfers replacement before closing the current last page |
| Cancellation races background navigation | High | Navigation task remains feature-owned and is cancelled/awaited before release |
| Plugin loading pollutes core with feature knowledge | Medium | Generic descriptors supplied by CamoProf composition; dependency-direction test |
| Tests pass without proving visible window count | Medium | Enumerate actual process-owned windows in live disposable-profile smoke |
| Old and new command paths coexist indefinitely | Medium | Phase 4 deletion is mandatory before final smoke or completion claim |

## Open Questions

None. Ownership, scope, and completion criteria are locked by the operator's
feature-modularity requirement.
