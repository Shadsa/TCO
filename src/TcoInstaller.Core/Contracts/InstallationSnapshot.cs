using TcoInstaller.Models;

namespace TcoInstaller.Contracts;

/// <summary>
/// Immutable result of one installation inspection, composed from the four configuration domains.
/// </summary>
public sealed record InstallationSnapshot(
    string TeraRoot,
    string ConfiguredPipeline,
    EngineConfiguration Engine,
    ReShadeConfiguration ReShade,
    DxvkConfiguration Dxvk,
    ClassicPlusConfiguration ClassicPlus);
