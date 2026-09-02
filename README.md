# TCO: TERA Classic+ Optimizer

TCO is a native Avalonia installer for the tested TERA graphics setup captured
on 2026-08-30. One self-contained Windows executable carries the engine configurations,
DXVK, ReShade runtime, shader collection, Generic Depth configuration, and
sanitized optional TCC/Shinra profiles. PowerShell is no longer used at runtime.

## Install

Download `TCO.Installer-win-x64.exe` from the latest release, close TERA, its
launcher, and Noctenium, then run the executable. Select the directory containing
`Binaries` and `S1Game`, choose an engine configuration, and use **Apply complete
pipeline**. File-changing actions request administrator access; diagnostics remain
read-only.

TCC and Shinra are optional. To install their profiles, launch each tool once so
its configuration directory exists, close both tools, and enable **Patch TCC and
Shinra during Apply** before running the complete pipeline.

Every action writes a timestamped transcript under `%LocalAppData%\TCO\logs`.
Changes are transactional: a failure or cancellation restores captured files.
Successful graphics actions lock the five managed TERA INIs read-only.

## Installed configuration

- ReShade 6.8 full add-on runtime loaded as `Binaries\d3d9.dll`.
- DXVK D3D9 loaded by ReShade through `Binaries\d3d9_dxvk.dll`.
- TERA FXAA disabled; SMAA is supplied by ReShade.
- The exact captured `ReShade.ini`, `TERA_Natural_Clarity.ini`, and shader tree.
- Generic Depth targets D24S8 at the user's active primary-display resolution,
  with exact-resolution matching, the higher-draw-call candidate, and clear
  index 1.
- TCO Standard disables texture, lightmap, priority, and dynamic streaming while
  retaining a 4096 MB fallback pool. TCO No-Dyn Light uses the same settings and
  additionally disables dynamic lights.
- Both profiles use a 1024 minimum shadow resolution, mesh and particle LOD bias
  of -2, SpeedTree billboards disabled, and 64 terrain material textures.
- Character LOD 2 and the current enabled mouse-smoothing values are preserved.
- Native TERA motion blur, depth of field, radial blur, and temporal AA remain
  disabled. ReShade owns the optional depth-based blur effects.
- Current preset techniques: SMAA, Deband, Tonemap, CAS, prod80 bloom and color
  grading, DepthHaze, and CinematicDOF.
- ReShade overlay: `Home`. Shinra paste: `Ctrl+Home`. Shinra alerts: muted.

The files in `payload\engine-reference` are the exact live INI snapshots used
to build this release. The installer deliberately applies the graphics profile
by section and key instead of copying those complete snapshots, so it does not
overwrite another player's resolution, account, or server-specific values.

## Engine preset configuration

Every selectable preset is a complete, independent JSON file under
`payload\EngineConfigurationPResets`. There is no shared baseline, inheritance,
or override catalog, so one preset can be added, reviewed, or removed without
changing another:

```json
{
  "Schema": 1,
  "Id": "tco-standard",
  "Name": "TCO Standard",
  "Description": "Maximum-quality TCO profile with texture streaming disabled.",
  "IsDefault": true,
  "Files": {
    "S1Engine.ini": {
      "Engine.Engine": {
        "bUseTextureStreaming": "False"
      },
      "TextureStreaming": {
        "PoolSize": "4096",
        "UseDynamicStreaming": "False"
      }
    }
  }
}
```

Adding a key updates it when present or inserts it into the named section when
missing. New sections are also created. INI values are represented as JSON
strings to preserve their exact Unreal Engine spelling. Supported files are `S1Engine.ini`,
`S1SystemSettings.ini`, `S1Option.ini`, `S1Input.ini`, and `BaseInput.ini`;
other filenames are rejected to prevent writes outside the managed TERA files.
TCO exposes TCO Standard and TCO No-Dyn Light. They differ only in the
`DynamicLights` value. **PC Only** is independent of both JSON profiles: when an
engine configuration is applied, it writes `AllowJoystickInput=0` when enabled
and `AllowJoystickInput=1` when disabled. Before the first native engine apply,
TCO stores a validated copy of all
five managed INIs under `ReShadeTools\engine-original`; **Restore** uses this
snapshot and refuses incomplete or modified backups.

## Actions

```text
Apply complete pipeline  Selected engine configuration + DXVK + ReShade + optional Classic+
Engine / Apply           Apply only the selected engine configuration
Engine / Restore         Restore the pre-TCO engine snapshot
ReShade / Activate       Activate ReShade and preserve the current DXVK state
ReShade / Deactivate     Deactivate ReShade and preserve the current DXVK state
DXVK / Activate          Activate DXVK and preserve the current ReShade state
DXVK / Deactivate        Restore the pre-TCO DXVK state and preserve ReShade
Scan current configuration  Detect engine, ReShade, DXVK, TCC, and Shinra without elevation
```

The scan writes a Markdown report under `%LocalAppData%\TCO\reports`. It marks
the engine configuration applied only when every managed setting matches, and
distinguishes active ReShade/DXVK from installed but inactive files.

