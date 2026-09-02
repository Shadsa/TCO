using System.Text.Json;
using TcoInstaller.Models;

namespace TcoInstaller.Backend;

/// <summary>
/// Loads complete engine presets, applies their INI values, and inspects the closest installed preset.
/// Presets are data-only JSON resources; this service contains the validation and mutation workflow.
/// </summary>
public sealed class EngineConfigurationService
{
    public const string PresetFolder = "EngineConfigurationPResets";

    private readonly EngineBackupStore _backups = new();
    private readonly IReadOnlyDictionary<string, EngineConfiguration> _configurations;

    public EngineConfigurationService(PayloadStore payload)
    {
        var presets = payload.Files
            .Where(path => path.StartsWith(PresetFolder + "/", StringComparison.OrdinalIgnoreCase) &&
                           path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(path => JsonSerializer.Deserialize<EngineConfiguration>(payload.ReadText(path), JsonOptions)
                ?? throw new InvalidDataException($"Engine preset is invalid: {path}"))
            .ToArray();

        if (presets.Length == 0)
            throw new InvalidDataException($"No engine presets were found in {PresetFolder}.");
        foreach (var preset in presets) Validate(preset);
        if (presets.Count(profile => profile.IsDefault) != 1)
            throw new InvalidDataException("Exactly one engine preset must be marked as default.");

        try
        {
            _configurations = presets.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Engine preset ids must be unique.", exception);
        }

        Configurations = presets;
        DefaultConfigurationId = presets.Single(profile => profile.IsDefault).Id;
        ManagedFxaa = GetEntries(Resolve(null)).Single(entry =>
            entry.File.Equals("S1SystemSettings.ini", StringComparison.OrdinalIgnoreCase) &&
            entry.Section.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase) &&
            entry.Key.Equals("FXAA", StringComparison.OrdinalIgnoreCase)).Value;
    }

    public string ManagedFxaa { get; }
    public string DefaultConfigurationId { get; }
    public IReadOnlyList<EngineConfiguration> Configurations { get; }

    public EngineConfiguration Resolve(string? configurationId) =>
        _configurations.TryGetValue(configurationId ?? DefaultConfigurationId, out var profile)
            ? profile
            : throw new InvalidDataException($"Unknown engine preset: {configurationId}");

    public int GetCount(string? configurationId) => GetEntries(Resolve(configurationId)).Count();

    /// <summary>Captures the original INIs once, then updates only keys owned by the selected preset.</summary>
    public void Apply(TeraPaths paths, FileTransaction transaction, string? configurationId = null, bool pcOnly = false)
    {
        paths.Validate();
        _backups.SaveIfMissing(paths, transaction);
        var profile = Resolve(configurationId);
        var textureFingerprint = IniFile.GetTextureGroupFingerprint(paths.SystemSettings);

        foreach (var file in profile.Files)
        {
            if (!paths.EngineFiles.TryGetValue(file.Key, out var path))
                throw new InvalidDataException($"Unsupported engine preset file: {file.Key}");
            transaction.CaptureFile(path);
            foreach (var section in file.Value)
                IniFile.SetValues(path, section.Key, section.Value);
        }

        // This user preference intentionally stays outside presets so it can accompany either profile.
        transaction.CaptureFile(paths.S1Engine);
        IniFile.SetValue(paths.S1Engine, "WinDrv.WindowsClient", "AllowJoystickInput", pcOnly ? "0" : "1");

        if (!textureFingerprint.Equals(IniFile.GetTextureGroupFingerprint(paths.SystemSettings), StringComparison.Ordinal))
            throw new InvalidDataException("Texture-group integrity check failed. A TEXTUREGROUP entry changed unexpectedly.");
    }

    /// <summary>Restores the validated pre-TCO snapshot without guessing missing files.</summary>
    public void RestoreOriginal(TeraPaths paths, FileTransaction transaction)
    {
        paths.Validate();
        _backups.Restore(paths, transaction);
    }

    public bool HasValidBackup(TeraPaths paths) => _backups.IsValid(paths);

    /// <summary>Returns the closest preset with live health, lock, and mismatch fields populated.</summary>
    public EngineConfiguration Inspect(TeraPaths paths)
    {
        var match = Configurations
            .Select(profile => (Profile: profile, Mismatches: GetMismatches(paths, profile.Id)))
            .OrderBy(candidate => candidate.Mismatches.Count)
            .First();
        var total = GetEntries(match.Profile).Count();
        var locks = paths.ConfigFiles.ToDictionary(
            path => Path.GetFileName(path) ?? throw new InvalidDataException($"Configuration path has no filename: {path}"),
            path => (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0,
            StringComparer.OrdinalIgnoreCase);

        return match.Profile with
        {
            BackupAvailable = HasValidBackup(paths),
            ChecksPassed = total - match.Mismatches.Count,
            ChecksTotal = total,
            TexturePoolMb = IniFile.GetValue(paths.S1Engine, "TextureStreaming", "PoolSize") ?? string.Empty,
            Fxaa = IniFile.GetValue(paths.SystemSettings, "SystemSettings", "FXAA") ?? string.Empty,
            PcOnly = IniFile.GetValue(paths.S1Engine, "WinDrv.WindowsClient", "AllowJoystickInput") == "0",
            ConfigsLocked = locks.Values.All(value => value),
            Mismatches = match.Mismatches,
            ConfigLocks = locks
        };
    }

    public Dictionary<string, string> GetMismatches(TeraPaths paths, string? configurationId = null)
    {
        var mismatches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in GetEntries(Resolve(configurationId)))
        {
            var actual = IniFile.GetValue(paths.EngineFiles[entry.File], entry.Section, entry.Key) ?? "<missing>";
            if (!actual.Equals(entry.Value, StringComparison.OrdinalIgnoreCase))
                mismatches[$"{entry.File} [{entry.Section}] {entry.Key}"] = $"expected {entry.Value}, actual {actual}";
        }
        return mismatches;
    }

    private static IEnumerable<(string File, string Section, string Key, string Value)> GetEntries(EngineConfiguration profile) =>
        from file in profile.Files
        from section in file.Value
        from setting in section.Value
        select (file.Key, section.Key, setting.Key, setting.Value);

    private static void Validate(EngineConfiguration profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) ||
            profile.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException($"Invalid engine preset id: {profile.Id}");
        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Description) || profile.Files.Count == 0)
            throw new InvalidDataException($"Engine preset is incomplete: {profile.Id}");

        var allowedFiles = new HashSet<string>(
            ["S1Engine.ini", "S1SystemSettings.ini", "S1Option.ini", "BaseInput.ini", "S1Input.ini"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in profile.Files)
        {
            if (!allowedFiles.Contains(file.Key) || file.Value.Count == 0)
                throw new InvalidDataException($"Unsupported or empty engine preset file: {file.Key}");
            foreach (var section in file.Value)
            {
                if (string.IsNullOrWhiteSpace(section.Key) || section.Key.IndexOfAny(['[', ']', '\r', '\n']) >= 0 || section.Value.Count == 0)
                    throw new InvalidDataException($"Invalid INI section in {file.Key}: {section.Key}");
                foreach (var setting in section.Value)
                    if (string.IsNullOrWhiteSpace(setting.Key) || setting.Key.IndexOfAny(['=', '\r', '\n']) >= 0 ||
                        setting.Value.Contains('\r') || setting.Value.Contains('\n'))
                        throw new InvalidDataException($"Invalid INI setting in {file.Key} [{section.Key}]: {setting.Key}");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
