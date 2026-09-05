using System.Diagnostics;
using System.Text;
using TcoInstaller.Models;
using static TcoInstaller.Backend.D3D9Files;

namespace TcoInstaller.Backend;

/// <summary>
/// Reads the live D3D9, ReShade, depth-buffer, and DXVK evidence without mutating the installation.
/// </summary>
public sealed class GraphicsStatusInspector(IDisplayResolutionService displays)
{
    public (ReShadeConfiguration ReShade, DxvkConfiguration Dxvk) Inspect(TeraPaths paths)
    {
        var files = new GraphicsPipelinePaths(paths);
        var activeKind = GetDllKind(files.ActiveD3D9);
        var proxyKind = GetDllKind(files.ProxyDxvk);
        var proxyEnabled = IniFile.GetValue(files.ReShadeConfig, "PROXY", "EnableProxyLibrary") == "1";
        var proxyLibrary = IniFile.GetValue(files.ReShadeConfig, "PROXY", "ProxyLibrary") ?? string.Empty;
        var depthFormat = IniFile.GetValue(files.ReShadeConfig, "DEPTH", "FilterFormat") ?? string.Empty;
        var depthWidth = IniFile.GetValue(files.ReShadeConfig, "DEPTH", "FilterResolutionWidth") ?? string.Empty;
        var depthHeight = IniFile.GetValue(files.ReShadeConfig, "DEPTH", "FilterResolutionHeight") ?? string.Empty;
        var depthAspect = IniFile.GetValue(files.ReShadeConfig, "DEPTH", "UseAspectRatioHeuristics") ?? string.Empty;
        var resolution = displays.GetPrimaryResolution();

        var logLength = File.Exists(files.ReShadeLog) ? new FileInfo(files.ReShadeLog).Length : 0;
        var (runtimeConfirmed, runtimeModule, renderApi) = InspectReShadeLog(files.ReShadeLog, logLength);
        var dxvkLog = Path.Combine(paths.Binaries, "TERA_d3d9.log");
        using var tera = Process.GetProcessesByName("TERA").FirstOrDefault();
        var dxvkConfirmed = File.Exists(dxvkLog) && (tera is null || File.GetLastWriteTime(dxvkLog) >= tera.StartTime);

        var reshadeActive = activeKind == "ReShade";
        var dxvkActive = activeKind == "DXVK" || reshadeActive && proxyKind == "DXVK" && proxyEnabled;
        var enabledTechniques = IniFile.GetPreambleValue(files.ReShadePreset, "Techniques") ?? string.Empty;
        var activeTechniques = enabledTechniques
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cinematicEnabled = activeTechniques.Contains("CinematicDOF@CinematicDOF.fx");
        var noBlur = File.Exists(files.ReShadePreset) &&
            !activeTechniques.Overlaps([
                "DepthHaze@DepthHaze.fx",
                "CinematicDOF@CinematicDOF.fx",
                "ADOF@qUINT_dof.fx",
                "LinearMotionBlur@LinearMotionBlur.fx"
            ]);

        var reshade = new ReShadeConfiguration
        {
            Active = reshadeActive,
            ActiveD3D9 = activeKind,
            ProxyEnabled = proxyEnabled,
            ProxyLibrary = proxyLibrary,
            OverlayShortcut = IniFile.GetValue(files.ReShadeConfig, "INPUT", "KeyOverlay") ?? string.Empty,
            EnabledTechniques = activeTechniques.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            DepthFormat = depthFormat,
            DepthResolution = $"{depthWidth}x{depthHeight}",
            PrimaryDisplayResolution = resolution.ToString(),
            DepthMatchesPrimaryDisplay = depthWidth == resolution.Width.ToString() && depthHeight == resolution.Height.ToString(),
            DepthUsesExactResolution = depthAspect == "3",
            PresetInstalled = File.Exists(files.ReShadePreset),
            ShadersInstalled = Directory.Exists(files.ReShadeShaders),
            CinematicDofInstalled = File.Exists(Path.Combine(files.ReShadeShaders, "Shaders", "OtisFX", "CinematicDOF.fx")),
            CinematicDofEnabled = cinematicEnabled,
            NoBlur = noBlur,
            RuntimeConfirmed = runtimeConfirmed,
            RuntimeModule = runtimeModule,
            RenderApi = renderApi,
            LogBytes = logLength
        };
        var dxvk = new DxvkConfiguration
        {
            Active = dxvkActive,
            ProxyTarget = proxyKind,
            RuntimeConfirmed = dxvkConfirmed
        };
        return (reshade, dxvk);
    }

    private static (bool Confirmed, string Module, string Api) InspectReShadeLog(string path, long length)
    {
        if (length == 0) return (false, "Not confirmed", "Not confirmed");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var log = reader.ReadToEnd();
        var confirmed = log.Contains("Initializing crosire's ReShade", StringComparison.Ordinal);
        var module = "Not confirmed";
        const string marker = "loaded from '";
        var markerIndex = log.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var start = markerIndex + marker.Length;
            var end = log.IndexOf('\'', start);
            if (end > start) module = Path.GetFileName(log[start..end]);
        }

        var api = log.Contains("Direct3DCreate9", StringComparison.Ordinal)
            ? "D3D9"
            : log.Contains("D3D11CreateDevice", StringComparison.Ordinal)
                ? "D3D11"
                : "Not confirmed";
        return (confirmed, module, api);
    }
}
