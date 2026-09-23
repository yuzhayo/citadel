# Module Runtime Start/Stop — implementation plan

Status: implementation plan; not implemented.

## Goal and non-negotiable behavior

This plan separates a citizen's permanent navigation identity from its
optional, costly runtime.

- Every discovered citizen remains registered and visible in the sidebar.
- Opening a stopped citizen shows a lightweight stopped screen with **Start**.
- **Start** creates the runtime and then shows the citizen's normal view.
- **Stop** stops only that citizen's runtime. It never unregisters its route.
- **Force Stop** is an explicit emergency action. It aborts a stuck runtime
  without waiting for normal work to drain; it is never invoked automatically.
- Navigation away only detaches presentation; it never stops a running runtime.
- At process boot, every citizen starts **Stopped**. Runtime state is not
  persisted as "Running"; durable module work is persisted by its own owner.
- A failed Stop never force-disposes runtime resources. The route remains
  visible and the host reports the failure with a retry path.

Out of scope: DLL unloading, route/sidebar toggles, stop-on-navigation,
disk-health features, a global module-settings toggle page, or new shared UI
primitives.

## Current evidence

- `ModuleGate.RegisterOnMain` eagerly invokes `IResidentModule` at registration.
  Today that starts MangaReader's `DownloaderBackgroundService` before the user
  opens the route.
- `Router` directly invokes `IModule.CreateView` and owns that view lifetime.
  Its existing retained cache is navigation-only and cannot express explicit
  runtime Stop.
- `MangaReaderModule` is the only current `IResidentModule`. Its queue currently
  has synchronous `StopAll`, but no public pause-and-drain barrier for an
  explicit module Stop.
- Yuzvid's `CreateView` creates and activates its browser/controller, and its
  view implements `IRetainedViewModule`; it must therefore be created only on
  Start and retained across navigation.

## Ownership and boundaries

| Owner | Responsibility | Must not own |
|---|---|---|
| `Citadel.Contract` | Optional citizen lifecycle contract | Shell UI or module-specific job logic |
| `Citadel.Shell/Runtime` | Per-route runtime state, UI host, runtime lifetime, visual mount/unmount | Manga queue, Yuzvid browser internals, provider logic |
| `ModuleGate` | Register/unregister actual discovered citizens | Start a runtime during registration |
| `Router` | Navigate and retain route hosts | Construct/destroy citizen runtime directly |
| Each citizen | Construct its runtime and prepare its own work for Stop | Referencing `Citadel.Shell` or another citizen |
| `setting/Components` | Existing visual primitives | Module lifecycle behavior |

The shell-level coordinator is generic orchestration, not a module-specific
service. Manga pause rules stay in `module/mangareader/Features/Downloader/Queue`.

## Contracts

`IModule` remains unchanged.

Add this optional contract in `core/Citadel.Contract`:

```csharp
public sealed record RuntimeSession(long Generation);

public interface IRuntimeModuleLifecycle
{
    Task StartRuntimeAsync(
        Lifetime runtimeLifetime,
        RuntimeSession session,
        CancellationToken cancellationToken);

    Task StopRuntimeAsync(RuntimeSession session, CancellationToken cancellationToken);

    void ForceStopRuntime(RuntimeSession session);
}
```

Contract rules:

1. The shell calls `StartRuntimeAsync` once for each successful Start, before
   `CreateView`.
2. The supplied `runtimeLifetime` owns all runtime resources for that start.
3. `StopRuntimeAsync` must return successfully only when it is safe to destroy
   that lifetime. Throwing leaves the lifetime alive.
4. `CreateView` still creates the presentation root and may register cleanup on
   the same runtime lifetime.
5. `RuntimeSession.Generation` is allocated for each Start attempt and is never
   reused, including after a failed Start. It is distinct from the Shell
   operation epoch: the latter rejects stale Shell continuations, while the
   session generation fences a citizen's own late worker continuations.
