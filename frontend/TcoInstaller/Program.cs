using Avalonia;

namespace TcoInstaller;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (Services.UpdateHandoff.TryApply(args))
            return;
        App.PendingRequestPayload = GetArgument(args, "--request");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect();

    private static string? GetArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }
}
