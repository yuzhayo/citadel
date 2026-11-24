# Citadel Current State

Citadel is a modular Windows desktop dashboard built with C#/.NET 10 and WPF.
It is tray-resident, supports self-contained Windows releases, and discovers
drop-in screens from `module/`.

## Working structure

- `core/` — application runtime, shell, navigation, tokens, shared contract,
  screen discovery (`Citadel.Searcher`), and the citizen build contract
  (`Citizen.targets`).
- `setting/` — built-in settings screens and reusable UI components.
- `module/` — independently deployable citizen screens plus the shared payload they
  compile in (`sharedLogic/`).
- `tests/` — Core, UI, and UIA coverage.
- `tools/` and `.github/workflows/` — packaging, CI, and release automation.

The public citizen contract remains the four types in `core/Citadel.Contract`.
Core libraries do not discover or reference citizen screens; the Shell app is
the composition root that connects `Citadel.Searcher` to the module gate.

## Expand the app

Read `module/README.md`, copy `module/blank/`, rename its manifest/project, and
implement the view inside the new screen folder. New screen-specific code stays
in that folder. Reusable controls belong in `setting/Components/`.

## Commands

```powershell
dotnet test Citadel.slnx
dotnet build module/blank/Module.Blank.csproj
.\tools\Build-Release.ps1
```

See [operations/release.md](operations/release.md) for installer, GitHub release,
in-app update details, and [operations/handoff.md](operations/handoff.md) for the
current operational baseline.

## Documentation map

- `architecture/` — as-built structure, dependency flows, and inventory.
- `contracts/` — current cross-module behavior contracts.
- `governance/` — boundary review, remediation, and restructuring records.
- `operations/` — release and smoke procedures.
- `work/` — active, scoped work plans and checklists.
- `history/` — dated plans, audits, research, reports, and imported harness notes.

### Filename aliases used inside the documents

Documents in `architecture/` and `governance/` were written before this tree was
flattened, so they cite their siblings by uppercase legacy names. The names are
not paths — this table is the translation. When you see a `CITADEL-*.md`
reference, look it up here.

| Cited name | Actual file |
| --- | --- |
| `CITADEL-STRUCTURE.md` | `architecture/structure.md` |
| `CITADEL-INVENTORY.md` | `architecture/inventory.md` |
| `CITADEL-KERNEL-MAP.md` | `architecture/kernel-map.md` |
| `CITADEL-FLOWS-CROSS.md` | `architecture/flows-cross.md` |
| `CITADEL-FLOWS-CORE.md` | `architecture/flows-core.md` |
| `CITADEL-FLOWS-SETTING.md` | `architecture/flows-setting.md` |
| `CITADEL-FLOWS-SHAREDLOGIC.md` | `architecture/flows-sharedlogic.md` |
| `CITADEL-FLOWS-MANGAREADER.md` | `architecture/flows-mangareader.md` |
| `CITADEL-FLOWS-CITIZENS.md` | `architecture/flows-citizens.md` |
| `CITADEL-VIOLATIONS.md` | `governance/violations.md` |
| `CITADEL-TASK-BRIEF.md` | `governance/task-brief.md` |
| `CITADEL-RESTRUCTURE-PLAN.md` | `governance/restructure-plan.md` |
| `SMOKE-CHECKLIST.md` | `operations/smoke-checklist.md` |
| `REPORT-catalog-mirror-handoff-2026-09-08.md` | `history/2026/reports/catalog-mirror-handoff.md` |

Historical material is retained for traceability only. Verify current behavior
from source and the architecture/contract documents before relying on it.
