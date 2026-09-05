namespace TcoInstaller.Models;

/// <summary>
/// Describes one complete engine preset and, after inspection, its installed state.
/// The same shape also stores the validated original-file backup ledger.
/// </summary>
public sealed record EngineConfiguration
{
    public int Schema { get; init; } = 1;
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Files { get; init; } = [];

    public string TeraRoot { get; init; } = string.Empty;
    public DateTimeOffset? CapturedAt { get; init; }
    public Dictionary<string, string> BackupFiles { get; init; } = [];
    public bool BackupAvailable { get; init; }
    public int ChecksPassed { get; init; }
    public int ChecksTotal { get; init; }
    public string TexturePoolMb { get; init; } = string.Empty;
    public string Fxaa { get; init; } = string.Empty;
    public string FpsCap { get; init; } = string.Empty;
    public int MonitorRefreshRateHz { get; init; }
    public bool PcOnly { get; init; }
    public bool ConfigsLocked { get; init; }
    public Dictionary<string, string> Mismatches { get; init; } = [];
    public Dictionary<string, bool> ConfigLocks { get; init; } = [];

    public override string ToString() => Name;
}