6. `ForceStopRuntime` must synchronously signal every owned process, worker,
   and transport to abort. It must not wait for normal queue draining or mutate
   WPF controls. It is called only after the operator confirms Force Stop.
   `StopRuntimeAsync` must observe its cancellation token, and both Stop paths
   must tolerate a force-abort arriving after normal Stop has begun.
7. Citizens without this contract use the default lifecycle: create view on
   Start; destroy runtime lifetime on Stop or Force Stop.
8. `IResidentModule` remains a one-release compatibility bridge: it is invoked
   only by the runtime coordinator at Start with the slot runtime lifetime,
   never by `ModuleGate` during registration. MangaReader migrates to the new
   contract in this change.

The bridge keeps an older dynamically discovered resident citizen functional
while eliminating eager startup. Its documentation and tests must be updated so
it no longer falsely promises application-lifetime attachment.

## Runtime state machine

```text
Stopped --Start--> Starting --success--> Running
                         --failure--> Faulted

Faulted --Retry Start--> Starting

Running --Stop--> Stopping --success--> Stopped
                         --failure--> Running (with stop error)

Stopping --Force Stop--> ForceStopping --dispose--> Stopped
                                      --failure--> Running (with force-stop error)
```

Additional rules:

- One short-lived state gate exists per route. It serializes state transitions
  for Start, Stop, actual unregister, and application exit, but is never held
  across an awaited citizen operation.
- A normal Start/Stop attempt owns an epoch and cancellation source. Force Stop
  is allowed to preempt a stuck normal Stop: it acquires the state gate, cancels
  that source, increments the epoch, then proceeds without waiting for the old
  task. Stale completions are ignored by epoch comparison.
- Start/Retry is disabled in `Starting` and `Stopping`.
- Stop is disabled in `Stopped`, `Faulted`, and `Starting`.
- A Stop requested by shutdown while Start is in progress is recorded as
  `PendingStop`; Start completes or fails first, then the queued Stop runs.
- Force Stop is available only while `Stopping` or after a visible Stop error;
  it requires a confirmation dialog and is never triggered by a timeout.
- No interaction is silently ignored: the host visibly exposes `Starting`,
  `Stopping`, or the failure message.

## Thread-affinity rule

This is mandatory because citizen views can contain WPF and WebView2 controls.

- `CreateView`, `LayoutApplier.Attach`, parent checks, view mount/unmount,
  `IRetainedViewModule` callbacks, header construction, and `Lifetime.Destroy`
  run only on the Shell UI thread.
- `StartRuntimeAsync` and `StopRuntimeAsync` are entered from the Shell UI
  thread. A citizen may perform background work internally, but may not mutate
  WPF controls after an off-thread continuation.
- After awaiting background pause/drain work, the coordinator marshals back to
  the UI dispatcher before mutating slot state or visual ownership.
- The coordinator must not use `ConfigureAwait(false)` across code that resumes
  into WPF/WebView2 work.

## Shell runtime architecture

Create:

```text
core/Citadel.Shell/Runtime/
  ModuleRuntimeCoordinator.cs
  ModuleRuntimeSlot.cs
  ModuleRuntimeHost.xaml
  ModuleRuntimeHost.xaml.cs
```

`ModuleRuntimeSlot`, keyed by exact route, holds:

```text
ModuleDescriptor
Runtime state
Runtime Lifetime (only while Starting/Running/Stopping/ForceStopping)
Runtime FrameworkElement (only after Start succeeds)
Last start/stop error
PendingStop flag
RuntimeSession generation, operation epoch, and active Stop cancellation source
Per-route short-lived state gate
```

`ModuleRuntimeCoordinator.StartAsync(route)` performs this transaction:

1. Resolve a still-registered descriptor; reject an unregistering route.
2. Mark the slot `Starting` and notify its host.
3. Allocate a fresh runtime lifetime.
4. Allocate a new `RuntimeSession` generation and invoke
   `IRuntimeModuleLifecycle.StartRuntimeAsync`, or legacy
   `IResidentModule.AttachApplicationLifetime` when applicable.
