# Queue independent transfer, scheduler, and grouped controls

Status: implemented and released in 2.2.13; live provider transfer remains unverified.
Baseline inspected: working tree 2.2.12, 2026-09-12.
Revision: Queue owns Comix manifest sessions as well as independent page transfer.
This is a scoped follow-up to PLAN-mangareader-downloader.md. Existing dirty and
untracked files are part of the inspected baseline, not new files of this plan.

## 1. Actual baseline and corrections

- DownloadQueueFeature.JobConcurrency is 1. Its scheduler waits for WhenAny of
  active workers; increasing the constant alone cannot implement manual starts
  while all workers are busy. It needs a command wake-up as well as completion.
- ChapterDownloadPipeline uses 8 page workers in proxy mode and 2 in direct mode.
  Native page requests have a full-body 30-second deadline. Fixed retries and a
  second recovery pass can call browser fallback once per pass, not once per page
  across the entire run. The previous completion report overstated this guarantee.
- ProxyPoolAdapter is round-robin plus quarantine. ProxyLease is an endpoint
  record, without owner, release, or busy tracking. HTTP/browser have independent
  cursors, so exclusivity is not currently guaranteed.
- DownloaderBackgroundService constructs one browser, source registry, proxy
  adapter, and transport. Queue borrows them. Runtime already outlives the view.
- ComixSource.GetManifestAsync goes through GetResultAsync and the prepared
  browser API. IQueueSourceReadiness currently parks Comix before the pipeline
  when that browser is unavailable. A native HTTP page session cannot supply
  this provider API prerequisite merely by selecting another proxy.
- DownloaderPyHostClient serializes session calls. Cancellation of the C# wait
  is not proof that an already-dispatched Python request stopped. Stop must wait
  for bounded completion before releasing ownership or deleting staging.
- Pipeline checkpoints every 8 successful pages, but has no final checkpoint on
  cancellation. The next plan flushes settled results on graceful Stop.
- DownloadListScreen is a flat SettingTable with stable JobIds, existing actions,
  and UiPreference.Key=mangareader.queue.table. It has no row checkboxes/groups.
  SettingTable has interactive columns but no exposed grouping API.

## 2. Behavior contract

### Independent and shared session

Independent means Queue owns both its Comix browser/API session for manifests and
its HTTP transfer contexts for pages, with separate profiles, cookies, clients,
cancellation and proxy reservations from Downloader. Each active chapter has its
own execution context; independent browser hosts are isolated so manifest calls
do not all serialize through the existing shared Downloader host.
Shared means the existing prepared Downloader browser, borrowed through a narrow
QueueSharedSessionAdapter. Queue cannot open/reconfigure/close that browser.

Queue Start/Resume authorizes lazy initialization of its own Comix session through
an exclusively reserved proxy. Opening the app or Queue view creates no requests.
Reuse the existing Comix parser, browser plugin and API request logic with a
Queue-owned client; do not duplicate signing/parsing logic or borrow Downloader
cookies/profile. Independent profile paths are namespaced by a stable hash of Queue JobId
and cannot be opened by two hosts simultaneously. Proxy replacement settles and
closes the previous host before reusing its profile or releasing its reservation.

A new Comix job can fetch its manifest with Downloader fully inactive. Replace
Queue's unconditional shared-readiness precheck with readiness of the selected
independent execution context. Use a Queue-owned ComixSource instance injected
with that context's client; leave the Downloader source instance unchanged.
Manifest acquisition and recovery exhaust their compatible independent candidates
before borrowing shared. Shared absence alone must not prevent independent work.

Persist manifest snapshots with staging for resume, validated by chapter identity,
schema, source and hash. Recovery uses the same independent-first manifest path.
No manual challenge step is required in this workflow. Denial/challenge responses
remain typed failures rather than being mistaken for successful manifest content.
The user's currently working proxy access is not a guarantee of future responses.

