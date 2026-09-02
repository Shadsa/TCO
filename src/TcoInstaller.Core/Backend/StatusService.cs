using System.Text;
using TcoInstaller.Contracts;

namespace TcoInstaller.Backend;

/// <summary>
/// Composes the four independently inspected configuration domains into one UI/report snapshot.
/// This class is read-only and contains no installation side effects.
/// </summary>
public sealed class StatusService(
    EngineConfigurationService engine,
    GraphicsStatusInspector graphics,
    ClassicPlusService classicPlus)
{
    public InstallationSnapshot Inspect(TeraPaths paths)
    {
        paths.Validate();
        var engineConfiguration = engine.Inspect(paths);
        var (reshade, dxvk) = graphics.Inspect(paths);
        var classicPlusConfiguration = classicPlus.Inspect();
        var pipeline = reshade.Active && dxvk.Active
            ? "ReShade D3D9 -> DXVK -> Vulkan"
            : reshade.Active
                ? "ReShade -> native D3D9"
                : dxvk.Active
                    ? "DXVK only (D3D9)"
                    : "Native D3D9";

        return new InstallationSnapshot(
            paths.Root,
            pipeline,
            engineConfiguration,
            reshade,
            dxvk,
            classicPlusConfiguration);
    }

    public static string Format(InstallationSnapshot snapshot)
    {
        var text = new StringBuilder();
        text.AppendLine($"TERA: {snapshot.TeraRoot}");
        text.AppendLine($"Engine preset: {snapshot.Engine.Name} ({snapshot.Engine.ChecksPassed}/{snapshot.Engine.ChecksTotal} settings)");
        text.AppendLine($"Engine backup: {(snapshot.Engine.BackupAvailable ? "available" : "not captured")}");
        text.AppendLine($"PC Only: {(snapshot.Engine.PcOnly ? "enabled" : "disabled")}");
        text.AppendLine($"Pipeline: {snapshot.ConfiguredPipeline}");
        text.AppendLine($"Depth: {snapshot.ReShade.DepthFormat} at {snapshot.ReShade.DepthResolution} (display {snapshot.ReShade.PrimaryDisplayResolution})");
        text.AppendLine($"ReShade runtime: {(snapshot.ReShade.RuntimeConfirmed ? "confirmed" : "not confirmed")}");
        text.AppendLine($"Config files: {(snapshot.Engine.ConfigsLocked ? "locked" : "unlocked")}");
        if (snapshot.Engine.Mismatches.Count > 0)
        {
            text.AppendLine("Engine mismatches:");
            foreach (var mismatch in snapshot.Engine.Mismatches)
                text.AppendLine($"  {mismatch.Key}: {mismatch.Value}");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Creates the durable Markdown report written by the read-only scan action.</summary>
    public static string FormatMarkdown(InstallationSnapshot snapshot, DateTimeOffset generatedAt)
    {
        var engineApplied = snapshot.Engine.ChecksTotal > 0 && snapshot.Engine.ChecksPassed == snapshot.Engine.ChecksTotal;
        var reshadeDetected = snapshot.ReShade.Active || snapshot.ReShade.PresetInstalled ||
            snapshot.ReShade.ShadersInstalled || snapshot.ReShade.RuntimeConfirmed;
        var dxvkDetected = snapshot.Dxvk.Active || snapshot.Dxvk.RuntimeConfirmed ||
            snapshot.Dxvk.ProxyTarget.Equals("DXVK", StringComparison.OrdinalIgnoreCase);

        var text = new StringBuilder();
        text.AppendLine("# TCO Configuration Report");
        text.AppendLine();
        text.AppendLine($"Generated: {generatedAt:O}");
        text.AppendLine($"TERA root: `{snapshot.TeraRoot}`");
        text.AppendLine();
        text.AppendLine("## Detection Summary");
        text.AppendLine();
        text.AppendLine("| Component | Detected | State |");
        text.AppendLine("| --- | --- | --- |");
        text.AppendLine($"| Engine configuration | {YesNo(engineApplied)} | {snapshot.Engine.Name} ({snapshot.Engine.ChecksPassed}/{snapshot.Engine.ChecksTotal}) |");
        text.AppendLine($"| ReShade | {YesNo(reshadeDetected)} | {ActiveInstalled(snapshot.ReShade.Active, reshadeDetected)} |");
        text.AppendLine($"| DXVK | {YesNo(dxvkDetected)} | {ActiveInstalled(snapshot.Dxvk.Active, dxvkDetected)} |");
        text.AppendLine($"| TCC | {YesNo(snapshot.ClassicPlus.TccInstalled)} | {Detected(snapshot.ClassicPlus.TccInstalled)} |");
        text.AppendLine($"| Shinra | {YesNo(snapshot.ClassicPlus.ShinraInstalled)} | {Detected(snapshot.ClassicPlus.ShinraInstalled)} |");
        text.AppendLine();
        text.AppendLine("## Engine Configuration");
        text.AppendLine();
        text.AppendLine($"- Closest configuration: {snapshot.Engine.Name} (`{snapshot.Engine.Id}`)");
        text.AppendLine($"- Fully applied: {YesNo(engineApplied)}");
        text.AppendLine($"- Matching settings: {snapshot.Engine.ChecksPassed}/{snapshot.Engine.ChecksTotal}");
        text.AppendLine($"- Texture pool: {snapshot.Engine.TexturePoolMb} MB");
        text.AppendLine($"- FXAA: {snapshot.Engine.Fxaa}");
        text.AppendLine($"- PC Only: {(snapshot.Engine.PcOnly ? "Enabled" : "Disabled")}");
        text.AppendLine($"- Original backup: {Detected(snapshot.Engine.BackupAvailable)}");
        text.AppendLine($"- Managed INIs: {(snapshot.Engine.ConfigsLocked ? "locked" : "unlocked or mixed")}");
        if (snapshot.Engine.Mismatches.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("### Mismatches");
            text.AppendLine();
            foreach (var mismatch in snapshot.Engine.Mismatches)
                text.AppendLine($"- `{mismatch.Key}`: {mismatch.Value}");
        }

        text.AppendLine();
        text.AppendLine("## ReShade");
        text.AppendLine();
        text.AppendLine($"- State: {ActiveInstalled(snapshot.ReShade.Active, reshadeDetected)}");
        text.AppendLine($"- Active D3D9 module: {snapshot.ReShade.ActiveD3D9}");
        text.AppendLine($"- Runtime confirmed by log: {YesNo(snapshot.ReShade.RuntimeConfirmed)}");
        text.AppendLine($"- Runtime module: {snapshot.ReShade.RuntimeModule}");
        text.AppendLine($"- Overlay key: {snapshot.ReShade.HomeKey}");
        text.AppendLine($"- Generic Depth: {snapshot.ReShade.DepthFormat} at {snapshot.ReShade.DepthResolution}");
        text.AppendLine($"- Primary display: {snapshot.ReShade.PrimaryDisplayResolution}");
        text.AppendLine($"- Preset: {Detected(snapshot.ReShade.PresetInstalled)}");
        text.AppendLine($"- Shader tree: {Detected(snapshot.ReShade.ShadersInstalled)}");

        text.AppendLine();
        text.AppendLine("## DXVK");
        text.AppendLine();
        text.AppendLine($"- State: {ActiveInstalled(snapshot.Dxvk.Active, dxvkDetected)}");
        text.AppendLine($"- ReShade proxy target: {snapshot.Dxvk.ProxyTarget}");
        text.AppendLine($"- Runtime confirmed by log: {YesNo(snapshot.Dxvk.RuntimeConfirmed)}");
        text.AppendLine($"- Detected render API: {snapshot.ReShade.RenderApi}");

        text.AppendLine();
        text.AppendLine("## TCC and Shinra");
        text.AppendLine();
        text.AppendLine($"- TCC: {Detected(snapshot.ClassicPlus.TccInstalled)}");
        text.AppendLine($"- Shinra: {Detected(snapshot.ClassicPlus.ShinraInstalled)}");
        text.AppendLine($"- Shinra paste shortcut: {snapshot.ClassicPlus.PasteShortcut}");
        text.AppendLine($"- Shinra audio muted: {YesNo(snapshot.ClassicPlus.AudioMuted)}");
        text.AppendLine($"- Local override: {Detected(snapshot.ClassicPlus.OverrideAvailable)}");
        return text.ToString();
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";
    private static string Detected(bool value) => value ? "Detected" : "Not detected";
    private static string ActiveInstalled(bool active, bool detected) => active ? "Active" : detected ? "Installed, inactive" : "Not detected";
}
