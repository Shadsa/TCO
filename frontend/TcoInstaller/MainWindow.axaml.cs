using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TcoInstaller.Models;
using TcoInstaller.Services;
using TcoInstaller.ViewModels;

namespace TcoInstaller;

public sealed partial class MainWindow : Window
{
    private readonly InstallerRunner _runner = new();
    private readonly MainWindowViewModel _viewModel;
    private readonly ElevationEnvelope? _pendingRequest;

    public MainWindow() : this(App.PendingRequestPayload)
    {
    }

    public MainWindow(string? pendingRequestPayload)
    {
        InitializeComponent();

        _pendingRequest = ElevationService.ReadRequest(pendingRequestPayload);
        var packageRoot = _pendingRequest?.PackageRoot ?? PackageLocator.FindPackageRoot();
        _viewModel = new MainWindowViewModel(packageRoot);
        DataContext = _viewModel;

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

    private async void Run_OnClick(object? sender, RoutedEventArgs e) =>
        await StartRequestAsync(_viewModel.CreateRequest());

    private async void RunPendingRequest(object? sender, EventArgs e)
    {
        Opened -= RunPendingRequest;
        if (_pendingRequest is not null)
            await RunCoreAsync(_pendingRequest.Request);
    }

    private async Task StartRequestAsync(InstallerRequest request)
    {
        if (_viewModel.PackageRoot is null)
        {
            _viewModel.FailRun("The TCO package root could not be located.");
            return;
        }

        var requiresElevation = request.Action != "Status";
        if (requiresElevation && !ElevationService.IsAdministrator())
        {
            var elevation = ElevationService.RelaunchElevated(_viewModel.PackageRoot, request);
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
        if (_viewModel.PackageRoot is null)
            return;

        _viewModel.BeginRun();
        var progress = new Progress<InstallerOutput>(output =>
        {
            _viewModel.HandleOutput(output);
            LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
        });

        try
        {
            var result = await _runner.RunAsync(_viewModel.PackageRoot, request, progress);
            _viewModel.CompleteRun(result);
        }
        catch (Exception exception)
        {
            _viewModel.FailRun(exception.ToString());
        }
    }

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
}
