# Suggested GitHub Release Notes

## Suggested Tag

- `v2.1.0`

## Suggested Title

- `v2.1.0 - Portable and installer release`

## Release Body

OptiScaler Installer 2.1.0 for Windows.

### Highlights

- Automatically detects your graphics vendor: `Nvidia`, `AMD`, or `Intel`
- Scans Steam, Epic, GOG, and Ubisoft installs for supported games on launch
- Supports manual folder selection for games not found automatically
- Downloads the latest stable OptiScaler release automatically
- Installs OptiScaler with safe proxy DLL selection
- Stores install records so you can use `Undo` later
- Shows live progress in a terminal-style log window
- Includes backup snapshot restore tools, inline diagnostics, and hardened release extraction checks
- Ships as both a portable zip and an installer exe

### Included Assets

- `OptiScalerInstaller-portable-win-x64-v2.1.0.zip`
- `OptiScalerInstaller-setup-win-x64-v2.1.0.exe`
- `SHA256SUMS.txt`

### Notes

- Windows only
- Uses the latest stable OptiScaler release from GitHub Releases
- The bundled supported-game catalog is a starter list and will grow over time
- Windows SmartScreen may warn on first launch because the app is not code-signed yet

## Manual Release Steps

1. Commit and push the repo changes.
2. Create and push the tag `v2.1.0`.
3. Let GitHub Actions run the `Release` workflow.
4. Verify the uploaded assets and hashes on the generated GitHub release.