5. Call `descriptor.Instance.CreateView(runtimeLifetime)`.
6. Reject `null` and already-parented views.
7. Apply `LayoutApplier.Attach` to the actual runtime view using the descriptor
   layout and the same runtime lifetime.
8. Store the view/lifetime, mark `Running`, and mount the view if its route host
   is visible.
9. On any failure, unmount any candidate, destroy the new runtime lifetime,
   retain the descriptor, mark `Faulted`, and expose Retry. Do **not** call
   `ModuleGate.RejectForFailedView`.

`ModuleRuntimeCoordinator.StopAsync(route)` performs this transaction:

1. Mark `Stopping`, disable runtime interaction, and notify the host/header.
2. If implemented, await `IRuntimeModuleLifecycle.StopRuntimeAsync`.
3. If it succeeds, call the shared `UnmountRuntimeView` operation, destroy the runtime lifetime,
   clear the slot, and mark `Stopped`.
4. If it throws or cancellation is not safe, retain the view and lifetime,
   return the slot to `Running`, and record the stop error. Nothing is killed.

The coordinator owns only slot orchestration and visual handoff. It does not
inspect concrete module types or queue states.

`ModuleRuntimeCoordinator.ForceStopAsync(route)` is a separate transaction:

1. Require the route to be `Stopping` or to have a recorded Stop error, and
   require prior confirmation from the operator.
2. Under the short-lived state gate, increment the slot operation epoch and
   cancel the normal Stop attempt; then release the gate. Every normal
   Start/Stop continuation checks that its captured epoch is still current
   before changing slot state or touching visual ownership.
3. Mark `ForceStopping`, disable the host, and call
   `IRuntimeModuleLifecycle.ForceStopRuntime` when implemented. If that abort
   signal itself throws, restore `Running` with a force-stop error and retain
   the runtime; do not continue to destruction.
4. Immediately call the shared `UnmountRuntimeView` operation and destroy its
   runtime lifetime; do
   not await queue drain, scheduler settlement, or provider requests.
5. Clear the slot and mark `Stopped`. `Lifetime.Destroy` isolates individual
   cleanup exceptions and continues its LIFO unwind; those exceptions are
   logged but cannot leave the slot falsely marked as a healthy running runtime.

Force Stop is intentionally an operator-selected interruption. It trades a
clean checkpoint for immediate release of the module runtime; the next Start
uses each module's existing crash/recovery semantics.

`ForceStopRuntime` is an abort signal only: it does not separately dispose
browser, proxy, HTTP, or queue objects. The runtime lifetime remains the single
owner of disposal. Before the coordinator destroys it, the citizen must put its
runtime into a **forced terminal mode** in which every registered cleanup is
non-blocking. A force path may cancel workers and close owned handles/processes,
but it must never wait for a scheduler settlement task. This is essential: the
current MangaReader queue disposal synchronously waits for scheduler shutdown,
so leaving that behavior reachable from Force Stop would make the button only a
renamed normal Stop.

## Router and visual lifetime

Citizen navigation changes from direct citizen view creation to a stable Shell
host:

```text
Router -> ModuleRuntimeHost(route) -> ModuleRuntimeCoordinator -> runtime view
```

`ModuleRuntimeHost` implements `IRetainedViewModule` and has two distinct
lifetimes:

| Object | Navigation away | Explicit Stop | Actual unregister / application exit |
|---|---|---|---|
| Route host | retained | remains, shows stopped screen | destroy |
| Runtime view | unmount only | destroy | destroy |
| Runtime lifetime | remains alive | destroy only after safe Stop | destroy after safe release |
| Router transition layer | disposed | n/a | disposed |

Host behavior:

- The coordinator owns one UI-thread-only `UnmountRuntimeView(slot)` operation.
  It removes the actual view from its host and, only when the view is currently
  mounted, invokes `IRetainedViewModule.OnViewDetached()` first. Navigation,
  safe Stop, Force Stop, Start rollback, actual unregister, and host disposal
  use this one operation. Thus a view already detached by navigation is never
  sent a duplicate detach callback before its lifetime dies.