The 3-second deadline applies to independent PAGE transfer. Browser bootstrap and
manifest/API operations retain the existing bounded operation timeouts initially
(120 seconds bootstrap, 45 seconds API), displayed as separate stages. No nested
browser failover loop may multiply the Queue-owned candidate traversal.

### Proxy ownership and retry

- Freeze a compatible candidate snapshot at each Start/Retry run. Empty,
  incompatible, busy, exhausted and provider-rejected are distinct results.
- Existing independent page workers are retained: up to 8 per chapter. Each
  in-flight page holds an exclusive proxy reservation until body completion or
  confirmed cancellation. Thus chapters never concurrently share an endpoint;
  a chapter may use several proxies. This resolves the earlier ambiguous claim
  of one fixed proxy per chapter while also retaining 8 different page proxies.
- Four automatic chapters can create up to 32 independent page requests. Manual
  chapter starts add requests beyond that; available exclusive leases still bound
  actual execution. Distribute lease grants fairly among active chapters.
- Track busy state by physical server identity (scheme/host/port), retaining full
  endpoint credentials privately. Do not expose credentials in status/logs.
- Keep each Queue manifest browser's selected proxy and the Downloader shared
  browser proxy reserved until its session actually closes. Close an idle Queue
  manifest host after manifest acquisition to release capacity for page workers;
  reopen lazily for recovery. Other users release HTTP reservations after
  body consumption. CamoProf/Catalog have separate existing adapters; cross-module
  global exclusivity is not claimed or added here.
- Each independent PAGE attempt gets 3 seconds for connect + headers + body.
  Time waiting for a free lease does not consume this deadline. Local decoding,
  checkpointing and publication are separate stages.
- Track attempted candidates per missing page per run; never repeat the same
  candidate indefinitely. A busy untried candidate is waited for, not counted as
  failed. Transport/auth proxy failures quarantine that endpoint for the run.
- Exhaustion means every compatible independent candidate has been tried or is
  already known transport-failed in this run. Reserved shared proxy is excluded.
  Pool updates do not extend an in-progress retry round indefinitely; a new
  explicit Retry captures a fresh snapshot and resets run-scoped quarantine.
- 404/410 and TooLarge remain terminal page outcomes, not a reason to exhaust the
  proxy pool. Provider denial/challenge must retain the existing safety behavior;
  do not classify it as a broken proxy or rotate indefinitely to bypass denial.
  Respect 429 cooldown and cancellation; do not quarantine a healthy proxy solely
  for a provider response. Status explains the blocked/cooldown condition.
- After independent candidates for the operation are exhausted, allow one shared
  fallback for that manifest operation, or that page across the entire run
  (including recovery). Preserve manifest hash,
  byte validation and page order. No direct connection fallback in proxy mode.
- If shared is absent/wrong provider/wrong route at exhaustion: latch Stop All,
  stop automatic and manual Queue work, cancel waits/retries, flush checkpoints,
  release leases after settlement and park all unfinished jobs. Do not auto-resume.
- Recheck shared readiness when the queued fallback actually receives its turn.
  If shared fails too, retain typed failure/staging; no restart of the proxy loop.

### Scheduler and actions

- Automatic admission: max 4 chapters, including group Resume/global Resume.
- Start/Retry on a chapter is manual admission, bypassing only that automatic
  chapter limit. It does not bypass duplicate-job protection, proxy ownership,
  readiness, cancellation, target validation or publication conflicts.
- Start/Retry must wake the scheduler immediately even when existing tasks are
  still pending. Manual jobs do not consume automatic slots. One JobId has at
  most one live execution generation; repeated clicks are idempotent.
- Stop(job/group/all) blocks further admission for the target, cancels active
  requests and waits for them to settle. Display Stopping until settlement, then
  Stopped (existing durable Paused state can be retained for compatibility).
- Group key: source id + stable title identity; translation/source GroupId remains
  a chapter attribute. Same display name is never an identity key.
