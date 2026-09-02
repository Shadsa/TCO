namespace TcoInstaller.Backend;

/// <summary>Defines TCO's per-user logs, update staging, and exported profile directories.</summary>
public static class AppStorage
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCO");

    public static string Logs => Path.Combine(Root, "logs");
    public static string Reports => Path.Combine(Root, "reports");
    public static string Profiles => Path.Combine(Root, "profiles");
    public static string Updates => Path.Combine(Root, "updates");
}