- On host `OnViewDetached`, invoke `UnmountRuntimeView(slot)`.
- On `OnViewAttached`, remount the same runtime view. If it implements the
  interface, invoke `OnViewAttached` after mounting. Track mount state so a
  callback pair occurs once for each real mount/detach pair.
- A stopped/faulted host never calls `CreateView` merely because it was opened.
- Re-navigating to the same active citizen route is idempotent: it does not
  recreate host, runtime view, or runtime lifetime. Built-in Settings preserves
  its existing rebuild behavior.

Router must stop treating a runtime Start failure as a broken citizen. It still
uses the current unregister/fallback behavior when a citizen genuinely vanishes
from the registry.

## Header and stopped UI

No shared primitive is added. `ModuleRuntimeHost` composes existing
`SettingViewport`, `SettingActionCard`, `SettingButton`, and `SettingCardStyle`.

Stopped host content:

```text
<Module title>
Runtime stopped
<short explanation that no module runtime is running>
[ Start <Module title> ]
```

Faulted host content displays the readable error plus Retry Start. Starting and
Stopping display the current operation and disable duplicate commands. During
Stopping, and after Stop fails, the host additionally offers **Force Stop**.
It opens the existing `SettingDialog` confirmation surface and states that
active work may be interrupted and require manual Resume/recovery afterward.

The host implements `IContentHeaderActionProvider`:

- While Running, it returns a horizontal composition of the runtime view's
  existing header action, if any, followed by `Stop <module title>`.
- While stopped/faulted, it returns no header action; Start/Retry remains in the
  host screen.
- `ModuleRuntimeCoordinator` publishes `RuntimePresentationChanged`; Router
  forwards it as `ContentHeaderActionInvalidated`; `MainWindow` calls its
  existing `RefreshContentHeaderAction` method.

Required automation names:

```text
Start <module title>
Stop <module title>
Retry start <module title>
Force stop <module title>
```

## MangaReader implementation

`MangaReaderModule` implements `IRuntimeModuleLifecycle`.

### Start

1. Construct `DownloaderBackgroundService` in `StartRuntimeAsync`.
2. Register its disposal on the supplied runtime lifetime.
3. `CreateView` borrows that active `DownloaderContext` exactly as it does now.
4. Persisted jobs restore as `Paused`; Start itself makes no provider request
   and does not resume work.

### Stop

Add an internal `PauseAndDrainAsync` owner method in
`DownloadQueueFeature`, exposed through `DownloaderBackgroundService` only.
It must:

1. Enter a queue quiescing mode that rejects new Start/Resume admission.
2. Atomically persist every runnable or in-flight record as `Pausing`/`Paused`.
3. Cancel both schedulers and await every active execution's settlement.
4. Run the existing `ParkIfPausing` completion path and persist the final
   `Paused` snapshot after all workers settle.
5. Preserve `Completed`, `Failed`, and decision-required states unchanged.
6. Exit successfully only when neither scheduler has active work.

`DownloaderBackgroundService.StopRuntimeAsync` calls this barrier. Only after
it succeeds may the coordinator destroy the runtime lifetime, which disposes in
the established order: online process, queue, browser/PyHost, HTTP transport,
cover client. A persistence failure leaves all resources alive and reports a
retryable Stop failure.

Start after Stop creates a fresh service and restores the durable queue as
Paused. It never creates duplicate jobs or resumes jobs automatically.

### MangaReader Force Stop

`MangaReaderModule.ForceStopRuntime` delegates to a new queue-owned emergency
abort method through `DownloaderBackgroundService`. It must:

1. Receive the active `RuntimeSession.Generation`, set the queue to a terminal
   quiescing generation, and make every normal pause/drain continuation compare
   its captured generation before it can start, publish, or persist state.
2. Best-effort persist every non-completed in-flight record as `Paused` with a
   warning that it was force-stopped. Persistence failure is logged and remains
   visible on recovery; it does not block the requested force operation.
