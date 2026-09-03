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
        set => SetField(ref _selectedEngineConfiguration, value);
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
        set => SetField(ref _noBlur, value);
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
                NoBlur = value.ReShade.NoBlur;
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
        NoBlur);

    public void ApplyRequest(InstallerRequest request)
    {
        TeraRoot = request.TeraRoot;
        IncludeClassicPlus = request.IncludeClassicPlus;
        PcOnly = request.PcOnly;
        NoBlur = request.NoBlur;
        SelectedEngineConfiguration = EngineConfigurations.FirstOrDefault(configuration =>
            configuration.Id.Equals(request.EngineConfigurationId, StringComparison.OrdinalIgnoreCase)) ?? SelectedEngineConfiguration;
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
