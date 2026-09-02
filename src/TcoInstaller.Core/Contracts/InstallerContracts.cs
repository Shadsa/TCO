namespace TcoInstaller.Contracts;

/// <summary>Supported commands exposed by the native backend.</summary>
public enum InstallerAction
{
    Apply,
    ApplyEngine,
    RestoreEngine,
    Status,
    EnableReShade,
    DisableReShade,
    EnableDxvk,
    DisableDxvk,
    RestoreReShade,
    ApplyClassicPlus,
    ExportClassicPlus,
    LockConfigs,
    UnlockConfigs
}

/// <summary>Typed input shared by the UI, elevated process, updater, and orchestrator.</summary>
public sealed record InstallerRequest(
    InstallerAction Action,
    string TeraRoot,
    bool IncludeClassicPlus,
    bool CheckForUpdates,
    string? EngineConfigurationId = null,
    bool PcOnly = false);

/// <summary>Versioned UAC transport wrapper for an installer request.</summary>
public sealed record ElevationEnvelope(
    int Schema,
    InstallerRequest Request);

/// <summary>One phase update emitted while an action is running.</summary>
public sealed record InstallerProgress(
    string Phase,
    string Status,
    string Message,
    bool IsError = false);

/// <summary>Runner result containing the inspection snapshot or a staged update handoff.</summary>
public sealed record InstallerRunResult(
    int ExitCode,
    string LogPath,
    InstallationSnapshot? Snapshot = null,
    StagedUpdate? Update = null,
    string? ReportPath = null);

/// <summary>Digest-verified executable waiting to replace the current application.</summary>
public sealed record StagedUpdate(
    string Version,
    string ExecutablePath,
    string Sha256);
