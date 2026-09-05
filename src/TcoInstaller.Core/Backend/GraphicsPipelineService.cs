using TcoInstaller.Models;
using static TcoInstaller.Backend.D3D9Files;

namespace TcoInstaller.Backend;

/// <summary>
/// Executes reversible ReShade and DXVK transitions. It captures recovery state first,
/// mutates files and INIs through a transaction, then validates the requested pipeline.
/// </summary>
public sealed class GraphicsPipelineService
{
    private const string ReShadeRuntime = "runtime/ReShade64.dll";
    private const string DxvkRuntime = "dxvk/d3d9.dll";
    private const string ReShadeConfigPayload = "reshade/ReShade.ini";
    private const string ReShadePresetPayload = "reshade/TERA_Natural_Clarity.ini";
    private static readonly string[] DefaultAtmosphereTechniques =
    [
        "DepthHaze@DepthHaze.fx"
    ];
    private static readonly HashSet<string> BlurTechniques = new(
    [
        .. DefaultAtmosphereTechniques,
        "CinematicDOF@CinematicDOF.fx",
        "ADOF@qUINT_dof.fx",
        "LinearMotionBlur@LinearMotionBlur.fx"
    ], StringComparer.OrdinalIgnoreCase);

    private readonly PayloadStore payload;
    private readonly EngineConfigurationService engineConfiguration;
    private readonly IDisplayResolutionService displays;
    private readonly IVulkanLayerRegistry registry;
    private readonly GraphicsStateStore states;

    public GraphicsPipelineService(
        PayloadStore payload,
        EngineConfigurationService engineConfiguration,
        IDisplayResolutionService displays,
        IVulkanLayerRegistry registry)
    {
        this.payload = payload;
        this.engineConfiguration = engineConfiguration;
        this.displays = displays;
        this.registry = registry;
        states = new GraphicsStateStore(payload, engineConfiguration, registry);
    }

