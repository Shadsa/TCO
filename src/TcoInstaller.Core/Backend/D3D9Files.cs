using System.Diagnostics;
using System.Security.Cryptography;

namespace TcoInstaller.Backend;

/// <summary>Contains the small, transaction-aware D3D9 DLL filesystem operations.</summary>
internal static class D3D9Files
{
    public static string GetDllKind(string path)
    {
        if (!File.Exists(path)) return "Missing";
        var product = FileVersionInfo.GetVersionInfo(path).ProductName ?? string.Empty;
        if (product.Equals("DXVK", StringComparison.Ordinal)) return "DXVK";
        if (product.Equals("ReShade", StringComparison.Ordinal)) return "ReShade";
        return $"Unknown ({product})";
    }

    public static string GetHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void CopyFile(string source, string destination, FileTransaction transaction)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        transaction.CaptureFile(destination);
        if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
        File.Copy(source, destination, true);
    }

    public static void DeleteFile(string path, FileTransaction transaction)
    {
        transaction.CaptureFile(path);
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    public static bool IsInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
