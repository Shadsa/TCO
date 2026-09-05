namespace TcoInstaller.Models;

/// <summary>
/// Holds ReShade recovery metadata and the current runtime, depth, and preset state.
/// </summary>
public sealed record ReShadeConfiguration
{
    public int Schema { get; init; } = 1;
    public string TeraRoot { get; init; } = string.Empty;
    public DateTimeOffset? CapturedAt { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string OriginalFxaa { get; init; } = string.Empty;
    public int? OriginalVulkanLayer64 { get; init; }
    public int? OriginalVulkanLayer32 { get; init; }

    public bool Active { get; init; }
    public string ActiveD3D9 { get; init; } = string.Empty;
    public bool ProxyEnabled { get; init; }
    public string ProxyLibrary { get; init; } = string.Empty;
    public string OverlayShortcut { get; init; } = string.Empty;
    public IReadOnlyList<string> EnabledTechniques { get; init; } = [];
    public string DepthFormat { get; init; } = string.Empty;
    public string DepthResolution { get; init; } = string.Empty;
    public string PrimaryDisplayResolution { get; init; } = string.Empty;
    public bool DepthMatchesPrimaryDisplay { get; init; }
    public bool DepthUsesExactResolution { get; init; }
    public bool PresetInstalled { get; init; }
    public bool ShadersInstalled { get; init; }
    public bool CinematicDofInstalled { get; init; }
    public bool CinematicDofEnabled { get; init; }
    public bool NoBlur { get; init; }
    public bool RuntimeConfirmed { get; init; }
    public string RuntimeModule { get; init; } = string.Empty;
    public string RenderApi { get; init; } = string.Empty;
    public long LogBytes { get; init; }
}
