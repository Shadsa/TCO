using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TcoInstaller.Models;
using TcoInstaller.Services;

namespace TcoInstaller.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private const int MaximumLogCharacters = 80_000;
    private string _teraRoot = string.Empty;
    private string _selectedAction = "Apply";
    private bool _includeClassicPlus;
    private bool _checkForUpdates = true;
    private bool _isRunning;
    private string _statusText = "Ready";
    private string _logText = string.Empty;
    private string? _lastLogPath;

    public MainWindowViewModel(string? packageRoot)
    {
        PackageRoot = packageRoot;
        IsWindows = OperatingSystem.IsWindows();
        TeraRoot = PackageLocator.DetectTeraRoot(packageRoot) ?? string.Empty;

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
        else if (PackageRoot is null)
            StatusText = "Install.ps1 could not be located";
    }

    public IReadOnlyList<string> Actions { get; } =
    [
        "Apply",
        "Status",
        "EnableReShade",
        "DisableReShade",
        "RestoreReShade",
        "ApplyClassicPlus",
        "ExportClassicPlus",
        "LockConfigs",
        "UnlockConfigs"
    ];

    public ObservableCollection<PhaseItem> Phases { get; }
    public string? PackageRoot { get; }
    public bool IsWindows { get; }

    public string TeraRoot
    {
        get => _teraRoot;
        set
        {
            if (SetField(ref _teraRoot, value))
                OnPropertyChanged(nameof(CanRun));
        }
    }

    public string SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (!SetField(ref _selectedAction, value))
                return;

            if (!CanIncludeClassicPlus)
                IncludeClassicPlus = false;
            OnPropertyChanged(nameof(CanIncludeClassicPlus));
            OnPropertyChanged(nameof(CanCheckForUpdates));
            OnPropertyChanged(nameof(RunButtonText));
        }
    }

    public bool IncludeClassicPlus
    {
        get => _includeClassicPlus;
        set => SetField(ref _includeClassicPlus, value);
    }

    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set => SetField(ref _checkForUpdates, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
                OnPropertyChanged(nameof(CanRun));
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
        PackageRoot is not null &&
        PackageLocator.LooksLikeTeraRoot(TeraRoot);

    public bool CanIncludeClassicPlus =>
        SelectedAction is "Apply" or "EnableReShade";

    public bool CanCheckForUpdates => SelectedAction == "Apply";
    public bool HasLog => !string.IsNullOrWhiteSpace(LastLogPath) && File.Exists(LastLogPath);
    public string RunButtonText => SelectedAction == "Status" ? "Inspect setup" : "Run action";

    public InstallerRequest CreateRequest() => new(
        SelectedAction,
        TeraRoot,
        CanIncludeClassicPlus && IncludeClassicPlus,
        !CanCheckForUpdates || CheckForUpdates);

    public void ApplyRequest(InstallerRequest request)
    {
        SelectedAction = request.Action;
        TeraRoot = request.TeraRoot;
        IncludeClassicPlus = request.IncludeClassicPlus;
        CheckForUpdates = request.CheckForUpdates;
    }

    public void BeginRun()
    {
        foreach (var phase in Phases)
            phase.State = PhaseState.Pending;

        LogText = string.Empty;
        LastLogPath = null;
        StatusText = "Starting installer…";
        IsRunning = true;
    }

    public void HandleOutput(InstallerOutput output)
    {
        if (output.Event is { } installerEvent)
        {
            HandleEvent(installerEvent);
            return;
        }

        AppendLog(output.Text, output.IsError);
    }

    public void CompleteRun(InstallerRunResult result)
    {
        LastLogPath = result.LogPath;
        IsRunning = false;
        StatusText = result.ExitCode == 0
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

    private void HandleEvent(InstallerEvent installerEvent)
    {
        if (!string.IsNullOrWhiteSpace(installerEvent.Message))
            AppendLog(installerEvent.Message!, installerEvent.Status == "failed");

        if (string.Equals(installerEvent.Event, "result", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = installerEvent.Status == "completed" ? "Finalizing…" : "Installation failed";
            return;
        }

        if (string.IsNullOrWhiteSpace(installerEvent.Phase))
            return;

        var phase = Phases.FirstOrDefault(item =>
            string.Equals(item.Id, installerEvent.Phase, StringComparison.OrdinalIgnoreCase));
        if (phase is null)
            return;

        phase.State = installerEvent.Status?.ToLowerInvariant() switch
        {
            "started" => PhaseState.Active,
            "completed" or "skipped" => PhaseState.Complete,
            "failed" => PhaseState.Failed,
            _ => phase.State
        };
        StatusText = installerEvent.Message ?? $"{phase.Label}: {installerEvent.Status}";
    }

    private void AppendLog(string line, bool isError)
    {
        var prefix = isError ? "[error] " : string.Empty;
        var updated = LogText + prefix + line + Environment.NewLine;
        if (updated.Length > MaximumLogCharacters)
            updated = updated[^MaximumLogCharacters..];
        LogText = updated;
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
