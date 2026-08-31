# credenz — citadel's identity vault

Central storage for everything the screens READ and WRITE: identities going
in (accounts, sessions, profile homes) and results coming out (claimed keys,
exports). One folder per target; data flows both ways.

```
credenz/
└── google/
    ├── profiles/     camoufox persistent profile homes, keyed by profile ID
    └── accounts/     identity.json + DPAPI CurrentUser password.dat per ID
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
- `identity.json` stores the detected provider email and timestamps. Passwords
  never enter JSON; `password.dat` is protected for the current Windows user
  and decrypted only when CamoProf starts a relog attempt.
