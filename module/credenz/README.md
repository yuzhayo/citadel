# credenz — citadel's identity vault

Central storage for everything the screens READ and WRITE: identities going
in (accounts, sessions, profile homes) and results coming out (claimed keys,
exports). One folder per target; data flows both ways.

```
credenz/
└── google/
    └── profiles/     camoufox persistent profile homes, one per account
```

## Rules

- Screens stay stateless code — they read identities from here and write
  results back here. Nothing valuable nests inside a screen folder.
- **Content is gitignored.** Only this README and `.gitkeep` markers may be
  committed. Credentials, profiles, sessions, and results must never enter
  git history.
- In development the vault is this folder. When the app is installed
  (read-only program folder), the vault resolves to
  `%LocalAppData%\Citadel\Credenz`. C# resolves the location and hands the
  absolute path to Python via `CITADEL_CREDENZ` — Python never computes
  paths itself.
