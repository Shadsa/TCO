# TCO Avalonia application

This project is the complete TCO application: Avalonia UI, native C# installer
backend, embedded verified payload, elevation transport, and single-file update
handoff. It has no PowerShell runtime dependency.

The installer tab presents only the complete Apply workflow plus paired
Apply/Restore Engine, Activate/Deactivate ReShade, and Activate/Deactivate DXVK
operations. The Readme tab renders the root Markdown document through
`MarkView.Avalonia` using native controls.

The engine selector exposes TCO Standard and TCO No-Dyn Light. PC Only is a
separate engine option and is transported through elevation independently of
the selected JSON configuration.

The read-only scan action requires no elevation. It shows engine, ReShade, DXVK,
TCC, and Shinra detection in the installer and writes a Markdown report under
`%LocalAppData%\TCO\reports`.

## Open and build

Open `TCO.slnx` or `TcoInstaller.csproj` in Rider, then build normally. From a
terminal at the repository root:

```powershell
dotnet restore TCO.slnx
dotnet build TCO.slnx -c Debug
dotnet run --project frontend\TcoInstaller\TcoInstaller.csproj
```

Set `TCO_TERA_ROOT` to preselect a TERA installation while debugging.

## Execution model

- `Status` runs without elevation.
- File-changing actions relaunch through Windows UAC with a Base64-encoded typed
  request.
- `InstallerOrchestrator` invokes the native services in-process and reports
  typed phase events and a structured installation snapshot.
- The snapshot composes the engine, ReShade, DXVK, and TCC/Shinra configuration
  models; the UI does not duplicate backend status fields.
- The payload and `payload/manifest.json` are embedded in `TCO.Core` and verified
  before use.
- Logs, update staging, and sanitized Classic+ overrides live under
  `%LocalAppData%\TCO`.
- Cancellation and failures dispose an uncommitted `FileTransaction`, restoring
  captured files and directories.

The application targets `net10.0-windows` because the graphics pipeline uses
TERA's Windows DLL layout, UAC, display APIs, and registry checks.

## Publishing

The project file contains the release defaults. From the repository root, use:

```powershell
.\tools\Publish-TCO.ps1
```

The output is the single self-contained
`artifacts\release\TCO.Installer-win-x64.exe`. Native libraries may extract to
the normal .NET single-file cache at runtime; TERA payload files are materialized
only at their required game/configuration destinations. Publish the EXE with a
GitHub asset digest or `<asset-name>.sha256` sidecar so the built-in updater can
accept it. Repository build output is centralized under `artifacts`.
