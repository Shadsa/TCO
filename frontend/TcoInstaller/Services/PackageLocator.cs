namespace TcoInstaller.Services;

/// <summary>Detects and validates candidate TERA installation roots for the UI.</summary>
public static class PackageLocator
{
    public static string? DetectTeraRoot()
    {
        var configured = Environment.GetEnvironmentVariable("TCO_TERA_ROOT");
        if (LooksLikeTeraRoot(configured))
            return Path.GetFullPath(configured!);

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
            {
                if (LooksLikeTeraRoot(directory.FullName))
                    return directory.FullName;
            }
        }

        return null;
    }

    public static bool LooksLikeTeraRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Directory.Exists(Path.Combine(path, "Binaries")) &&
        Directory.Exists(Path.Combine(path, "S1Game"));

}
