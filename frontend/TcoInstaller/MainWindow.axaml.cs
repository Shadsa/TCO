using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TcoInstaller.Backend;
using TcoInstaller.Contracts;
using TcoInstaller.Services;
using TcoInstaller.ViewModels;

namespace TcoInstaller;

public sealed partial class MainWindow : Window
{
    private readonly InstallerRunner _runner;
    private readonly InstallerOrchestrator _orchestrator;
    private readonly MainWindowViewModel _viewModel;
    private readonly ElevationEnvelope? _pendingRequest;
    private CancellationTokenSource? _runCancellation;

    public MainWindow() : this(App.PendingRequestPayload)
    {
    }

    public MainWindow(string? pendingRequestPayload)
    {
        InitializeComponent();

        _orchestrator = new InstallerOrchestrator();
        _runner = new InstallerRunner(_orchestrator);
        _pendingRequest = ElevationService.ReadRequest(pendingRequestPayload);
        _viewModel = new MainWindowViewModel(_orchestrator.EngineConfigurations, _orchestrator.DefaultEngineConfigurationId);
        DataContext = _viewModel;
        ReadmeViewer.LinkClicked += (_, args) =>
        {
            if (Uri.TryCreate(args.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };

        if (_pendingRequest is not null)
        {
            _viewModel.ApplyRequest(_pendingRequest.Request);
            Opened += RunPendingRequest;
        }

        Closing += (_, args) =>
        {
            if (_viewModel.IsRunning)
                args.Cancel = true;
        };
    }

    private async void Browse_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the TERA installation folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            _viewModel.TeraRoot = folders[0].Path.LocalPath;
    }

    private async void BrowseEngineProfile_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a custom TCO engine profile",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("TCO engine profile") { Patterns = ["*.json"] }
            ]
        });

        if (files.Count == 0)
            return;

        try
        {
            var path = files[0].Path.LocalPath;
            var configuration = _orchestrator.LoadCustomEngineConfiguration(path);
            _viewModel.SetCustomEngineConfiguration(path, configuration);
            _viewModel.StatusText = $"Custom engine profile ready: {configuration.Name}";
        }
        catch (Exception exception)
        {
            _viewModel.StatusText = exception.Message;
        }
    }

    private void ClearEngineProfile_OnClick(object? sender, RoutedEventArgs e) =>
        _viewModel.ClearCustomEngineConfiguration();

    private async void Action_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: not null } button &&
            Enum.TryParse<InstallerAction>(button.Tag.ToString(), true, out var action))
            await StartRequestAsync(_viewModel.CreateRequest(action));
    }

    private async void RunPendingRequest(object? sender, EventArgs e)
    {
        Opened -= RunPendingRequest;
        if (_pendingRequest is not null)
            await RunCoreAsync(_pendingRequest.Request);
    }

    private async Task StartRequestAsync(InstallerRequest request)
    {
        var requiresElevation = request.Action != InstallerAction.Status;
        if (requiresElevation && !ElevationService.IsAdministrator())
        {
            var elevation = ElevationService.RelaunchElevated(request);
            if (!elevation.Started)
            {
                _viewModel.StatusText = elevation.Error ?? "Elevation failed";
                return;
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            return;
        }

        await RunCoreAsync(request);
    }

    private async Task RunCoreAsync(InstallerRequest request)
    {
        _viewModel.BeginRun();
        _runCancellation = new CancellationTokenSource();
        var progress = new Progress<InstallerProgress>(output =>
        {
            _viewModel.HandleOutput(output);
            LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
        });

        try
        {
            var result = await _runner.RunAsync(request, progress, _runCancellation.Token);
            _viewModel.CompleteRun(result);
            if (result.Update is not null)
            {
                UpdateHandoff.Start(result.Update, request);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            }
        }
        catch (OperationCanceledException)
        {
            _viewModel.FailRun("Operation cancelled. Any in-progress file changes were rolled back.");
        }
        catch (Exception exception)
        {
            _viewModel.FailRun(exception.ToString());
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => _runCancellation?.Cancel();

    private void OpenLog_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasLog || _viewModel.LastLogPath is null)
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _viewModel.LastLogPath,
            UseShellExecute = true
        });
    }

    private void OpenReport_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasReport || _viewModel.LastReportPath is null)
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _viewModel.LastReportPath,
            UseShellExecute = true
        });
    }
}
