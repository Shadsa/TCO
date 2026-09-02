namespace TcoInstaller.Backend;

/// <summary>
/// Provides one Windows path identity policy for persisted installation roots,
/// transaction targets, and containment checks.
/// </summary>
internal static class PathIdentity
{
    private static readonly StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>Returns an absolute directory path without non-root trailing separators.</summary>
    public static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path);
        var rootLength = Path.GetPathRoot(normalized)?.Length ?? 0;
        while (normalized.Length > rootLength && Path.EndsInDirectorySeparator(normalized))
            normalized = normalized[..^1];
        return normalized;
    }

    /// <summary>Compares two directory spellings without requiring identical separators, case, or trailing slashes.</summary>
    public static bool DirectoryEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return NormalizeDirectory(left).Equals(NormalizeDirectory(right), Comparison);
        }
        catch (Exception exception) when (IsInvalidPath(exception))
        {
            return false;
        }
    }

    /// <summary>Tests lexical containment after resolving relative segments and enforcing a directory boundary.</summary>
    public static bool IsInsideDirectory(string root, string candidate)
    {
        var normalizedRoot = NormalizeDirectory(root);
        var normalizedCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        return relative.Equals(".", Comparison) ||
               (!Path.IsPathRooted(relative) &&
                !relative.Equals("..", Comparison) &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, Comparison) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, Comparison));
    }

    private static bool IsInvalidPath(Exception exception) =>
        exception is ArgumentException or NotSupportedException or PathTooLongException;
}
