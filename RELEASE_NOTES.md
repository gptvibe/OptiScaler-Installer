# Suggested GitHub Release Notes

## Suggested Tag

- `v0.1.0`

## Suggested Title

- `v0.1.0 - Initial public release`

## Release Body

First public release of OptiScaler Installer for Windows.

### Highlights

- Automatically detects your graphics vendor: `Nvidia`, `AMD`, or `Intel`
- Scans Steam libraries for supported games on launch
- Supports manual folder selection for games not found automatically
- Downloads the latest stable OptiScaler release automatically
- Installs OptiScaler with safe proxy DLL selection
- Stores install records so you can use `Undo` later
- Shows live progress in a terminal-style log window

### Included Asset

- `OptiScalerInstaller-win-x64.exe`

### Notes

- Windows only
- Uses the latest stable OptiScaler release from GitHub Releases
- The bundled supported-game catalog is a starter list and will grow over time
- Windows SmartScreen may warn on first launch because the app is not code-signed yet

## Manual Release Steps

1. Commit and push the repo changes.
2. Create the tag `v0.1.0`.
3. Open the GitHub repo release page.
4. Create a new release from that tag.
5. Upload `artifacts/packages/OptiScalerInstaller-win-x64.exe`.
6. Paste the release body above.