- Start/Retry reuse valid staged pages; Retry resets only the failed request round.
  Completed jobs retain Open folder; ordinary Start does not overwrite a CBZ.
- RemoveMany: snapshot selected JobIds, stop/await affected jobs, commit removal
  once, then remove their staging. Existing completed CBZ files remain intact.
  One confirmation covers staging deletion; persistence failure leaves rows/data
  recoverable. Publication already committed before Stop must remain Completed.
- Stop All also cancels Queue's queued/shared fallback work without terminating
  Downloader. Already-dispatched shared requests use their existing bounded
  completion and remain Stopping until done; never claim immediate cancellation
  or remove their staging while Python can still write it.
- Stop(job/group/all) closes the targeted Queue-owned manifest hosts, cancels its
  independent HTTP requests and releases their reservations after settlement.
  Shared Downloader session remains owned by its own Stop button.
- UI navigation and close-to-tray preserve runtime. Exit tray disposes runtime.
  Application restart restores unfinished work stopped; no automatic network.

## 3. Files and boundaries

Paths below are relative to module/mangareader unless stated otherwise.

### Task A — proxy reservations and independent/shared transfer (logic)

| File | Status | Responsibility and boundary |
|---|---|---|
| shareLogic/ProxyLeaseRegistry.cs | New | Atomic reserve/release, busy wait, owner snapshot and shared reservation; no Queue scheduling or HTTP requests. |
| shareLogic/ProxyPoolAdapter.cs | Modify | Read existing published pool, candidate compatibility, run-scoped failure evidence; use registry instead of bare endpoint selection. Preserve existing callers. |
| shareLogic/ProxyHttpTransport.cs | Modify | Support caller-owned explicit lease and response-lifetime release; no hidden Queue retries or chapter rules. |
| Features/Downloader/Queue/QueueIndependentSession.cs | New | Per-chapter HTTP context, candidate traversal and 3-second page deadline; consumes registry and transport. No UI or shared browser ownership. |
| Features/Downloader/Queue/QueueManifestSession.cs | New | Queue-owned browser host/profile, ComixSource instance and independent-first manifest/recovery requests. Candidate traversal is bounded and owned here; no page downloading or scheduler admission. |
| Features/Downloader/Queue/QueueSharedSessionAdapter.cs | New | Readiness, serialized borrowing for manifest/page fallback and bounded settlement of existing Downloader session; no authority to Start/Stop/reconfigure it. |
| Features/Downloader/DownloaderPyHostClient.cs | Modify narrowly | Accept explicit runtime/profile identity and selected lease for Queue instances; register/release actual browser reservation; expose provider/route/session generation. Existing Downloader defaults and startup/API/shutdown behavior remain intact. |
| Features/Downloader/mangareader_downloader/browser.py | Modify narrowly if profile routing is hardcoded | Accept validated Queue profile identity through existing launch path; reuse the existing Comix browser/API implementation. Preserve default Downloader profile and request behavior. |
| Features/Downloader/Queue/PageTransport.cs | Modify | Native streaming/image validation with supplied independent context; shared fallback separate, issued only by pipeline policy. Preserve direct-mode behavior. |
| Features/Downloader/Queue/ChapterDownloadPipeline.cs | Modify | Use one candidate/fallback budget per page/run, checkpoint on Stop, retain recovery/completeness validation. Remove overlapping fixed retry loops for proxy mode. |
| Features/Downloader/Queue/QueueManifestStore.cs | New | Atomic per-job manifest snapshot in existing downloads/jobs/<JobId>/ directory; validate identity/hash/schema before reuse. No requests or credentials. |
| Features/Downloader/DownloaderBackgroundService.cs | Retain existing wiring | Existing resident owner already supplies browser, registry and transport. Queue constructs its narrow execution adapters from those dependencies; no additional background lifecycle changes needed. |