    public async Task ValidatePayloadAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { ReShadeRuntime, DxvkRuntime })
            await payload.ReadVerifiedBytesAsync(path, cancellationToken);
        foreach (var path in new[] { ReShadeConfigPayload, ReShadePresetPayload })
            if (!payload.Contains(path)) throw new InvalidDataException($"The embedded ReShade default is missing: {path}");
        if (!payload.Files.Any(path => path.StartsWith("reshade/reshade-shaders/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The embedded ReShade shader tree is missing.");
        _ = displays.GetPrimaryResolution();
    }

    public Task EnablePipelineAsync(
        TeraPaths paths,
        FileTransaction transaction,
        CancellationToken cancellationToken,
        bool noBlur = false,
        IReadOnlyDictionary<string, bool>? configuredTechniques = null,
        string? overlayShortcut = null,
        string? fxaa = null) =>
        EnableReShadeCoreAsync(paths, transaction, true, noBlur, configuredTechniques, overlayShortcut, fxaa, cancellationToken);

    public async Task EnableReShadeAsync(
        TeraPaths paths,
        FileTransaction transaction,
        CancellationToken cancellationToken,
        bool noBlur = false,
        IReadOnlyDictionary<string, bool>? configuredTechniques = null,
        string? overlayShortcut = null,
        string? fxaa = null)
    {
        var files = new GraphicsPipelinePaths(paths);
        var activeKind = GetDllKind(files.ActiveD3D9);
        var keepDxvk = activeKind == "DXVK" ||
            activeKind == "ReShade" && GetDllKind(files.ProxyDxvk) == "DXVK" &&
            IniFile.GetValue(files.ReShadeConfig, "PROXY", "EnableProxyLibrary") == "1";
        await EnableReShadeCoreAsync(paths, transaction, keepDxvk, noBlur, configuredTechniques, overlayShortcut, fxaa, cancellationToken);
    }

    public void DisableReShade(TeraPaths paths, FileTransaction transaction)
    {
        var files = new GraphicsPipelinePaths(paths);
        var state = states.Require(files);
        if (GetDllKind(files.ActiveD3D9) == "ReShade")
        {
            var dxvkEnabled = GetDllKind(files.ProxyDxvk) == "DXVK" &&
                IniFile.GetValue(files.ReShadeConfig, "PROXY", "EnableProxyLibrary") == "1";
            if (dxvkEnabled)
                CopyFile(files.ProxyDxvk, files.ActiveD3D9, transaction);
            else
                RestoreOriginalD3D9(files, state.Dxvk, transaction);
        }

        if (File.Exists(files.ReShadeConfig))
        {
            DeleteFile(files.DisabledConfig, transaction);
            transaction.CaptureFile(files.ReShadeConfig);
            transaction.CaptureFile(files.DisabledConfig);
            File.Move(files.ReShadeConfig, files.DisabledConfig, true);
        }
        transaction.CaptureFile(paths.SystemSettings);
        IniFile.SetValue(paths.SystemSettings, "SystemSettings", "FXAA", state.ReShade.OriginalFxaa);
        var previous64 = registry.Get64();
        var previous32 = registry.Get32();
        try
        {
            SetRegistryLayers(state.ReShade.OriginalVulkanLayer64, state.ReShade.OriginalVulkanLayer32);
            if (GetDllKind(files.ActiveD3D9) == "ReShade")
                throw new InvalidDataException("ReShade deactivation validation failed.");
        }
        catch
        {
            SetRegistryLayers(previous64, previous32);
            throw;
        }
    }

    public async Task EnableDxvkAsync(TeraPaths paths, FileTransaction transaction, CancellationToken cancellationToken)
    {
        await payload.ReadVerifiedBytesAsync(DxvkRuntime, cancellationToken);
        var files = new GraphicsPipelinePaths(paths);
        var state = states.SaveIfMissing(files, transaction);
        if (GetDllKind(files.ActiveD3D9) == "ReShade")
        {
            await EnsureDxvkProxyAsync(files, state.Dxvk, transaction, cancellationToken);
            if (!File.Exists(files.ReShadeConfig))
                throw new InvalidDataException("ReShade is active but its configuration file is missing.");
            transaction.CaptureFile(files.ReShadeConfig);
            IniFile.SetValue(files.ReShadeConfig, "PROXY", "EnableProxyLibrary", "1");
            IniFile.SetValue(files.ReShadeConfig, "PROXY", "ProxyLibrary", @".\d3d9_dxvk.dll");
        }
        else if (GetDllKind(files.ActiveD3D9) != "DXVK")
        {
            if (GetDllKind(files.ProxyDxvk) == "DXVK" &&
                GetHash(files.ProxyDxvk).Equals(state.Dxvk.Sha256, StringComparison.OrdinalIgnoreCase))
                CopyFile(files.ProxyDxvk, files.ActiveD3D9, transaction);
            else
                await payload.CopyFileAsync(DxvkRuntime, files.ActiveD3D9, transaction, cancellationToken);
        }
        AssertDxvkEnabled(files, state.Dxvk);
    }

    public void DisableDxvk(TeraPaths paths, FileTransaction transaction)
    {
        var files = new GraphicsPipelinePaths(paths);
        var state = states.Require(files);
        if (GetDllKind(files.ActiveD3D9) == "ReShade")
        {
            if (!File.Exists(files.ReShadeConfig))
                throw new InvalidDataException("ReShade is active but its configuration file is missing.");
            transaction.CaptureFile(files.ReShadeConfig);
            IniFile.SetValue(files.ReShadeConfig, "PROXY", "EnableProxyLibrary", "0");
        }
        else if (GetDllKind(files.ActiveD3D9) == "DXVK")
        {
            RestoreOriginalD3D9(files, state.Dxvk, transaction);
        }

        var activeDxvk = GetDllKind(files.ActiveD3D9) == "DXVK";
        var proxiedDxvk = GetDllKind(files.ActiveD3D9) == "ReShade" &&
            IniFile.GetValue(files.ReShadeConfig, "PROXY", "EnableProxyLibrary") == "1";
        if ((activeDxvk && !state.Dxvk.OriginalD3D9Kind.Equals("DXVK", StringComparison.OrdinalIgnoreCase)) || proxiedDxvk)
            throw new InvalidDataException("DXVK deactivation validation failed.");
    }

    public void Restore(TeraPaths paths, FileTransaction transaction)
    {
        DisableReShade(paths, transaction);
        DisableDxvk(paths, transaction);
    }

    private async Task EnableReShadeCoreAsync(
        TeraPaths paths,
        FileTransaction transaction,
        bool enableDxvk,
        bool noBlur,
        IReadOnlyDictionary<string, bool>? configuredTechniques,
        string? overlayShortcut,
        string? fxaa,
        CancellationToken cancellationToken)
    {
        await ValidatePayloadAsync(cancellationToken);
        var files = new GraphicsPipelinePaths(paths);
        var state = states.SaveIfMissing(files, transaction);
        if (enableDxvk)
            await EnsureDxvkProxyAsync(files, state.Dxvk, transaction, cancellationToken);

        await payload.CopyFileAsync(ReShadeRuntime, files.ActiveD3D9, transaction, cancellationToken);
        if (File.Exists(files.DisabledConfig))
        {
            transaction.CaptureFile(files.ReShadeConfig);
            transaction.CaptureFile(files.DisabledConfig);
            File.Move(files.DisabledConfig, files.ReShadeConfig, true);
        }
        else if (!File.Exists(files.ReShadeConfig))
        {
            await payload.CopyFileAsync(ReShadeConfigPayload, files.ReShadeConfig, transaction, cancellationToken);
        }
        if (!File.Exists(files.ReShadePreset))
            await payload.CopyFileAsync(ReShadePresetPayload, files.ReShadePreset, transaction, cancellationToken);
        await payload.CopyTreeAsync(
            "reshade/reshade-shaders",
            files.ReShadeShaders,
            transaction,
            overwriteExisting: false,
            cancellationToken: cancellationToken);
        ConfigureEffects(files.ReShadePreset, configuredTechniques, noBlur, transaction);

        transaction.CaptureFile(files.ReShadeConfig);
        if (!string.IsNullOrWhiteSpace(overlayShortcut))
            IniFile.SetValue(files.ReShadeConfig, "INPUT", "KeyOverlay", ValidateOverlayShortcut(overlayShortcut));
        IniFile.SetValue(files.ReShadeConfig, "PROXY", "EnableProxyLibrary", enableDxvk ? "1" : "0");
        IniFile.SetValue(files.ReShadeConfig, "PROXY", "ProxyLibrary", @".\d3d9_dxvk.dll");
        var resolution = displays.GetPrimaryResolution();
        IniFile.SetValue(files.ReShadeConfig, "DEPTH", "FilterResolutionWidth", resolution.Width.ToString());
        IniFile.SetValue(files.ReShadeConfig, "DEPTH", "FilterResolutionHeight", resolution.Height.ToString());
        transaction.CaptureFile(paths.SystemSettings);
        var expectedFxaa = fxaa ?? engineConfiguration.ManagedFxaa;
        IniFile.SetValue(paths.SystemSettings, "SystemSettings", "FXAA", expectedFxaa);
        var previous64 = registry.Get64();
        var previous32 = registry.Get32();
        try
        {
            SetRegistryLayers(1, 1);
            DeleteFile(files.ReShadeLog, transaction);
            AssertReShadeEnabled(paths, state.Dxvk, enableDxvk, overlayShortcut, expectedFxaa);
        }
        catch
        {
            SetRegistryLayers(previous64, previous32);
            throw;
        }
    }

    private async Task EnsureDxvkProxyAsync(
        GraphicsPipelinePaths files,
        DxvkConfiguration state,
        FileTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (GetDllKind(files.ActiveD3D9) == "DXVK")
            CopyFile(files.ActiveD3D9, files.ProxyDxvk, transaction);
        else if (GetDllKind(files.ProxyDxvk) != "DXVK" ||
                 !GetHash(files.ProxyDxvk).Equals(state.Sha256, StringComparison.OrdinalIgnoreCase))
            await payload.CopyFileAsync(DxvkRuntime, files.ProxyDxvk, transaction, cancellationToken);

        if (GetDllKind(files.ProxyDxvk) != "DXVK")
            throw new InvalidDataException("The DXVK proxy DLL is missing or invalid.");
    }

    private static void ConfigureEffects(
        string presetPath,
        IReadOnlyDictionary<string, bool>? configuredTechniques,
        bool noBlur,
        FileTransaction transaction)
    {
        var configured = IniFile.GetPreambleValue(presetPath, "Techniques") ?? string.Empty;
        var techniques = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (configuredTechniques is not null)
        {
            var availableTechniques = (IniFile.GetPreambleValue(presetPath, "TechniqueSorting") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var available = availableTechniques.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requested = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var technique in configuredTechniques)
                requested[technique.Key] = technique.Value;
            var managed = requested.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unsupported = managed.Where(technique => !available.Contains(technique)).ToArray();
            if (unsupported.Length > 0)
                throw new InvalidDataException("Unsupported ReShade technique: " + string.Join(", ", unsupported));

            techniques.RemoveAll(technique => managed.Contains(technique));
            techniques.AddRange(availableTechniques.Where(technique =>
                requested.TryGetValue(technique, out var enabled) && enabled));
            if (noBlur)
                techniques.RemoveAll(technique => BlurTechniques.Contains(technique));
        }
        else
        {
            techniques.RemoveAll(technique => BlurTechniques.Contains(technique));
            if (!noBlur)
                techniques.AddRange(DefaultAtmosphereTechniques);
        }

        transaction.CaptureFile(presetPath);
        IniFile.SetPreambleValue(presetPath, "Techniques", string.Join(',', techniques.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private static string ValidateOverlayShortcut(string shortcut)
    {
        var values = shortcut.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 4 ||
            !int.TryParse(values[0], out var key) || key is < 1 or > 255 ||
            values.Skip(1).Any(value => value is not "0" and not "1"))
            throw new InvalidDataException("The ReShade overlay shortcut is invalid.");
        return string.Join(',', values);
    }

    private void RestoreOriginalD3D9(GraphicsPipelinePaths files, DxvkConfiguration state, FileTransaction transaction)
    {
        if (state.OriginalD3D9Kind.Equals("Missing", StringComparison.OrdinalIgnoreCase))
        {
            DeleteFile(files.ActiveD3D9, transaction);
            return;
        }

        if (state.OriginalD3D9Kind.Equals("DXVK", StringComparison.OrdinalIgnoreCase))
        {
            if (GetDllKind(files.ActiveD3D9) == "DXVK" &&
                GetHash(files.ActiveD3D9).Equals(state.Sha256, StringComparison.OrdinalIgnoreCase))
                return;
            if (GetDllKind(files.ProxyDxvk) != "DXVK" ||
                !GetHash(files.ProxyDxvk).Equals(state.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The original DXVK runtime is unavailable.");
            CopyFile(files.ProxyDxvk, files.ActiveD3D9, transaction);
            return;
        }

        if (string.IsNullOrWhiteSpace(state.OriginalD3D9Backup) ||
            !IsInside(files.OriginalBackupRoot, state.OriginalD3D9Backup) ||
            !File.Exists(state.OriginalD3D9Backup))
            throw new InvalidOperationException("The original D3D9 backup is missing or unsafe; refusing restore.");
        CopyFile(state.OriginalD3D9Backup, files.ActiveD3D9, transaction);
    }

    private void AssertReShadeEnabled(
        TeraPaths paths,
        DxvkConfiguration state,
        bool expectDxvk,
        string? overlayShortcut,
        string expectedFxaa)
    {
        var files = new GraphicsPipelinePaths(paths);
        var issues = new List<string>();
        if (GetDllKind(files.ActiveD3D9) != "ReShade") issues.Add(@"Binaries\d3d9.dll is not ReShade.");
        if (File.Exists(files.ActiveD3D9) && !GetHash(files.ActiveD3D9).Equals(payload.GetSha256(ReShadeRuntime), StringComparison.OrdinalIgnoreCase))
            issues.Add("Installed ReShade hash does not match the embedded runtime.");
        var proxyEnabled = IniFile.GetValue(files.ReShadeConfig, "PROXY", "EnableProxyLibrary") == "1";
        if (proxyEnabled != expectDxvk) issues.Add("ReShade proxy state does not match the requested DXVK state.");
        if (expectDxvk)
        {
            if (GetDllKind(files.ProxyDxvk) != "DXVK") issues.Add(@"Binaries\d3d9_dxvk.dll is not DXVK.");
            else if (!GetHash(files.ProxyDxvk).Equals(state.Sha256, StringComparison.OrdinalIgnoreCase))
                issues.Add("Installed DXVK hash does not match the recorded source runtime.");
            if (!string.Equals(IniFile.GetValue(files.ReShadeConfig, "PROXY", "ProxyLibrary"), @".\d3d9_dxvk.dll", StringComparison.OrdinalIgnoreCase))
                issues.Add("ReShade does not target d3d9_dxvk.dll.");
        }
        var resolution = displays.GetPrimaryResolution();
        if (IniFile.GetValue(files.ReShadeConfig, "DEPTH", "FilterResolutionWidth") != resolution.Width.ToString() ||
            IniFile.GetValue(files.ReShadeConfig, "DEPTH", "FilterResolutionHeight") != resolution.Height.ToString())
            issues.Add("Generic Depth resolution does not match the primary display.");
        if (!string.Equals(IniFile.GetValue(paths.SystemSettings, "SystemSettings", "FXAA"), expectedFxaa, StringComparison.OrdinalIgnoreCase))
            issues.Add("TERA FXAA does not match the engine configuration.");
        if (!File.Exists(files.ReShadeConfig) || !File.Exists(files.ReShadePreset) || !Directory.Exists(files.ReShadeShaders))
            issues.Add("One or more ReShade artifacts are missing.");
        if (!string.IsNullOrWhiteSpace(overlayShortcut) &&
            !string.Equals(IniFile.GetValue(files.ReShadeConfig, "INPUT", "KeyOverlay"), ValidateOverlayShortcut(overlayShortcut), StringComparison.Ordinal))
            issues.Add("ReShade overlay shortcut does not match the selected configuration.");
        if (registry.Get64() is int layer64 && layer64 != 1) issues.Add("The global ReShade Vulkan 64-bit layer was not disabled.");
        if (registry.Get32() is int layer32 && layer32 != 1) issues.Add("The global ReShade Vulkan 32-bit layer was not disabled.");
        if (issues.Count > 0)
            throw new InvalidDataException("ReShade installation validation failed:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", issues));
    }

    private static void AssertDxvkEnabled(GraphicsPipelinePaths files, DxvkConfiguration state)
    {
        var active = GetDllKind(files.ActiveD3D9);
        var enabled = active == "DXVK" || active == "ReShade" &&
            GetDllKind(files.ProxyDxvk) == "DXVK" &&
            IniFile.GetValue(files.ReShadeConfig, "PROXY", "EnableProxyLibrary") == "1";
        var path = active == "DXVK" ? files.ActiveD3D9 : files.ProxyDxvk;
        if (!enabled || !GetHash(path).Equals(state.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DXVK activation validation failed.");
    }

    private void SetRegistryLayers(int? value64, int? value32)
    {
        var previous64 = registry.Get64();
        var previous32 = registry.Get32();
        try
        {
            registry.Set64(value64);
            registry.Set32(value32);
        }
        catch
        {
            registry.Set64(previous64);
            registry.Set32(previous32);
            throw;
        }
    }

}
