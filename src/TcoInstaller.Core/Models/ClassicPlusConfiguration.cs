namespace TcoInstaller.Models;

/// <summary>
/// Describes the optional TCC and Shinra installation, hotkey, sound, and override state.
/// </summary>
public sealed record ClassicPlusConfiguration
{
    public bool TccInstalled { get; init; }
    public bool ShinraInstalled { get; init; }
    public string PasteShortcut { get; init; } = string.Empty;
    public bool AudioMuted { get; init; }
    public bool OverrideAvailable { get; init; }
}
