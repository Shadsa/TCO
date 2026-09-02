using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace TcoInstaller;

public sealed partial class App : Application
{
    public static string? PendingRequestPayload { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(PendingRequestPayload);

        base.OnFrameworkInitializationCompleted();
    }
}
