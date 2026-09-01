namespace TcoInstaller.Services;

public static class PackageLocator
{
    public static string? FindPackageRoot()
    {
        var configured = Environment.GetEnvironmentVariable("TCO_PACKAGE_ROOT");
        if (IsPackageRoot(configured))
            return Path.GetFullPath(configured!);

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
            {
                if (IsPackageRoot(directory.FullName))
                    return directory.FullName;
            }
        }

        return null;
    }

    public static string? DetectTeraRoot(string? packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
            return null;

        var package = new DirectoryInfo(packageRoot);
        if (LooksLikeTeraRoot(package.FullName))
            return package.FullName;

        return package.Parent is not null && LooksLikeTeraRoot(package.Parent.FullName)
            ? package.Parent.FullName
            : null;
    }

    public static bool LooksLikeTeraRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Directory.Exists(Path.Combine(path, "Binaries")) &&
        Directory.Exists(Path.Combine(path, "S1Game"));

    private static bool IsPackageRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "Install.ps1")) &&
        File.Exists(Path.Combine(path, "manifest.json")) &&
        Directory.Exists(Path.Combine(path, "modules"));
}