No new pool database. Published pool remains ProxyPoolContract.ActivePoolPath.
Usage reservations are runtime-only; queue.json and existing job.json remain the
durable queue/staging owners. Manifest snapshot is separate provider metadata.

### Task B — commands and scheduling (logic)

| File | Status | Responsibility and boundary |
|---|---|---|
| Features/Downloader/Queue/QueueScheduler.cs | New | Automatic/manual admission, command wake-up, fair scheduling, execution generations, cancellation and completion handles; no persistence or provider parsing. |
| Features/Downloader/Queue/DownloadQueueFeature.cs | Modify | Public Start/Retry/Stop/StopAll/ResumeGroup/StopGroup/RemoveMany commands, atomic state transitions, global exhaustion stop, scheduler integration. |
| Features/Downloader/Queue/DownloadQueueModels.cs | Modify | Typed action availability and display phase/reason/route; preserve existing persisted enum values and job identities. |
| Features/Downloader/Queue/DownloadQueueStore.cs | Modify if serialization needs it | Backward-compatible defaults and recovery for added persisted fields; never persist leases/tasks/manual active execution. |

Extract the existing scheduler into QueueScheduler and remove the superseded
scheduler path from DownloadQueueFeature. Do not run old and new schedulers together.
Avoid async Progress callbacks overwriting terminal states: apply updates in order,
guarded by active JobId/generation, and ignore settled execution updates.

### Task C — grouped table and controls (logic + UI)

| File | Status | Responsibility and boundary |
|---|---|---|
| Features/Downloader/Queue/QueueGroupProjection.cs | New | Stable group/chapter row models, selected JobIds, group tri-state checkbox, expand/collapse, group totals and presentation sort; no network or job lifetime. |
| Features/Downloader/Queue/DownloadListScreen.xaml | Modify | Compose existing SettingTable, SettingButton, SettingTableActions, SettingActionCard and shared checkbox resources. Top Stop All/Remove Selected; group Resume/Stop; chapter Start/Stop/Retry. |
| Features/Downloader/Queue/DownloadListScreen.xaml.cs | Modify | Forward UI commands, confirmation and dispatcher-bound projection updates. Preserve viewport and row identities during progress. |

Use one flattened visible collection: group row followed by its expanded chapter
rows, inside the existing SettingTable. Chevron is a shared button in the title
cell; expand/collapse changes only visible rows, never cancels work. No second
DataGrid, nested scroll viewer or screen-local table template.

Native flat column sorting would scatter group headers and children. Initially
disable it for this grouped presentation; preserve queue order within each group
and existing width preferences. Do not silently apply a saved flat sort. If sorting
is later required, the projection must sort within groups while retaining headers.

Keep expansion/selection in the resident Queue presentation owner through view
navigation. Persist expansion via existing UI preferences if needed across app
restart; selected rows remain transient. Checkbox groups include collapsed children.
The existing provider-group column is not the new title grouping hierarchy.

Example:

    [Stop All] [Remove Selected (3)] [Resume]           4 automatic + 1 manual
    [ ] v Title A / Comix      2 active, 8 stopped     [Resume] [Stop]
        [x] Chapter 1        Independent / proxy-id   [Start] [Stop] [Retry]
        [x] Chapter 2        Waiting for proxy       [Start] [Stop] [Retry]
    [-] > Title B / Comix     12 chapters             [Resume] [Stop]

Actions are enabled only in valid states. Proxy details show a safe identifier,
route, attempt count and owner; never user/password credentials. No extra proxy
management screen is needed for the requested checker adapter.

## 4. Verification and delivery

Implement A then B then C, with targeted behavioral checks:

1. Local HTTP fixture sends headers then stalls body: cancelled at 3 seconds;
   reservation released only after settlement; next free proxy tried. No live
   provider probing is needed to validate this timeout.
2. Concurrent reservations never give one proxy to two chapters or reuse the
   active shared browser proxy; busy is not exhausted; retry traverses a finite
   candidate snapshot once; no direct fallback.
