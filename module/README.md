# module/ — the playbook

Everything a screen author needs. The searcher's rules **are** the spec, so this
file states them once and the code follows it.

**Making a screen is: copy `blank/`, rename the folder, change `module.json`,
fill in the view.** Nothing outside `module/` is edited, ever.

---

## Three different things called "module"

Confusing these is the most expensive mistake available here, so they are named
separately throughout:

| | Where | What it is |
|---|---|---|
| **source** | `module/blank/` | the files you edit and copy |
| **build output** | `module/blank/bin/<Config>/net10.0-windows/` | what the compiler produced |
| **deployment** | `core/Citadel.Shell/bin/<Config>/net10.0-windows/module/blank/` | **what Citadel actually watches** |

The app watches `AppContext.BaseDirectory\module` — beside the executable. It
never looks at this source tree. `Citizen.targets` deploys for you, so in normal
use you only think about the first column.

Release packaging follows the same rule. `tools/Build-Release.ps1` discovers
every immediate `module/<screen>/Module.*.csproj`, builds it directly, and points
`Citizen.targets` at the installer staging tree. Adding a screen therefore does
not require a solution, workflow, or release-script edit.

Watching the source tree instead would appear to work in development and never
work once installed.

---

## Folder shape

```
module/
  README.md            this file
  Citizen.targets      shared build logic — every screen imports it
  Citadel.Searcher/    finds screens and tells the gate
  blank/               the minimal living screen — copy this
    module.json          identity: title/icon/route/order/entry/type
    layout.json          editable slots
    BlankModule.cs       the IModule implementation — the only face
    BlankView.xaml(.cs)  the screen
    Module.Blank.csproj  imports ../Citizen.targets and declares nothing else
```

`Citadel.Screen/` is **reserved and stays empty**. Git cannot
track an empty directory, so the folder is not created; this paragraph is the
reservation. Its purpose is explicit: chrome
displayed *across* screens — a shared header carrying a button, a clock — would
live there. Extracting a shared base from one example is guessing at what
repeats, so it stays empty until there is a second real screen to compare
against.
---

## Making a new screen

```
cp -r module/blank module/reports
cd module/reports
mv Module.Blank.csproj Module.Reports.csproj
```

Then edit, **only inside this folder**:

1. `module.json` — `title`, `route`, `entry` (`Module.Reports.dll`), `type`
2. `BlankModule.cs` → rename the class, and `Route` must equal the manifest route
3. `BlankView.xaml(.cs)` → your screen
4. `layout.json` — the slots you want editable from Settings

```
dotnet build module/reports/Module.Reports.csproj
```

That is all. The screen appears in the running app with nothing pressed.

**Do not add the project to `Citadel.slnx`.** Building it directly is what makes
"a new screen changes nothing outside `module/`" true, and `dotnet build
Citadel.slnx` deliberately does not build screens.

### The namespace and assembly name must be yours

`Citizen.targets` derives `AssemblyName` and `RootNamespace` from the folder
name, so `module/reports` produces `Module.Reports`. **Rename the C# namespace
inside your copied files to match**, because WPF compiles a resource URI like
`/Module.Reports;component/reportsview.xaml` into `InitializeComponent` and
resolves it at runtime through a lookup keyed by *simple assembly name, across
every load context*.

Two screens shipping assemblies with the same simple name therefore fight over
one resource key, and whichever loses throws `does not have a resource
identified by the URI` from its own view. Deriving the name from the folder makes
that impossible by default; overriding `AssemblyName` makes uniqueness your
problem again.

---

## `module.json` — identity, validated strictly

```json
{
  "title": "Reports",
  "icon": "",
  "route": "reports",
  "order": 10,
  "entry": "Module.Reports.dll",
  "type": "Module.Reports.ReportsModule"
}
```

| Field | Required | Notes |
|---|---|---|
| `title` | yes | sidebar label and content header |
| `route` | yes | navigation name; must equal `IModule.Route` |
| `entry` | yes | assembly filename, relative to this folder |
| `type` | yes | full type name implementing `IModule` |
| `icon` | no | Segoe MDL2 glyph, or omit |
| `order` | no | sidebar sort key; defaults to 0 |

Comments and trailing commas are allowed. Property names are matched
case-insensitively.

**A malformed `module.json` fails the folder, loudly and visibly.** It is
identity: there is nothing to fall back to. Refused outright:

- a missing or blank `title`/`route`/`entry`/`type`
- a route that is `settings`, `settings/appearance`, `settings/layout`, or
  `settings/gallery` — those are core's, and a screen claiming one would silently
  shadow navigation
- a malformed route (empty segments, spaces, backslashes, leading/trailing `/`)
- an absolute `entry`, or one escaping the folder with `..` — one screen must not
  load another's assembly
