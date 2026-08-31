# TERA Complete Graphics and Classic+ Profile

This package reproduces the tested TERA setup captured on 2026-08-30. It
combines the stable UE3 engine profile, a D3D9 ReShade-to-DXVK pipeline, the
current ReShade preset and Generic Depth selection, and sanitized TCC/Shinra
settings.

## Install

Extract the package folder directly inside the TERA installation directory:

```text
TERA/
  Binaries/
  S1Game/
  TERA-Complete-Graphics-Package-2026-08-30-final/
    Install.cmd
```

Close TERA, the launcher, Noctenium, TCC, and Shinra Meter. Run `Install.cmd`.
The script requests administrator rights because it checks the system ReShade
Vulkan-layer registry values. It finishes by locking the five managed TERA INI
files read-only.

PowerShell equivalent:

```powershell
.\Install-TERA-Complete.ps1 -Action Apply
```

## Installed configuration

- ReShade 6.8 full add-on runtime loaded as `Binaries\d3d9.dll`.
- DXVK D3D9 loaded by ReShade through `Binaries\d3d9_dxvk.dll`.
- TERA FXAA disabled; SMAA is supplied by ReShade.
- The exact captured `ReShade.ini`, `TERA_Natural_Clarity.ini`, and shader tree.
- Generic Depth targets D24S8 at 3440x1440, exact-resolution matching, the
  higher-draw-call candidate, and clear index 1.
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

## Actions

```text
Apply               Engine + DXVK + ReShade + TCC + Shinra
ApplyClassicPlus    TCC and Shinra profiles only
ExportClassicPlus   Refresh the sanitized profile payload
EnableReShade       Reapply the saved ReShade/DXVK configuration
DisableReShade      Keep DXVK active without ReShade
RestoreReShade      Restore the D3D9 state recorded before first use
LockConfigs         Mark the five managed TERA INIs read-only
UnlockConfigs       Remove the read-only attribute
Status              Show engine, D3D9 pipeline, depth, and profile status
```

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
