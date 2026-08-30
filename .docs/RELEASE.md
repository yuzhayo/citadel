# Citadel Windows Release Guide

Citadel ships as a per-user Velopack installer. The package is self-contained
for `win-x64`, so the target computer does not need a separate .NET 10 Desktop
Runtime installation.

## Local installer

From the repository root:

```powershell
.\tools\Build-Release.ps1
```

Outputs:

```text
artifacts/publish/win-x64/   complete staging tree
Releases/                    Setup.exe, portable ZIP, packages, release index
```

The script publishes `Citadel.Shell`, then discovers every immediate
`module/<screen>/Module.*.csproj` and deploys it into the staging `module/`
folder. A new screen made from `module/blank/` therefore joins the next installer
without editing the solution, release script, or workflow.

Do not distribute `Citadel.Shell.exe` by itself. The app requires the other
published assemblies, `Components/`, and its citizen folders.

## GitHub release

`.github/workflows/ci.yml` tests every pull request and every `main` commit.
`.github/workflows/release.yml` is manually dispatched with a patch, minor, or
major bump and will only release the exact `main` commit whose `build` check is
green. It then:

1. derives the version from `version.props` and existing `v*` tags;
2. downloads the previous Velopack release when one exists;
3. builds the self-contained installer and delta package;
4. publishes the assets, tag, release notes, and installer SHA-256 to GitHub.

The first release is `0.1.0`. Later dispatches bump from the latest tag unless
`version.props` is already higher.

## In-app update

The main Settings screen contains an **Updates** card modeled on dhepz:

1. **Check now** reads the public
   `https://github.com/yuzhayo/citadel` GitHub Releases feed;
2. when a newer non-prerelease package exists, **Update & restart** appears;
3. Citadel downloads in the background, shows progress, asks Velopack to apply
   the package after process exit, and performs a real application shutdown so
   the resident tray instance cannot keep files locked.

There is no automatic startup check or polling timer. An unpackaged development
build shows the current version but disables the actions; update is active for a
valid Velopack installation or portable package.

GitHub Release payload is the source of truth. Velopack replacing the installed
`current/` directory — including the shipped `module/` citizens and component
presets beside the EXE — is intended. Manual changes inside the installed payload
are not preserved; customize the source tree and publish a new release instead.

`v0.1.1` is the first updater-bearing release. A `v0.1.0` installation must move
to it or a later release manually once; `v0.1.1` and later can use **Update &
restart**.

## Deliberate limits

- The installer is currently unsigned, so Windows SmartScreen may warn until a
  code-signing certificate is configured.