3. Cancel scheduler tokens without waiting for settlement.
4. Immediately signal termination only to MangaReader-owned online/PyHost child
   processes and mark `DownloaderBackgroundService` as forced-terminal. Its
   lifetime-registered disposal must then use `DisposeAfterForceAbort`: cancel
   and close owned resources without calling `QueueScheduler.ShutdownAsync`,
   `Task.GetResult`, or any other settlement wait.
5. On the next Start, apply the existing queue load recovery: unfinished work
   is Paused and requires explicit Resume.

The queue's normal pause-drain continuation captures the supplied session
generation and must become a no-op after Force Stop invalidates it. It may log its stale result,
but cannot write a later queue state, remount a view, or revive an old runtime.

No Force Stop path may delete staging, publish partial archives, resume work,
or modify another module's proxy/runtime.

## Yuzvid and ordinary citizens

Yuzvid needs no Shell reference and no Yuzvid-specific coordinator branch.

- Its current browser/controller is created only when `CreateView` runs after
  Start.
- Navigation keeps its runtime lifetime and remounts its retained view.
- Explicit Stop destroys that lifetime, therefore invoking existing browser,
  popup-host, queue, local-proxy, and WebView2 cleanup.

Blank, FTF, Proxy, and CamoProf use default lifecycle unless they later need a
safe asynchronous stop barrier. Their runtime is simply their view lifetime.

## Actual module unregistration

Actual unregistration is distinct from user Stop. `ModuleGate.Unregister` must
become a two-stage Shell operation without changing the external `IModuleGate`
void API used by Watcher:

```text
Watcher calls Unregister(route)
  -> Gate posts BeginUnregisterOnMain(route), marks descriptor Unregistering,
     blocks new Start, and invokes its bound async release handler
  -> Coordinator ReleaseRouteAsync(route) uses the normal safe Stop path
  -> success: Gate removes descriptor and fires RegistryChanged
  -> failure: Gate retains the descriptor as RemovalPending and exposes Retry removal
```

`ModuleGate` receives an internal Shell-only
`BindRuntimeReleaseHandler(Func<string, Task<RuntimeReleaseResult>>)` during
application composition, before the Searcher watcher starts. `Unregister` stays
void: it posts the begin operation to the UI dispatcher, observes the returned
task, and routes every result/exception back through that dispatcher. It must
not use an unobserved fire-and-forget task.

When safe release fails after a source folder has disappeared, the retained host
shows the failure and **Retry removal**. That retries `ReleaseRouteAsync` using
the in-memory descriptor; it does not attempt Start and does not need the source
folder to exist. Successful retry completes removal. A rediscovered descriptor
for the same route is retained as the one latest pending replacement; after old
runtime release, Gate atomically registers that replacement instead of relying
on another file-system event. If no replacement exists, it removes the route.

While a route is unregistering, a rediscovery of the same route is deferred;
after successful removal, the next watcher reconciliation registers the newest
descriptor. This avoids a duplicate-route race while old runtime resources are
still alive.

Only successful actual unregistration removes a sidebar item. User Stop never
does.

## Application exit

Replace the tray shutdown callback with `Func<Task> RequestShutdownAsync`.

For user-triggered tray Exit:

1. Mark exit as in-progress and block duplicate Exit/Open commands.
2. Await `ModuleRuntimeCoordinator.StopAllAsync()`.
3. On full success, invoke `Application.Shutdown()`.
4. On failure, cancel exit, retain all unsafe runtime slots, and show the error.

Force Stop is deliberately not used by tray Exit. Exit preserves the safe Stop
contract; an operator who needs emergency interruption must choose Force Stop
for the affected module first.

`OnExit` is defensive cleanup only; normally all slots are already stopped.
For Windows session ending or forced termination, there is no false guarantee of
async drain. MangaReader's existing startup recovery parks formerly in-flight
jobs as Paused on the next Start.

## File plan

