# TCO Avalonia frontend

This is a minimal Avalonia 12 desktop host for the root `Install.ps1` CLI. It
does not duplicate installer logic: actions and options are passed to PowerShell
through `ProcessStartInfo.ArgumentList`, while stdout and stderr are streamed
into the window.

## Open and build

Open `TCO.slnx` or `TcoInstaller.csproj` in Rider, then build normally. From a
terminal at the repository root:

```powershell
dotnet restore TCO.slnx
dotnet build TCO.slnx -c Debug
dotnet run --project frontend\TcoInstaller\TcoInstaller.csproj
```

The application locates `Install.ps1` by walking upward from its executable and
working directory. Set `TCO_PACKAGE_ROOT` to override discovery while debugging.

## Execution model

- Status runs without elevation.
- File-changing actions relaunch the frontend through Windows UAC using a
  Base64-encoded, typed request passed as one process argument.
- The elevated frontend starts `powershell.exe` without a console window and
  captures both output streams asynchronously.
- `Install.ps1 -OutputMode JsonLines` emits `TCO_EVENT` records for reliable
  phase progress. Normal CLI output remains enabled.
- The authoritative transcript remains under the package `logs` directory.

The Avalonia shell builds on Windows, macOS, and Linux. The actual TCO backend is
intentionally disabled outside Windows because the current graphics pipeline
uses TERA's Windows DLL layout, UAC, and Windows registry checks.

## Publishing

Start with the Windows build used by TERA players:

```powershell
dotnet publish frontend\TcoInstaller\TcoInstaller.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true
```

Do not add the running frontend executable to the PowerShell-managed package
manifest without first implementing staged executable replacement. Windows
locks the running executable during an update. The current updater continues to
manage `Install.ps1`, modules, and payload files.