3. At most 4 automatic jobs; fifth manual job starts promptly; double Start does
   not duplicate; a release wakes Waiting for proxy; Stop All prevents late starts.
4. Exhaustion + inactive shared parks every unfinished Queue job and preserves
   checkpoint; active shared used once per missing page/run; session disappears
   while waiting produces the same stop behavior without auto-bootstrap.
5. Stop/Resume and batch remove race against page completion, fallback completion
   and publication; no write-after-delete, no terminal-state overwrite.
6. Fresh Comix job without a snapshot and with Downloader inactive obtains its
   manifest through its independent host. Verify isolated profiles/proxies,
   manifest candidate exhaustion and shared fallback, recovery and snapshot hash
   validation. Stop closes only the targeted independent hosts; opening Queue
   never starts them. No mandatory manual challenge gate.
7. Group checkbox selects hidden children; collapsed groups retain progress;
   progress does not reset scrolling/expansion/selection. Native visual smoke at
   normal and narrow window sizes with existing shared components.

Extend existing DownloadResumeTests, DownloadQueuePersistenceTests,
DownloadListActionValidityTests, ProxyPoolAdapterTests and ProxyHttpTransportTests;
add QueueSchedulerTests, QueueManifestSessionTests and QueueGroupProjectionTests
for the new owners. Extend existing browser transport tests for profile isolation
and preservation of Downloader defaults. Update
the Downloader test csproj only where explicit source inclusions require it, and
the existing LevelBGuardTests for new boundaries. No full unrelated solution suite.

Runtime implementation bumps version.props from actual current version (currently
2.2.12). Before Release build, stop the running local instance cleanly so Queue
flushes checkpoints. Build/deploy to artifacts/smoke/publish and run. Record actual
build, focused-test and visual results separately. Commit/push/GitHub release are
not part of this implementation request. No code or version changed when originally writing this plan.

## 5. Implementation handoff — 2026-09-12

- Task A implemented: exclusive runtime proxy reservations, isolated Queue Comix
  manifest client/profile, validated manifest snapshots, 3-second full-body page
  attempts, finite candidate traversal, existing-session-only shared fallback.
  Provider rejection is terminal, not a reason to rotate around a denial.
- Task B implemented: four automatic chapter slots, explicit manual Start/Retry
  admission, command wake-up, scoped Stop/Stop All and settled batch removal.
  Page parallelism remains eight per proxy-mode chapter, bounded by free leases.
- Task C implemented with the existing SettingTable and shared action components:
  expandable title groups, hidden-child selection, group Resume/Stop and chapter
  actions. Progress updates preserve row identity. Column widths remain user
  preferences; flat column sorting is disabled to keep groups together.
- Existing resident background owner and Downloader default profile are retained.
  Queue profile identity is stable per job, rather than a reusable scheduler slot;
  the scheduler prevents simultaneous execution of the same job.
- Release version is 2.2.13. Running instances were exited through tray Exit
  before deployment to artifacts/smoke/publish (not a Debug deployment).
- Native UI checked at 1180x900: title expansion/collapse, selection of all 45
  children, retained selection while collapsed, and deselection. No user queue
  entries were removed or started. Existing wide column preferences can require
  horizontal scrolling; this task does not reset them.
- Focused automated coverage includes timeout through a local stalled-body HTTP
  fixture, exclusive leases, finite fallback, provider rejection, scheduler
  admission, cancellation/removal settlement, snapshot integrity, group selection,
  and Python profile isolation. Manifest tests use the actual Comix parser with a
  fixture transport; they do not demonstrate current live Comix availability.
- Not verified here: real provider download, real proxy-pool performance, or a
  live shared-browser timeout. No provider requests were sent for verification.
  Already-dispatched shared work must settle before staging removal; Stop may
  remain Stopping while the shared host finishes its operation.
- Follow-up release completed: commit `81e9e30`, tag `v2.2.13`, installer and
  GitHub release published after the implementation handoff.
