# OptiScaler Installer

Windows utility for automatically detecting supported games, downloading the latest stable OptiScaler release, installing it for you, and undoing the install later if needed.

![OptiScaler Installer screenshot](assets/app-screenshot.png)

## What It Does

- Detects your GPU vendor automatically: `Nvidia`, `AMD`, or `Intel`
- Scans Steam libraries for supported games
- Lets you pick one game or install to all detected supported games
- Supports manual folder selection for non-Steam games
- Downloads the latest stable OptiScaler release automatically (with retry and timeout)
- Caches latest-release metadata for the session and reuses one prepared release across multi-game installs
- Falls back to a previously downloaded local cache when GitHub is unreachable
- Installs OptiScaler with safe proxy DLL selection
- Runs preflight checks (writability, locked files, disk space) before every install
- Keeps an install record so you can use `Undo`
- Writes a timestamped run log to `%LocalAppData%\OptiScalerInstaller\logs\`
- Includes app version and build metadata in copied/exported diagnostics output
- Shows live progress in a terminal-style log window
- Cancel button stops any in-progress scan, install, or undo

## Download

From the GitHub release page, download:

- `OptiScalerInstaller-setup-win-x64-v2.0.0.exe`
- `OptiScalerInstaller-portable-win-x64-v2.0.0.zip`

Use the setup exe for the normal install experience. Use the portable zip if you want an unpack-and-run build with no installer.

## How To Use

1. Download either the setup exe or the portable zip from the latest GitHub release.
2. If you downloaded the setup exe, run it and launch the app from the installed shortcut.
3. If you downloaded the portable zip, extract it and run `OptiScalerInstaller.exe`.
4. Wait for the app to scan your Steam libraries automatically.
5. If supported games are found:
   - leave the checked games selected
   - click `Install Selected` to install only checked games
   - or click `Install All` to install all detected supported games
6. If your game is not auto-detected, click `Manual Folder` and choose the game folder.
7. If the game is not officially supported, the app will warn you before allowing a manual override install.
8. Watch the terminal log on the right to see exactly what the installer is doing.
9. If you want to remove a managed install later, use the `Undo` button for that game.

## Important Notes

- The app downloads the latest stable OptiScaler release automatically when you install.
- If GitHub is unreachable, the installer transparently falls back to the most recently downloaded local cache (`%LocalAppData%\OptiScalerInstaller\cache\`).
- Multi-game install runs now prepare the GitHub release once, reuse it across selected games, and use bounded executable scanning plus virtualized UI lists to keep large libraries responsive.
- Before installing, the app verifies the game folder is writable, no target files are locked (game is not running), and there is at least 200 MB of free disk space.
- CPU does not matter for detection or install behavior here. Only the detected graphics vendor is used.
- If your system has both an Intel iGPU and an Nvidia GPU, the app will prefer showing `Nvidia`.
- Some games may be intentionally blocked from auto-install if they are marked unsafe.
- Manual override is available, but it is not officially supported and may not work for every game.
- Installing into protected folders such as `Program Files` may require administrator rights.
- Because the app is not code-signed yet, Windows SmartScreen may show a warning on first launch.
- Each run creates a log file under `%LocalAppData%\OptiScalerInstaller\logs\` for troubleshooting.
- Unhandled errors are caught globally and also written to the run log before reporting to the user.

## Undo

The installer keeps a manifest of files it created or replaced.

When you click `Undo`, it will:

- stage backup payload and validate restore targets before mutating the game folder
- remove files created by the installer
- restore backed-up files that were replaced
- keep unrelated files untouched
- stage restore payload first and only delete backups after the restore fully succeeds

## Snapshot Recovery

- Every install now writes a transactional backup snapshot manifest to `%LocalAppData%\OptiScalerInstaller\state\backup-snapshots.json`.
- Snapshot state is independent from `installs.json`, so backup recovery still works if install state is missing or corrupted.
- Before mutating game files, the installer writes a `Pending` snapshot and records file-level metadata (created/replaced path, backup path, file sizes, SHA-256 hashes, release tag, proxy DLL, timestamps, and status).
- If install fails after backups begin, rollback runs automatically.
- If restore or rollback is interrupted, the snapshot remains recoverable and the app prompts to resume recovery on startup.

## Resilience

| Concern | Behaviour |
|---|---|
| GitHub unreachable | Retries up to 3× with exponential backoff (1 s → 2 s → 4 s) then falls back to the most recent local cache |
| Corrupted `installs.json` | Backed up as `installs.json.corrupted` and treated as empty so the app can still run |
| Atomic writes | `installs.json`, `backup-snapshots.json`, and per-game manifests are written to a `.tmp` file first, then renamed, so a crash mid-write never corrupts state |
| Locked / in-use files | Preflight check detects locked target files before any download begins |
| Unwritable folder | Preflight check rejects the install immediately with a clear message |
| Low disk space | Preflight check requires ≥ 200 MB free on the install drive |
| Unhandled exceptions | Caught globally; written to the run log and shown to the user in a dialog |

## Current Scope

- Windows only
- Steam auto-detection
- Manual folder fallback
- Latest stable OptiScaler release only
- Bundled starter support catalog in [`data/supported-games.json`](data/supported-games.json)

## Roadmap

- Show game cover art after the app detects supported games
- Expand the bundled supported-game catalog
- Improve release packaging and signing
- Add launcher support beyond Steam

## Build From Source

```powershell
dotnet build OptiScalerInstaller.slnx
dotnet run --project .\src\OptiScalerInstaller.App\OptiScalerInstaller.App.csproj
```

## Publish

Self-contained Windows x64 publish:

```powershell
dotnet publish .\src\OptiScalerInstaller.App\OptiScalerInstaller.App.csproj -c Release /p:PublishProfile=WinX64SelfContained
```

The publish output lands under:

- `src\OptiScalerInstaller.App\bin\Release\net10.0-windows\win-x64\publish\`

## Release Packaging

Build both release artifacts locally:

```powershell
.\scripts\Build-ReleaseArtifacts.ps1 -Version 2.0.0 -SkipInstaller
```

If Inno Setup is installed and `ISCC.exe` is on `PATH`, omit `-SkipInstaller` and the script will create:

- `artifacts\release\portable\OptiScalerInstaller-portable-win-x64-v2.0.0.zip`
- `artifacts\release\installer\OptiScalerInstaller-setup-win-x64-v2.0.0.exe`
- `artifacts\release\SHA256SUMS.txt`

## Test

```powershell
dotnet test OptiScalerInstaller.slnx
```

## CI

- GitHub Actions workflow: [`.github/workflows/ci.yml`](.github/workflows/ci.yml)
- Runs `restore`, `build`, `test`, and self-contained publish validation on `windows-latest`
- Tagging `v2.0.0` runs [`.github/workflows/release.yml`](.github/workflows/release.yml) to produce the portable zip, setup exe, and `SHA256SUMS.txt`

## Release Signing And SmartScreen

- Unsigned builds are expected to trigger SmartScreen warnings, especially for new releases with low download reputation.
- If you want to sign public releases, use the Windows SDK `signtool.exe` with a code-signing certificate you already manage. This repo does not require any paid signing SaaS or external release platform.
- Timestamp signatures during release signing so binaries remain valid after the certificate expires.
- Keep the product name, file name, and certificate identity stable across releases. SmartScreen reputation is tied to consistency and builds over time.
- If you rotate to a new certificate, expect SmartScreen reputation to reset and early releases to show warnings again.
- Publish SHA-256 hashes alongside each release so users can verify the downloaded setup exe or portable zip.
- PowerShell hash example:

```powershell
Get-FileHash .\OptiScalerInstaller-setup-win-x64-v2.0.0.exe -Algorithm SHA256
```

- Practical signing flow with built-in Windows tooling:

```powershell
signtool sign /fd SHA256 /td SHA256 /tr <RFC3161-TIMESTAMP-URL> /a .\OptiScalerInstaller-setup-win-x64-v2.0.0.exe
```
