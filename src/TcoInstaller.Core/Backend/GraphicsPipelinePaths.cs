namespace TcoInstaller.Backend;

/// <summary>Centralizes every path touched by the ReShade/DXVK workflow.</summary>
internal sealed class GraphicsPipelinePaths(TeraPaths paths)
{
    public TeraPaths Tera { get; } = paths;
    public string ToolsRoot { get; } = paths.Tools;
    public string ActiveD3D9 { get; } = Path.Combine(paths.Binaries, "d3d9.dll");
    public string ProxyDxvk { get; } = Path.Combine(paths.Binaries, "d3d9_dxvk.dll");
    public string ReShadeConfig { get; } = Path.Combine(paths.Binaries, "ReShade.ini");
    public string DisabledConfig { get; } = Path.Combine(paths.Binaries, "ReShade.ini.proxy-disabled");
    public string ReShadePreset { get; } = Path.Combine(paths.Binaries, "TERA_Natural_Clarity.ini");
    public string ReShadeShaders { get; } = Path.Combine(paths.Binaries, "reshade-shaders");
    public string ReShadeLog { get; } = Path.Combine(paths.Binaries, "ReShade.log");
    public string ReShadeState { get; } = Path.Combine(paths.Tools, "reshade-configuration.json");
    public string DxvkState { get; } = Path.Combine(paths.Tools, "dxvk-configuration.json");
    public IReadOnlyList<string> LegacyStates { get; } =
    [
        Path.Combine(paths.Tools, "tera-reshade-proxy-state.json"),
        Path.Combine(paths.Tools, "tera-reshade-state.json")
    ];
    public string OriginalBackupRoot { get; } = Path.Combine(paths.Tools, "tera-reshade-original");
}