The Classic+ checkbox affects only the complete Apply workflow. PC Only affects
both the complete pipeline and the standalone engine Apply action. The native
backend retains additional diagnostic/maintenance operations for compatibility,
but they are deliberately absent from the simplified interface.
Sanitized local Classic+ exports are stored under
`%LocalAppData%\TCO\profiles\classicplus`; it never mutates immutable resources
inside the executable.

## Installer workflow

1. The orchestrator optionally checks for a digest-verified application update.
2. Preflight validates the TERA layout and every embedded payload hash.
3. Process guards ensure TERA and requested Classic+ companions are closed.
4. A `FileTransaction` captures each destination before engine, ReShade, DXVK,
   or Classic+ services mutate it.
5. `StatusService` independently inspects the four configuration domains and
   composes an `InstallationSnapshot`.
6. Successful verification commits the transaction; exceptions or cancellation
   restore captured files. Managed INIs are re-locked in a `finally` block.

## Native architecture

- `src/TcoInstaller.Core` is the UI-independent application core. It owns typed
  requests/results, validation, transactions, profiles, graphics state, Classic+
  configuration, status inspection, and release staging.
- `src/TcoInstaller.Core/Models` contains exactly four configuration models:
  `EngineConfiguration`, `ReShadeConfiguration`, `DxvkConfiguration`, and
  `ClassicPlusConfiguration`. The small `InstallationSnapshot` composes them;
  installer transport records live separately under `Contracts`.
- `PayloadStore` reads embedded resources and verifies every payload entry
  against `payload/manifest.json`. Raw and CRLF-to-LF canonical bytes are accepted
  to tolerate Git checkout line-ending conversion without relaxing content
  integrity.
- `EngineConfigurationService` applies section/key values without replacing unrelated
  resolution, account, server, or texture-group data. `EngineBackupStore` alone
  owns and validates the pre-TCO restore snapshot.
- `GraphicsPipelineService` coordinates ReShade and DXVK transitions;
  `GraphicsStateStore`, `GraphicsPipelinePaths`, and `D3D9Files` isolate durable
  state, filesystem layout, and DLL operations. Recovery metadata is persisted
  separately as `reshade-configuration.json` and `dxvk-configuration.json`.
- `ClassicPlusService` applies bundled or local sanitized TCC/Shinra profiles.
- `FileTransaction` captures files and directories before mutation and rolls them
  back unless the operation commits.
- `frontend/TcoInstaller` is the Avalonia presentation and Windows elevation
  shell. It calls one shared `InstallerOrchestrator` in-process with typed
  requests, progress, and status results.
- `tests/TcoInstaller.Tests` is a dependency-free integration harness for payload
  integrity, INI mutation, rollback, profiles, graphics transitions, and updates.
- `tools/Update-PayloadManifest.ps1` deterministically rebuilds the embedded
  payload manifest. `legacy/powershell` is a frozen historical snapshot only.
- A separate Readme tab renders this embedded Markdown document with native
  Avalonia controls.

Runtime data is stored under `%LocalAppData%\TCO` rather than beside the EXE.

## Build and test

Open `TCO.slnx` in Rider or use .NET 10 from the repository root:

```powershell
dotnet restore TCO.slnx
dotnet build TCO.slnx -c Debug
dotnet run --project tests\TcoInstaller.Tests\TcoInstaller.Tests.csproj
.\tools\Publish-TCO.ps1
```

Publishing produces the self-contained
`artifacts\release\TCO.Installer-win-x64.exe`. The executable contains the .NET
runtime, Avalonia dependencies, README, configuration presets, and verified TCO
payload; it needs no companion files. The publish script cleans only that release
directory, verifies the one-file contract, and prints its SHA-256. Generated build
files are centralized under `artifacts` and can be discarded wholesale.
Every Release build invokes the same packaging script as a guarded post-build
step. The product version is centralized in `Directory.Build.props`.
The TCO backend is intentionally
Windows-only because TERA, UAC, D3D9 DLL placement, display enumeration, and the
ReShade Vulkan-layer registry are Windows-specific.

## Release updates

`Apply` can query <https://github.com/Shadsa/TCO/releases> for a newer semantic
version. A release must contain exactly one preferred `TCO*.exe` or
`TERA-Complete*.exe` asset and expose its SHA-256 through GitHub's asset digest or
a matching `.sha256` sidecar. TCO verifies the download, starts that staged EXE,
waits for the old process to exit, atomically replaces it with a rollback backup,
verifies the installed bytes again, and resumes the original request with update
checking disabled. Network or release-format errors leave the current executable
and embedded package in use.

The Classic+ export removes account hashes, usernames, tokens, and absolute user
paths. On install, the Shinra export directory is rebuilt under the current
user's Documents folder.

## Notes

The full add-on build is required for Generic Depth. ReShade may disable add-ons
when its network-activity protection is triggered; this package does not bypass
server or anti-cheat policy. Use it only where the server permits ReShade
add-ons. `DisableReShade` is the quick diagnostic path, while `RestoreReShade`
removes the package's ReShade files and restores the recorded starting D3D9
state.
