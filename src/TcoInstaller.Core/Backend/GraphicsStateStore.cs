using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TcoInstaller.Models;
using static TcoInstaller.Backend.D3D9Files;

namespace TcoInstaller.Backend;

/// <summary>
/// Persists the ReShade and DXVK recovery models separately. Existing combined state files
/// are read and migrated during the next graphics mutation so installed packages remain reversible.
/// </summary>
internal sealed class GraphicsStateStore(
    PayloadStore payload,
    EngineConfigurationService engineConfiguration,
    IVulkanLayerRegistry registry)
{
    private const int CurrentSchema = 1;
    private const string DxvkRuntime = "dxvk/d3d9.dll";
    private const string ReShadeRuntime = "runtime/ReShade64.dll";

    public (ReShadeConfiguration ReShade, DxvkConfiguration Dxvk) SaveIfMissing(
        GraphicsPipelinePaths files,
        FileTransaction transaction)
    {
        var existing = Read(files);
        if (existing is not null)
        {
            if (!File.Exists(files.ReShadeState) || !File.Exists(files.DxvkState))
                Write(files, existing.Value.ReShade, existing.Value.Dxvk, transaction);
            return existing.Value;
        }

        Directory.CreateDirectory(files.ToolsRoot);
        var activeKind = GetDllKind(files.ActiveD3D9);
        var dxvkHash = activeKind == "DXVK"
            ? GetHash(files.ActiveD3D9)
            : GetDllKind(files.ProxyDxvk) == "DXVK"
                ? GetHash(files.ProxyDxvk)
                : payload.GetSha256(DxvkRuntime);

        string? originalBackup = null;
        if (activeKind != "Missing" && activeKind != "DXVK")
        {
            Directory.CreateDirectory(files.OriginalBackupRoot);
            originalBackup = Path.Combine(files.OriginalBackupRoot, "d3d9.original.dll");
            CopyFile(files.ActiveD3D9, originalBackup, transaction);
        }

        var capturedAt = DateTimeOffset.Now;
        var reshade = new ReShadeConfiguration
        {
            Schema = CurrentSchema,
            TeraRoot = files.Tera.Root,
            CapturedAt = capturedAt,
            Sha256 = payload.GetSha256(ReShadeRuntime),
            OriginalFxaa = IniFile.GetValue(files.Tera.SystemSettings, "SystemSettings", "FXAA") ?? engineConfiguration.ManagedFxaa,
            OriginalVulkanLayer64 = registry.Get64(),
            OriginalVulkanLayer32 = registry.Get32()
        };
        var dxvk = new DxvkConfiguration
        {
            Schema = CurrentSchema,
            TeraRoot = files.Tera.Root,
            CapturedAt = capturedAt,
            Sha256 = dxvkHash,
            OriginalD3D9Kind = activeKind,
            OriginalD3D9Backup = originalBackup
        };
        Write(files, reshade, dxvk, transaction);
        return (reshade, dxvk);
    }

    public (ReShadeConfiguration ReShade, DxvkConfiguration Dxvk) Require(GraphicsPipelinePaths files) =>
        Read(files) ?? throw new InvalidOperationException(
            "Graphics state is missing; refusing an unsafe deactivation operation.");

    private static (ReShadeConfiguration ReShade, DxvkConfiguration Dxvk)? Read(GraphicsPipelinePaths files)
    {
        if (File.Exists(files.ReShadeState) || File.Exists(files.DxvkState))
        {
            if (!File.Exists(files.ReShadeState) || !File.Exists(files.DxvkState))
                throw new InvalidDataException("The separated graphics state is incomplete.");
            var reshade = JsonSerializer.Deserialize<ReShadeConfiguration>(File.ReadAllText(files.ReShadeState), JsonOptions)
                ?? throw new InvalidDataException("The ReShade state file is invalid.");
            var dxvk = JsonSerializer.Deserialize<DxvkConfiguration>(File.ReadAllText(files.DxvkState), JsonOptions)
                ?? throw new InvalidDataException("The DXVK state file is invalid.");
            Validate(files, reshade, dxvk);
            return (reshade, dxvk);
        }

        var legacyPath = files.LegacyStates.FirstOrDefault(File.Exists);
        if (legacyPath is null) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
        var root = document.RootElement;
        var legacySchema = ReadNullableInt(root, "Schema");
        if (legacySchema is not 1 and not 2)
            throw new InvalidDataException($"Unsupported legacy graphics state schema: {legacySchema?.ToString() ?? "missing"}.");
        var capturedAt = root.TryGetProperty("CreatedAt", out var createdAt)
            ? createdAt.GetDateTimeOffset()
            : DateTimeOffset.MinValue;
        var reshadeLegacy = new ReShadeConfiguration
        {
            Schema = CurrentSchema,
            TeraRoot = ReadString(root, "TeraRoot"),
            CapturedAt = capturedAt,
            Sha256 = ReadString(root, "ReShadeSha256"),
            OriginalFxaa = ReadString(root, "OriginalFxaa"),
            OriginalVulkanLayer64 = ReadNullableInt(root, "VulkanLayer64"),
            OriginalVulkanLayer32 = ReadNullableInt(root, "VulkanLayer32")
        };
        var originalD3D9Kind = ReadString(root, "OriginalD3D9Kind");
        if (string.IsNullOrWhiteSpace(originalD3D9Kind))
        {
            // Schema-1 proxy state was created only after DXVK was installed and restored DXVK by default.
            originalD3D9Kind = "DXVK";
        }
        var dxvkLegacy = new DxvkConfiguration
        {
            Schema = CurrentSchema,
            TeraRoot = reshadeLegacy.TeraRoot,
            CapturedAt = capturedAt,
            Sha256 = ReadString(root, "DxvkSha256"),
            OriginalD3D9Kind = originalD3D9Kind,
            OriginalD3D9Backup = ReadNullableString(root, "OriginalD3D9Backup")
        };
        Validate(files, reshadeLegacy, dxvkLegacy);
        return (reshadeLegacy, dxvkLegacy);
    }

    private static void Write(
        GraphicsPipelinePaths files,
        ReShadeConfiguration reshade,
        DxvkConfiguration dxvk,
        FileTransaction transaction)
    {
        transaction.CaptureFile(files.ReShadeState);
        transaction.CaptureFile(files.DxvkState);
        File.WriteAllText(files.ReShadeState, JsonSerializer.Serialize(reshade, JsonOptions), new UTF8Encoding(false));
        File.WriteAllText(files.DxvkState, JsonSerializer.Serialize(dxvk, JsonOptions), new UTF8Encoding(false));
    }

    private static void Validate(
        GraphicsPipelinePaths files,
        ReShadeConfiguration reshade,
        DxvkConfiguration dxvk)
    {
        var issues = new List<string>();
        if (reshade.Schema != CurrentSchema) issues.Add($"ReShade schema is {reshade.Schema}, expected {CurrentSchema}.");
        if (dxvk.Schema != CurrentSchema) issues.Add($"DXVK schema is {dxvk.Schema}, expected {CurrentSchema}.");
        if (!PathMatches(reshade.TeraRoot, files.Tera.Root)) issues.Add("ReShade TERA root does not match the selected installation.");
        if (!PathMatches(dxvk.TeraRoot, files.Tera.Root)) issues.Add("DXVK TERA root does not match the selected installation.");
        if (!IsSha256(reshade.Sha256)) issues.Add("ReShade SHA-256 is missing or invalid.");
        if (!IsSha256(dxvk.Sha256)) issues.Add("DXVK SHA-256 is missing or invalid.");
        if (string.IsNullOrWhiteSpace(dxvk.OriginalD3D9Kind)) issues.Add("Original D3D9 kind is missing.");
        if (issues.Count > 0)
            throw new InvalidDataException("The graphics state files failed validation:" + Environment.NewLine +
                " - " + string.Join(Environment.NewLine + " - ", issues));
    }

    private static string ReadString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? ReadNullableString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadNullableInt(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static bool PathMatches(string candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            return TeraPaths.NormalizeRoot(candidate)
                .Equals(TeraPaths.NormalizeRoot(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };
}
