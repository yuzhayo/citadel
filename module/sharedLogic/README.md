# sharedLogic — generic python + shared C# source, owned by no screen

Capabilities shared by every citizen live here. This folder is NOT a
citizen: it carries no `module.json`, so the searcher skips it silently
(Folder Law 4 — shared-by-all-screens code goes to `module/` root).

```
sharedLogic/
├── requirements.txt   pinned python deps (camoufox, playwright)
├── pyhost/            the stdio host — the ONLY C#↔Python seam
│   ├── pyhost.py      NDJSON protocol server (see its README)
│   └── README.md      PROTOCOL v1 — the contract consumers build against
├── reference/         proven flows copied from the frozen reference repo,
│                      kept as living documentation (not executed)
└── cs/                shared C# source (PyHost client, RuntimeSetup);
                       citizens compile it via one line in their csproj:
                       <Compile Include="..\sharedLogic\cs\**\*.cs" />
```

## venv rule

One shared venv at the runtime root (`%LocalAppData%\Citadel\runtime\.venv`,
overridable via `CITADEL_RUNTIME`). A feature with genuinely conflicting
dependencies MAY carry its own venv at `<runtimeRoot>\venvs\<feature>\.venv`
— the per-feature venv wins when present. Never create a venv inside this
source tree.

## deployment

`Citizen.targets` (`DeploySharedLogic`) mirrors the read-only payload
(`pyhost/pyhost.py` + `requirements.txt`) beside the shell executable at
`AppContext.BaseDirectory\sharedLogic\` — a sibling of `module\`, so the
searcher and the release directory-count invariant never see it. Runtime
state (venv, vendored python, caches) is NOT deployed; `RuntimeSetup`
creates it at the runtime root.