- a `type` that is absent, does not implement `IModule`, or throws when constructed
- a `type` whose `Route` disagrees with the manifest — otherwise the sidebar entry
  and the router disagree and navigation lands nowhere

**A folder with no `module.json` is not a citizen** and is skipped silently. That
is deliberately different from a citizen whose manifest is broken.

---

## `layout.json` — presentation, fail-soft

```json
{
  "slots": {
    "HeartbeatText": { "kind": "position",   "x": 16, "y": 8 },
    "PoolList":      { "kind": "size",       "w": 640, "h": 380 },
    "StatusPill":    { "kind": "visibility", "visible": true }
  }
}
```

Slot names are `x:Name` values in your XAML. Three kinds only — `position`,
`size`, `visibility`. Anything richer would turn this into a UI description
language.

The shell applies these *after* `CreateView`, so a screen reads no tokens and no
layout itself. Users edit them from **Settings → Module layout**, and their edits
persist sparsely in `%AppData%\Citadel\ui.json` under your route — never written
back into your folder, so updating a screen does not discard user edits and a
read-only install still works.

**A malformed `layout.json` does not fail the screen.** Identity is still valid,
so it registers with no editable slots and the reason is listed in Settings. A
missing `layout.json` is normal.

---

## What a screen may reference

`Citadel.Core`, `Citadel.Contract`, `Citadel.Setting`. Never `Citadel.Ui`, never
`Citadel.Shell`. `Citizen.targets` declares the three for you with
`Private=false`.

### The identity caveat

A screen folder **must not contain** `Citadel.Core.dll`, `Citadel.Contract.dll`,
`Citadel.Setting.dll`, `Citadel.Ui.dll`, or `Citadel.Shell.dll`.

A private copy of `Citadel.Contract` defines a *second* `IModule` type, the cast
in the loader fails, and the error blames your screen rather than the deployment.
`Private=false` is the convention; `Citizen.targets` also **fails the build** if
one of those files is found in the deployed folder, because a convention is not
an enforcement.

### Private dependencies

Reference whatever else you need. `Citizen.targets` sets
`EnableDynamicLoading=true`, which emits a `.deps.json` beside your DLL and
copies your private dependencies locally; the loader reads that file through
`AssemblyDependencyResolver` to find them.

Shared Citadel assemblies always come from the app's default context, so one
`IModule` identity holds across the process no matter what a screen ships.

---

## How loading works, and what it means for you

**Each screen gets its own collectible load context.** A broken screen cannot
take the shell down, and two screens cannot see each other's types.

**Assemblies are loaded from memory, not from the path.** `LoadFromAssemblyPath`
keeps an OS file handle open for the life of the context, which would make your
deployed folder undeletable while Citadel runs — not by the app, and not by you
in Explorer. Reading the bytes first keeps the folder deletable, which is the
whole point of "delete the folder and the screen disappears".

The cost: `Assembly.Location` is empty for your assembly. Do not derive paths
from it.

**Contexts are retained until the app exits, never unloaded on delete.** So:

| You do | What happens |
|---|---|
| add a folder | screen appears, live, nothing pressed |
| delete a folder | screen disappears immediately; the context unloads at exit |
| **change a DLL** | **picked up on the next launch, not live** |

That last row is a real limitation, not an oversight. Unloading a context while a
destroyed-but-not-collected view still references its types produces a
partially-unloaded context and a failure that surfaces much later. Live
replacement is deferred.

**A half-copied folder is retried, then settles.** Copying takes time and the
watcher fires while files are still arriving, so the searcher retries a few times
with short backoff, logging each attempt, then records a visible failure. Finish
the copy and the same path picks it up.

**Nothing polls.** Discovery is `FileSystemWatcher` plus one serialized pump that
blocks when idle. `Settings → Update modules` re-raises the same scan by hand,
for cases a watcher genuinely misses — network paths, denied notifications.

---

## When something is wrong

Two places, always:

- **Settings → PROBLEMS** — every failure, keyed by folder, with the stage that
  failed. It clears when you fix or delete the folder.
- **`%AppData%\Citadel\log.txt`** — the `Modules` sink, with retry attempts.

A broken screen is isolated: the rest of the app keeps working, and a healthy
screen beside it still registers.

---

## The four folder laws

The runtime boundary is:

1. **A screen folder is self-contained.** Delete it and only that screen goes.
2. **Components come from `setting/`**; *how* they behave here is declared here.
3. **No component contains screen-specific logic.**
4. **Shared-by-all-screens code goes to `module/` root**, never into a sibling
   screen's folder.

Law 4 is why `Citizen.targets` sits here rather than inside `blank/`: it is shared
by every screen and owned by none. Law 1 is why that matters — a shared build file
inside one screen's folder would make deleting that folder break the others.
