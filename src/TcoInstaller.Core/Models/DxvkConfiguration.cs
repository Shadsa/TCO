namespace TcoInstaller.Models;

/// <summary>
/// Holds DXVK recovery metadata and whether the runtime is active directly or through ReShade.
/// </summary>
public sealed record DxvkConfiguration
{
    public int Schema { get; init; } = 1;
    public string TeraRoot { get; init; } = string.Empty;
    public DateTimeOffset? CapturedAt { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string OriginalD3D9Kind { get; init; } = string.Empty;
    public string? OriginalD3D9Backup { get; init; }

    public bool Active { get; init; }
    public string ProxyTarget { get; init; } = string.Empty;
    public bool RuntimeConfirmed { get; init; }
}
