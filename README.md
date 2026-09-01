# TERA Complete Graphics and Classic+ Profile

This package reproduces the tested TERA setup captured on 2026-08-30. It
combines the stable UE3 engine profile, a D3D9 ReShade-to-DXVK pipeline, the
current ReShade preset and Generic Depth selection, and sanitized TCC/Shinra
settings.

## Prerequisites

No TCC or Shinra installation is required for the default graphics setup. To
install their bundled profiles too, install and launch both tools at least once,
then use `-IncludeClassicPlus`. Their configuration folders must already exist
under `%APPDATA%\Crazy-eSports-ClassicPlus\mods\external`. The installer checks
them before changing TERA files and applies their profiles only after the
ReShade/DXVK setup succeeds.

## Install

Extract the package folder directly inside the TERA installation directory:

```text
TERA/
  Binaries/
  S1Game/
  TERA-Complete-Graphics-Package-2026-08-30-final/
    Install.ps1
```

Open PowerShell in the package folder, close TERA, its launcher, and Noctenium,
then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

This single CLI entry point displays progress and writes all update and install
output to a timestamped file under `logs`. An administrator consent prompt is
required because it checks the system ReShade Vulkan-layer registry values. It
finishes by locking the five managed TERA INI files read-only.

To include the optional Classic+ profiles, also close TCC and Shinra Meter and
run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -IncludeClassicPlus
```

Before the default `Apply` action, `Install.ps1` calls
`modules\update_tco.ps1` to check the latest published release at
<https://github.com/Shadsa/TCO/releases>. A newer release ZIP is
downloaded and installed only after its SHA-256 and complete package manifest
have been validated. If GitHub is unavailable or no release exists, the local
validated package continues normally. A failed file replacement is rolled back;
an incomplete rollback stops the installation.

### Publishing updates

The latest GitHub release must contain exactly one ZIP asset whose name begins
with `TCO` or `TERA-Complete`. The ZIP must contain one package root with
`manifest.json`, `Install.ps1`, and the `modules` folder. Publish a GitHub asset
SHA-256 digest or a companion `<asset-name>.sha256` file. Packages without these
integrity checks are rejected and the existing local package is retained.

## Installed configuration

- ReShade 6.8 full add-on runtime loaded as `Binaries\d3d9.dll`.
- DXVK D3D9 loaded by ReShade through `Binaries\d3d9_dxvk.dll`.
- TERA FXAA disabled; SMAA is supplied by ReShade.
- The exact captured `ReShade.ini`, `TERA_Natural_Clarity.ini`, and shader tree.
- Generic Depth targets D24S8 at the user's active primary-display resolution,
  with exact-resolution matching, the higher-draw-call candidate, and clear
  index 1.
- Texture streaming pool 4096 MB, high view distance, shadows, ambient
  occlusion, bloom, lens flare, anisotropic filtering, and stable frame pacing.
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

## Engine profile configuration

All managed engine settings live in `payload\engine-profile.json`, grouped as
INI filename, section, then key/value:

```json
{
  "S1Engine.ini": {
    "TextureStreaming": {
      "PoolSize": 4096,
      "UseDynamicStreaming": true
    }
  }
}
```

Adding a key updates it when present or inserts it into the named section when
missing. New sections are also created. Values may be JSON strings, numbers, or
booleans. Supported file categories are `S1Engine.ini`,
`S1SystemSettings.ini`, `S1Option.ini`, `S1Input.ini`, and `BaseInput.ini`;
other filenames are rejected to prevent writes outside the managed TERA files.
The `Status` action validates every entry currently present in the JSON profile.

## Actions

```text
Apply               Engine + DXVK + ReShade; checks GitHub first
ApplyClassicPlus    TCC and Shinra profiles only
ExportClassicPlus   Refresh the sanitized profile payload
EnableReShade       Reapply ReShade/DXVK
DisableReShade      Keep DXVK active without ReShade
RestoreReShade      Restore the D3D9 state recorded before first use
LockConfigs         Mark the five managed TERA INIs read-only
UnlockConfigs       Remove the read-only attribute
Status              Show engine, D3D9 pipeline, depth, and profile status
```

Add `-IncludeClassicPlus` to `Apply` or `EnableReShade` to install the TCC and
Shinra profiles after the graphics phase. Use `-SkipUpdate` only for an offline
or diagnostic run.

## Avalonia frontend

A compact graphical frontend is available under `frontend\TcoInstaller`. Open
`TCO.slnx` in Rider, or build it from the repository root:

```powershell
dotnet build TCO.slnx -c Debug
dotnet run --project frontend\TcoInstaller\TcoInstaller.csproj
```

The frontend feeds validated options to `Install.ps1`, requests UAC only for
file-changing actions, runs PowerShell without a console window, and streams
phase events plus normal output into its live log panel. Its UI is
cross-platform; the TCO installation backend remains Windows-only.

`-OutputMode JsonLines` is reserved for frontend integration. It adds
`TCO_EVENT` JSON lines without removing the normal CLI output or transcript.
See `frontend\TcoInstaller\README.md` for development and publishing details.

The Classic+ payload removes account hashes, usernames, tokens, and absolute
user paths. On install, the Shinra export directory is rebuilt under the
current user's Documents folder.

## Notes

The full add-on build is required for Generic Depth. ReShade may disable add-ons
when its network-activity protection is triggered; this package does not bypass
server or anti-cheat policy. Use it only where the server permits ReShade
add-ons. `DisableReShade` is the quick diagnostic path, while `RestoreReShade`
removes the package's ReShade files and restores the recorded starting D3D9
state.
