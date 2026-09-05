using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Media;
using TcoInstaller.Contracts;
using TcoInstaller.Backend;
using TcoInstaller.Models;
using TcoInstaller.Services;

namespace TcoInstaller.ViewModels;

/// <summary>Owns presentation state and translates typed installer progress into visible phase state.</summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private const int MaximumLogCharacters = 80_000;
    private string _teraRoot = string.Empty;
    private EngineConfiguration _selectedEngineConfiguration = null!;
    private bool _includeClassicPlus;
    private bool _pcOnly;
    private bool _noBlur;
    private string? _customEngineConfigurationPath;
    private string? _customEngineConfigurationName;
    private string _selectedReShadeShortcut = "Shift + F12";
    private bool _isRunning;
    private string _statusText = "Ready";
    private string _logText = string.Empty;
    private string? _lastLogPath;
    private string? _lastReportPath;
    private InstallationSnapshot? _snapshot;

    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#6DD6A0"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F0C36A"));
    private static readonly IBrush MissingBrush = new SolidColorBrush(Color.Parse("#FF7B8B"));

    public MainWindowViewModel(
        IReadOnlyList<EngineConfiguration> engineConfigurations,
        string defaultEngineConfigurationId)
    {
        IsWindows = OperatingSystem.IsWindows();
        TeraRoot = PackageLocator.DetectTeraRoot() ?? string.Empty;
        EngineConfigurations = engineConfigurations;
        SelectedEngineConfiguration = EngineConfigurations.First(configuration => configuration.Id == defaultEngineConfigurationId);
        EssentialReShadeEffects =
        [
            Effect("Directionally_Localized_Anti_Aliasing@DLAA_Plus.fx", "DLAA Plus", "Smooths edges and reduces distant shimmering.", true),
            Effect("SMAA@SMAA.fx", "SMAA", "Alternative edge smoothing with low performance cost.", false),
            Effect("NFAA@NFAA.fx", "NFAA", "Alternative smoothing for thin and diagonal edges.", false),
            Effect("TAA@Temporal_AA.fx", "Temporal AA", "Experimental frame-based smoothing for moving shimmer.", false),
            Effect("Deband@Deband.fx", "Deband", "Reduces visible color bands.", true),
            Effect("Tonemap@Tonemap.fx", "Tonemap", "Balances brightness and color.", true),
            Effect("ContrastAdaptiveSharpen@CAS.fx", "CAS sharpening", "Restores fine image detail.", true),
            Effect("prod80_02_Bloom@PD80_02_Bloom.fx", "Bloom", "Adds a soft glow to bright areas.", true),
            Effect("prod80_03_FilmicTonemap@PD80_03_Filmic_Adaptation.fx", "Filmic adaptation", "Softens harsh light transitions.", true),
            Effect("prod80_03_Shadows_Midtones_Highlights@PD80_03_Shadows_Midtones_Highlights.fx", "Light balance", "Tunes shadows, midtones, and highlights.", true)
        ];
        DepthReShadeEffects =
        [
            Effect("DepthHaze@DepthHaze.fx", "Depth atmosphere", "Adds haze to distant scenery.", true, true),
            Effect("CinematicDOF@CinematicDOF.fx", "Distance blur", "Blurs the far background.", false, true),
            Effect("DisplayDepth@DisplayDepth.fx", "Depth preview", "Shows the depth buffer for troubleshooting.", false),
            Effect("MXAO@qUINT_mxao.fx", "MXAO", "Adds contact shadows using depth.", true),
            Effect("ADOF@qUINT_dof.fx", "Adaptive depth of field", "Adds a stronger cinematic focus effect.", false, true),
            Effect("LinearMotionBlur@LinearMotionBlur.fx", "Motion blur", "Adds blur while the image moves.", false, true),
            Effect("DRME@MotionEstimation.fx", "Motion estimation", "Builds motion data for advanced effects.", false),
            Effect("SSR@qUINT_ssr.fx", "Screen-space reflections", "Adds approximate reflections using depth.", false)
        ];
        ColorReShadeEffects =
        [
            Effect("Bloom@qUINT_bloom.fx", "qUINT bloom", "Alternative bloom effect.", false),
            Effect("Debanding@qUINT_deband.fx", "qUINT debanding", "Alternative color-band cleanup.", false),
            Effect("DELC_Sharpen@qUINT_sharp.fx", "DELC sharpening", "Alternative detail sharpening.", false),
            Effect("Lightroom@qUINT_lightroom.fx", "Lightroom", "Advanced color correction.", false),
            Effect("LUT@LUT.fx", "Color LUT", "Applies a color lookup table.", false),
            Effect("prod80_01_Color_Gamut@PD80_01_Color_Gamut.fx", "Color gamut", "Changes the available color range.", false),
            Effect("prod80_04_ColorBalance@PD80_04_Color_Balance.fx", "Color balance", "Adjusts red, green, and blue balance.", false),
            Effect("prod80_04_ColorTemperature@PD80_04_Color_Temperature.fx", "Color temperature", "Makes the image warmer or cooler.", false),
            Effect("prod80_04_ContrastBrightnessSaturation@PD80_04_Contrast_Brightness_Saturation.fx", "Contrast and saturation", "Adjusts overall image strength.", false),
            Effect("prod80_05_LumaSharpen@PD80_05_Sharpening.fx", "Luma sharpening", "Sharpens brightness detail.", false),
            Effect("prod80_06_ChromaticAberration@PD80_06_Chromatic_Aberration.fx", "Chromatic aberration", "Adds colored lens edges.", false),
            Effect("prod80_06_FilmGrain@PD80_06_Film_Grain.fx", "Film grain", "Adds a fine cinematic grain.", false)
        ];
        ReadmeText = LoadReadme();

        Phases = new ObservableCollection<PhaseItem>
        {
            new("preflight", "Preflight"),
            new("update", "Update"),
            new("engine", "Engine"),
            new("graphics", "Graphics"),
            new("classicplus", "Classic+"),
            new("verification", "Verify")
        };

        if (!IsWindows)
            StatusText = "The TCO backend requires Windows";
    }

    public ObservableCollection<PhaseItem> Phases { get; }
    public bool IsWindows { get; }
    public IReadOnlyList<EngineConfiguration> EngineConfigurations { get; }
    public IReadOnlyList<ReShadeEffectOption> EssentialReShadeEffects { get; }
    public IReadOnlyList<ReShadeEffectOption> DepthReShadeEffects { get; }
    public IReadOnlyList<ReShadeEffectOption> ColorReShadeEffects { get; }
    public IReadOnlyList<string> ReShadeShortcuts { get; } = ShortcutValues.Keys.ToArray();
    public string ReadmeText { get; }

    public string TeraRoot
    {
        get => _teraRoot;
        set
        {
            if (SetField(ref _teraRoot, value))
                OnPropertyChanged(nameof(CanRun));
        }
    }

    public EngineConfiguration SelectedEngineConfiguration
    {
        get => _selectedEngineConfiguration;
        set
        {
            if (SetField(ref _selectedEngineConfiguration, value))
                OnPropertyChanged(nameof(EngineProfileSummary));
        }
    }

    public string? CustomEngineConfigurationPath
    {
        get => _customEngineConfigurationPath;
        private set
        {
            if (!SetField(ref _customEngineConfigurationPath, value))
                return;
            OnPropertyChanged(nameof(HasCustomEngineConfiguration));
            OnPropertyChanged(nameof(EngineProfileSummary));
            OnPropertyChanged(nameof(CanSelectBuiltInEngineConfiguration));
        }
    }

    public bool HasCustomEngineConfiguration => !string.IsNullOrWhiteSpace(CustomEngineConfigurationPath);
    public bool CanSelectBuiltInEngineConfiguration => CanEdit && !HasCustomEngineConfiguration;
    public string EngineProfileSummary => HasCustomEngineConfiguration
        ? $"Custom profile: {_customEngineConfigurationName ?? Path.GetFileNameWithoutExtension(CustomEngineConfigurationPath)}"
        : SelectedEngineConfiguration.Description;

    public string SelectedReShadeShortcut
    {
        get => _selectedReShadeShortcut;
        set => SetField(ref _selectedReShadeShortcut, value);
    }

    public bool IncludeClassicPlus
    {
        get => _includeClassicPlus;
        set => SetField(ref _includeClassicPlus, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CanSelectBuiltInEngineConfiguration));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetField(ref _logText, value);
    }

    public string? LastLogPath
    {
        get => _lastLogPath;
        private set
        {
            if (SetField(ref _lastLogPath, value))
                OnPropertyChanged(nameof(HasLog));
        }
    }

    public bool CanRun =>
        !IsRunning &&
        IsWindows &&
        PackageLocator.LooksLikeTeraRoot(TeraRoot);

    public bool CanEdit => !IsRunning;
    public bool HasLog => !string.IsNullOrWhiteSpace(LastLogPath) && File.Exists(LastLogPath);

    public string? LastReportPath
    {
        get => _lastReportPath;
        private set
        {
            if (SetField(ref _lastReportPath, value))
                OnPropertyChanged(nameof(HasReport));
        }
    }

    public bool PcOnly
    {
        get => _pcOnly;
        set => SetField(ref _pcOnly, value);
    }

    public bool NoBlur
    {
        get => _noBlur;
        set
        {
            if (_noBlur == value)
                return;

            foreach (var effect in AllReShadeEffects.Where(effect => effect.IsBlur))
                effect.IsAvailable = !value;

            SetField(ref _noBlur, value);
        }
    }

    public bool HasReport => !string.IsNullOrWhiteSpace(LastReportPath) && File.Exists(LastReportPath);

    public InstallationSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (!SetField(ref _snapshot, value))
                return;
            if (value?.ReShade.PresetInstalled == true)
            {
                NoBlur = false;
                SetEnabledTechniques(value.ReShade.EnabledTechniques, preserveBlur: value.ReShade.NoBlur);
                NoBlur = value.ReShade.NoBlur;
                if (ShortcutNames.TryGetValue(value.ReShade.OverlayShortcut, out var shortcut))
                    SelectedReShadeShortcut = shortcut;
            }
            OnPropertyChanged(nameof(HasSnapshot));
            OnPropertyChanged(nameof(PipelineSummary));
            OnPropertyChanged(nameof(EngineDetection));
            OnPropertyChanged(nameof(ReShadeDetection));
            OnPropertyChanged(nameof(DxvkDetection));
            OnPropertyChanged(nameof(TccDetection));
            OnPropertyChanged(nameof(ShinraDetection));
            OnPropertyChanged(nameof(EngineDetectionBrush));
            OnPropertyChanged(nameof(ReShadeDetectionBrush));
            OnPropertyChanged(nameof(DxvkDetectionBrush));
            OnPropertyChanged(nameof(TccDetectionBrush));
            OnPropertyChanged(nameof(ShinraDetectionBrush));
        }
    }

    public bool HasSnapshot => Snapshot is not null;
    public string PipelineSummary => Snapshot?.ConfiguredPipeline ?? string.Empty;

    public string EngineDetection => Snapshot is null
        ? string.Empty
        : EngineApplied
            ? $"Applied · {Snapshot.Engine.Name}{(Snapshot.Engine.PcOnly ? " · PC Only" : string.Empty)}"
            : $"Not applied · closest match {Snapshot.Engine.ChecksPassed}/{Snapshot.Engine.ChecksTotal}";
    public string ReShadeDetection => Snapshot?.ReShade.Active == true
        ? "Active"
        : ReShadeDetected ? "Installed, inactive" : "Not detected";
    public string DxvkDetection => Snapshot?.Dxvk.Active == true
        ? "Active"
        : DxvkDetected ? "Installed, inactive" : "Not detected";
    public string TccDetection => Snapshot?.ClassicPlus.TccInstalled == true ? "Detected" : "Not detected";
    public string ShinraDetection => Snapshot?.ClassicPlus.ShinraInstalled == true ? "Detected" : "Not detected";

    public IBrush EngineDetectionBrush => EngineApplied ? SuccessBrush : MissingBrush;
    public IBrush ReShadeDetectionBrush => Snapshot?.ReShade.Active == true ? SuccessBrush : ReShadeDetected ? WarningBrush : MissingBrush;
    public IBrush DxvkDetectionBrush => Snapshot?.Dxvk.Active == true ? SuccessBrush : DxvkDetected ? WarningBrush : MissingBrush;
    public IBrush TccDetectionBrush => Snapshot?.ClassicPlus.TccInstalled == true ? SuccessBrush : MissingBrush;
    public IBrush ShinraDetectionBrush => Snapshot?.ClassicPlus.ShinraInstalled == true ? SuccessBrush : MissingBrush;

    private bool EngineApplied => Snapshot is not null && Snapshot.Engine.ChecksTotal > 0 &&
        Snapshot.Engine.ChecksPassed == Snapshot.Engine.ChecksTotal;
    private bool ReShadeDetected => Snapshot is not null && (Snapshot.ReShade.Active || Snapshot.ReShade.PresetInstalled ||
        Snapshot.ReShade.ShadersInstalled || Snapshot.ReShade.RuntimeConfirmed);
    private bool DxvkDetected => Snapshot is not null && (Snapshot.Dxvk.Active || Snapshot.Dxvk.RuntimeConfirmed ||
        Snapshot.Dxvk.ProxyTarget.Equals("DXVK", StringComparison.OrdinalIgnoreCase));

    public InstallerRequest CreateRequest(InstallerAction action) => new(
        action,
        TeraRoot,
        action == InstallerAction.Apply && IncludeClassicPlus,
        action == InstallerAction.Apply,
        SelectedEngineConfiguration.Id,
        PcOnly,
        NoBlur,
        CustomEngineConfigurationPath,
        AllReShadeEffects.ToDictionary(effect => effect.Technique, effect => effect.IsEnabled, StringComparer.OrdinalIgnoreCase),
        ShortcutValues[SelectedReShadeShortcut]);

    public void ApplyRequest(InstallerRequest request)
    {
        TeraRoot = request.TeraRoot;
        IncludeClassicPlus = request.IncludeClassicPlus;
        PcOnly = request.PcOnly;
        NoBlur = request.NoBlur;
        if (!string.IsNullOrWhiteSpace(request.CustomEngineConfigurationPath))
        {
            CustomEngineConfigurationPath = request.CustomEngineConfigurationPath;
            _customEngineConfigurationName = Path.GetFileNameWithoutExtension(request.CustomEngineConfigurationPath);
            OnPropertyChanged(nameof(EngineProfileSummary));
        }
        if (request.ReShadeTechniques is not null)
            SetConfiguredTechniques(request.ReShadeTechniques);
        if (request.ReShadeOverlayShortcut is not null && ShortcutNames.TryGetValue(request.ReShadeOverlayShortcut, out var shortcut))
            SelectedReShadeShortcut = shortcut;
        SelectedEngineConfiguration = EngineConfigurations.FirstOrDefault(configuration =>
            configuration.Id.Equals(request.EngineConfigurationId, StringComparison.OrdinalIgnoreCase)) ?? SelectedEngineConfiguration;
    }

    public void SetCustomEngineConfiguration(string path, EngineConfiguration configuration)
    {
        CustomEngineConfigurationPath = Path.GetFullPath(path);
        _customEngineConfigurationName = configuration.Name;
        OnPropertyChanged(nameof(EngineProfileSummary));
    }

    public void ClearCustomEngineConfiguration()
    {
        CustomEngineConfigurationPath = null;
        _customEngineConfigurationName = null;
        OnPropertyChanged(nameof(EngineProfileSummary));
    }

    public void BeginRun()
    {
        foreach (var phase in Phases)
            phase.State = PhaseState.Pending;

        LogText = string.Empty;
        LastLogPath = null;
        LastReportPath = null;
        StatusText = "Starting installer…";
        IsRunning = true;
    }

    public void HandleOutput(InstallerProgress output) => HandleEvent(output);

    public void CompleteRun(InstallerRunResult result)
    {
        LastLogPath = result.LogPath;
        LastReportPath = result.ReportPath;
        Snapshot = result.Snapshot;
        IsRunning = false;
        StatusText = result.ReportPath is not null
            ? "Scan complete · report written"
            : result.ExitCode == 0
            ? "Completed successfully"
            : $"Failed with exit code {result.ExitCode}";

        if (result.ExitCode != 0)
        {
            var active = Phases.FirstOrDefault(phase => phase.State == PhaseState.Active);
            if (active is not null)
                active.State = PhaseState.Failed;
        }
    }

    public void FailRun(string message)
    {
        AppendLog(message, true);
        IsRunning = false;
        StatusText = "Installer failed to start";
        var active = Phases.FirstOrDefault(phase => phase.State == PhaseState.Active);
        if (active is not null)
            active.State = PhaseState.Failed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void HandleEvent(InstallerProgress installerEvent)
    {
        AppendLog(installerEvent.Message, installerEvent.IsError || installerEvent.Status == "failed");

        var phase = Phases.FirstOrDefault(item =>
            string.Equals(item.Id, installerEvent.Phase, StringComparison.OrdinalIgnoreCase));
        if (phase is null)
            return;

        phase.State = installerEvent.Status.ToLowerInvariant() switch
        {
            "started" => PhaseState.Active,
            "completed" or "skipped" => PhaseState.Complete,
            "failed" => PhaseState.Failed,
            _ => phase.State
        };
        StatusText = installerEvent.Message;
    }

    private void AppendLog(string line, bool isError)
    {
        var prefix = isError ? "[error] " : string.Empty;
        var updated = LogText + prefix + line + Environment.NewLine;
        if (updated.Length > MaximumLogCharacters)
            updated = updated[^MaximumLogCharacters..];
        LogText = updated;
    }

    private static string LoadReadme()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tco.Content/README.md")
            ?? throw new FileNotFoundException("The embedded README is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    private IEnumerable<ReShadeEffectOption> AllReShadeEffects =>
        EssentialReShadeEffects.Concat(DepthReShadeEffects).Concat(ColorReShadeEffects);

    private void SetEnabledTechniques(IEnumerable<string> techniques, bool preserveBlur = false)
    {
        var enabled = techniques.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var effect in AllReShadeEffects)
            if (!preserveBlur || !effect.IsBlur)
                effect.IsEnabled = enabled.Contains(effect.Technique);
    }

    private void SetConfiguredTechniques(IReadOnlyDictionary<string, bool> techniques)
    {
        foreach (var effect in AllReShadeEffects)
            if (techniques.TryGetValue(effect.Technique, out var enabled))
                effect.IsEnabled = enabled;
    }

    private static ReShadeEffectOption Effect(string technique, string name, string description, bool enabled, bool isBlur = false) =>
        new(technique, name, description, enabled, isBlur);

    private static readonly IReadOnlyDictionary<string, string> ShortcutValues = new Dictionary<string, string>
    {
        ["Shift + F12"] = "123,0,1,0",
        ["Shift + F2"] = "113,0,1,0",
        ["F10"] = "121,0,0,0",
        ["Insert"] = "45,0,0,0",
        ["Home"] = "36,0,0,0"
    };

    private static readonly IReadOnlyDictionary<string, string> ShortcutNames =
        ShortcutValues.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
