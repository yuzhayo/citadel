# Citadel handoff

Snapshot: 2026-09-12, after release `v2.3.0`.

## Verified release state

- Active checkout: `C:\VSCODE\citadel`.
- Branch: `main`; snapshot commit: `ced5501` (`chore: bump version to 2.3.0`).
- Latest published release: [`v2.3.0`](https://github.com/yuzhayo/citadel/releases/tag/v2.3.0), targeting `ced5501`.
- Published assets: installer, portable ZIP, full package, delta package, and
  Velopack release indexes.
- The GitHub CI and release workflows completed successfully for that commit.
- Local working version moves independently after this snapshot; check
  `version.props` and `git status` before treating this release as current.

## Repository shape

`docs/` is the only versioned documentation tree:

- `architecture/` — as-built structure, inventory, dependency flows.
- `contracts/` — current behavior contracts shared across screens.
- `governance/` — architecture review, violations, remediation records.
- `operations/` — release, smoke, and this handoff.
- `work/` — active scoped work; currently the CamoProf Add Profile plan/todo.
- `history/2026/` — dated plans, research, audits, reports, and imported Hermes
  material. Treat this as evidence/history, not an authority over source.

There is no tracked `.docs/`, `tasks/`, or `.hermes/plans/` directory anymore.
The root tool directories remain intentional:

- `.agents/` — two Citadel policy skills and two boundary hooks.
- `.config/` — `dotnet` local-tool manifest for Velopack.
- `.github/workflows/` — CI and release automation; GitHub requires this path.
- `.vscode/` — workspace editor settings.

## Application model

- Citadel is a .NET 10/WPF, tray-resident shell. Module navigation and closing
  the main window must not dispose resident work.
- Only **tray Exit** terminates the application-wide resident runtime. A module
  Stop button only stops that module's owned operation; it must not stop another
  module or the shell.
- `core/` owns shell/runtime/contracts; `setting/` owns reusable UI components;
  `module/` owns independently deployable citizen screens; `tests/` owns test
  projects. Citizens may not reference the Shell, UI assembly, or another citizen.

## Delivered functional baseline

- MangaReader has Library, History, Cover Builder, Downloader, Catalog, and Queue.
  Its Downloader Queue now has independent proxy-aware sessions, grouped rows,
  bounded automatic admission, explicit manual actions, checkpoint-aware Stop,
  and shared-session fallback only after independent transport exhaustion.
- Proxy is now a functional citizen with Pool, Sync, and Settings tabs. Its
  published pool is consumed through adapters by MangaReader and CamoProf.
- CamoProf and MangaReader use their own ownership lanes; no cross-module global
  proxy exclusivity claim is made.
- Existing Downloader provider behavior is preserved as the baseline. Do not
  remove explicit user-triggered Start/Stop safety boundaries or turn a view open,
  filter edit, or tab navigation into a provider request.

## Known limits and verification boundary

- The Queue implementation has fixture/targeted coverage and release-build
  evidence. It deliberately does not claim a current live Comix download or live
  proxy-pool performance result.
- Provider access, blocks, terms, and source markup are time-dependent. Recheck
  live access and authorization before provider work. Do not bypass challenges,
  blocks, paywalls, or access controls.
- Documentation under `history/` can describe superseded plans. Confirm actual
  behavior in source and the architecture/contract documents.
- No credentials, browser profiles, pool runtime state, or generated release
  artifacts are versioned. Keep it that way.

## Next-agent checklist

1. Read `AGENTS.md`, then the relevant maintained Yuzskill and local Citadel
   policy skill before editing.
2. Inspect `git status`, `version.props`, current release/tag, and the target
   feature owner before acting. Preserve unrelated worktree changes.
3. Reuse `setting/Components` for shared UI; do not create duplicate primitives.
4. Keep feature logic inside its feature owner and parent modules composition-only.
5. For any change, bump `version.props` as required by the operator. Before a
   local Release build, close the running Citadel instance through tray Exit.
6. Build/test only the scope needed to prove the change. Commit, push, installer,
   and GitHub release require explicit operator instruction.