| File / area | Change |
|---|---|
| `core/Citadel.Contract/IRuntimeModuleLifecycle.cs` | New optional lifecycle contract and `RuntimeSession` generation |
| `core/Citadel.Contract/IResidentModule.cs` | Clarify legacy runtime-start bridge semantics |
| `core/Citadel.Shell/Runtime/*` | New generic slots, coordinator, host UI |
| `core/Citadel.Shell/ModuleGate.cs` | Remove eager resident attach; bound and observed staged actual unregister/retry |
| `core/Citadel.Shell/Router.cs` | Citizen runtime host routing, retained host lifecycle, invalidation event |
| `core/Citadel.Shell/MainWindow.xaml.cs` | Refresh header on runtime state changes |
| `core/Citadel.Shell/App.xaml.cs` | Construct coordinator; async graceful shutdown |
| `core/Citadel.Shell/ResidentShell.cs` | Async Exit request and duplicate-exit guard |
| `module/mangareader/MangaReaderModule.cs` | Implement lifecycle; no eager resident runtime |
| `module/mangareader/Features/Downloader/DownloaderBackgroundService.cs` | Delegate safe queue stop barrier; forced-terminal non-blocking disposal branch |
| `module/mangareader/Features/Downloader/Queue/DownloadQueueFeature.cs` | Quiesce, pause, drain, session-generation fence, and forced terminal abort |
| `tests/Citadel.Uia/*` | Gate, Router, host, lifecycle, unregister, exit tests |
| `tests/Module.Mangareader.Downloader.Tests/*` | Pause/drain persistence and no-auto-resume tests |

## Implementation order and gates

1. **Contracts and state tests** — add lifecycle contract, slots, and tests
   proving registration does not attach a resident runtime.
2. **Coordinator and host** — add stopped/faulted/running host, UI-thread guard,
   and Start failure retention tests.
3. **Router integration** — route through host; retain on navigation; preserve
   same-route idempotence; preserve actual unregister fallback.
4. **MangaReader stop barrier** — implement queue quiesce/pause/drain and
   persistence-failure behavior, then implement its session-generation fence,
   forced-terminal non-blocking disposal branch, and recovery marker.
5. **Header and exit** — compose existing module action with Stop; add graceful
   async tray exit.
6. **Unregister sequencing** — add staged removal and route rediscovery guard.
7. **Targeted validation** — run Shell/UIA tests, Manga queue tests, then a
   Release build and live manual smoke checks.

Each gate must pass before the next phase; no provider/live-download behavior is
changed by this work.

## Acceptance criteria

- Registering a citizen never constructs its costly runtime.
- A stopped citizen remains visible in the sidebar and opens a lightweight host.
- Start creates exactly one runtime per route and Start failure keeps the route.
- Navigation away/back preserves a running runtime without duplication.
- Stop destroys a runtime only after its module confirms safe preparation.
- MangaReader Stop leaves no active scheduler execution and all resumable work
  durably Paused.
- A failed MangaReader Stop leaves runtime resources alive and retryable.
- Force Stop is opt-in, confirms interruption, prevents stale scheduler
  continuations from modifying a later runtime, and leaves unfinished work for
  explicit recovery/Resume.
- Force Stop preempts a deliberately non-settling normal Stop; it must reach
  Stopped without waiting on the original Stop task, and that stale task cannot
  overwrite the forced state when it eventually completes.
- Force Stop never reaches `QueueScheduler.ShutdownAsync`, synchronous task
  waiting, or another scheduler-settlement wait through lifetime disposal.
- A failed source-folder removal remains retryable from its retained route; a
  deferred rediscovery becomes the newest descriptor after old runtime release.
- Yuzvid WebView2 survives ordinary navigation and is disposed on explicit Stop.
- Actual module removal waits for safe runtime release before sidebar removal.
- Tray Exit waits for safe module Stop; failure cancels user-triggered exit.
- No citizen references Shell, no new shared primitive is created, and no
  route/sidebar behavior changes merely because a module is stopped.
