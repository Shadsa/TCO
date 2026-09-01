namespace TcoInstaller.Models;

public sealed record InstallerRequest(
    string Action,
    string TeraRoot,
    bool IncludeClassicPlus,
    bool CheckForUpdates);

public sealed record ElevationEnvelope(
    string PackageRoot,
    InstallerRequest Request);

public sealed record InstallerEvent(
    string? Event,
    string? Phase,
    string? Status,
    string? Message);

public sealed record InstallerOutput(
    string Text,
    bool IsError,
    InstallerEvent? Event = null);

public sealed record InstallerRunResult(
    int ExitCode,
    string LogPath);
