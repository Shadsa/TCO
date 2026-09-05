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
    private const string FrameRateFile = "S1Engine.ini";
    private const string FrameRateSection = "Engine.Engine";
    private const string FrameRateKey = "MaxSmoothedFrameRate";

    private readonly EngineBackupStore _backups = new();
    private readonly IReadOnlyDictionary<string, EngineConfiguration> _configurations;
    private readonly IDisplayResolutionService _displays;

    public EngineConfigurationService(PayloadStore payload, IDisplayResolutionService? displays = null)
    {
        _displays = displays ?? new DisplayResolutionService();
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

    public EngineConfiguration Resolve(string? configurationId, string? customConfigurationPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customConfigurationPath))
            return LoadCustom(customConfigurationPath);

        return _configurations.TryGetValue(configurationId ?? DefaultConfigurationId, out var profile)
            ? profile
            : throw new InvalidDataException($"Unknown engine preset: {configurationId}");
    }

    /// <summary>Loads a user-selected JSON profile and applies the same safety checks as embedded profiles.</summary>
    public EngineConfiguration LoadCustom(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The custom engine profile does not exist.", fullPath);
        if (!Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A custom engine profile must be a JSON file.");
        if (new FileInfo(fullPath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("The custom engine profile is unexpectedly large.");

        EngineConfiguration profile;
        try
        {
            profile = JsonSerializer.Deserialize<EngineConfiguration>(File.ReadAllText(fullPath), JsonOptions)
                ?? throw new InvalidDataException("The custom engine profile is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The custom engine profile is not valid JSON.", exception);
        }

        Validate(profile);
        return profile with { IsDefault = false };
    }

    public int GetCount(string? configurationId) =>
        GetEntries(Resolve(configurationId)).Count(entry => !IsFrameRateCap(entry.File, entry.Section, entry.Key)) + 1;

    public int GetCount(EngineConfiguration profile) =>
        GetEntries(profile).Count(entry => !IsFrameRateCap(entry.File, entry.Section, entry.Key)) + 1;

    public string GetManagedFxaa(EngineConfiguration profile) =>
        GetEntries(profile)
            .Where(entry => entry.File.Equals("S1SystemSettings.ini", StringComparison.OrdinalIgnoreCase) &&
                            entry.Section.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase) &&
                            entry.Key.Equals("FXAA", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value)
            .SingleOrDefault() ?? ManagedFxaa;

    /// <summary>Captures the original INIs once, then updates only keys owned by the selected preset.</summary>
    public void Apply(TeraPaths paths, FileTransaction transaction, string? configurationId = null, bool pcOnly = false)
        => Apply(paths, transaction, Resolve(configurationId), pcOnly);

    public void Apply(TeraPaths paths, FileTransaction transaction, EngineConfiguration profile, bool pcOnly = false)
    {
        paths.Validate();
        Validate(profile);
        _backups.SaveIfMissing(paths, transaction);
        var refreshRate = _displays.GetPrimaryResolution().RefreshRateHz;
        var textureFingerprint = IniFile.GetTextureGroupFingerprint(paths.SystemSettings);

        foreach (var file in profile.Files)
        {
            if (!paths.EngineFiles.TryGetValue(file.Key, out var path))
                throw new InvalidDataException($"Unsupported engine preset file: {file.Key}");
            transaction.CaptureFile(path);
            foreach (var section in file.Value)
                IniFile.SetValues(path, section.Key, section.Value);
        }

        transaction.CaptureFile(paths.S1Engine);
        IniFile.SetValue(paths.S1Engine, FrameRateSection, FrameRateKey, refreshRate.ToString());

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
    public EngineConfiguration Inspect(TeraPaths paths, EngineConfiguration? customProfile = null)
    {
        var refreshRate = _displays.GetPrimaryResolution().RefreshRateHz;
        if (customProfile is not null)
            Validate(customProfile);
        IEnumerable<EngineConfiguration> candidates = customProfile is null
            ? Configurations
            : [customProfile, .. Configurations];
        var match = candidates
            .Select(profile => (Profile: profile, Mismatches: GetMismatches(paths, profile, refreshRate)))
            .OrderBy(candidate => candidate.Mismatches.Count)
            .First();
        var total = GetManagedEntries(match.Profile, refreshRate).Count();
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
            FpsCap = IniFile.GetValue(paths.S1Engine, FrameRateSection, FrameRateKey) ?? string.Empty,
            MonitorRefreshRateHz = refreshRate,
            PcOnly = IniFile.GetValue(paths.S1Engine, "WinDrv.WindowsClient", "AllowJoystickInput") == "0",
            ConfigsLocked = locks.Values.All(value => value),
            Mismatches = match.Mismatches,
            ConfigLocks = locks
        };
    }

    public Dictionary<string, string> GetMismatches(TeraPaths paths, string? configurationId = null)
    {
        var profile = Resolve(configurationId);
        return GetMismatches(paths, profile, _displays.GetPrimaryResolution().RefreshRateHz);
    }

    private static Dictionary<string, string> GetMismatches(TeraPaths paths, EngineConfiguration profile, int refreshRate)
    {
        var mismatches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in GetManagedEntries(profile, refreshRate))
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

    private static IEnumerable<(string File, string Section, string Key, string Value)> GetManagedEntries(
        EngineConfiguration profile,
        int refreshRate) =>
        GetEntries(profile)
            .Where(entry => !IsFrameRateCap(entry.File, entry.Section, entry.Key))
            .Append((FrameRateFile, FrameRateSection, FrameRateKey, refreshRate.ToString()));

    private static bool IsFrameRateCap(string file, string section, string key) =>
        file.Equals(FrameRateFile, StringComparison.OrdinalIgnoreCase) &&
        section.Equals(FrameRateSection, StringComparison.OrdinalIgnoreCase) &&
        key.Equals(FrameRateKey, StringComparison.OrdinalIgnoreCase);

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
