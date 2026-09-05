using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TcoInstaller.Backend;

/// <summary>Performs section-aware INI reads and updates while preserving unrelated settings.</summary>
public static class IniFile
{
    private static readonly Regex SectionPattern = new("^\\s*\\[(.+?)\\]\\s*$", RegexOptions.Compiled);

    public static string? GetValue(string path, string section, string key)
    {
        if (!File.Exists(path))
            return null;

        var inSection = false;
        var keyPattern = new Regex($"^\\s*{Regex.Escape(key)}\\s*=\\s*(.*?)\\s*$", RegexOptions.IgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var sectionMatch = SectionPattern.Match(line);
            if (sectionMatch.Success)
            {
                inSection = sectionMatch.Groups[1].Value.Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection && keyPattern.Match(line) is { Success: true } match)
                return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>Reads a key placed before the first INI section, as used by ReShade preset files.</summary>
    public static string? GetPreambleValue(string path, string key)
    {
        if (!File.Exists(path))
            return null;

        var keyPattern = new Regex($"^\\s*{Regex.Escape(key)}\\s*=\\s*(.*?)\\s*$", RegexOptions.IgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            if (SectionPattern.IsMatch(line))
                break;
            if (keyPattern.Match(line) is { Success: true } match)
                return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Updates a key before the first INI section while preserving all unrelated preset content.</summary>
    public static void SetPreambleValue(string path, string key, string value)
    {
        if (value.Contains('\r') || value.Contains('\n'))
            throw new InvalidDataException($"INI value {key} contains a line break.");

        var lines = File.ReadAllLines(path).ToList();
        var preambleEnd = lines.FindIndex(line => SectionPattern.IsMatch(line));
        if (preambleEnd < 0)
            preambleEnd = lines.Count;
        var keyPattern = new Regex($"^\\s*{Regex.Escape(key)}\\s*=", RegexOptions.IgnoreCase);
        var matches = Enumerable.Range(0, preambleEnd).Where(index => keyPattern.IsMatch(lines[index])).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException($"Ambiguous duplicate {key} entries before the first section of {path}.");
        if (matches.Length == 0)
            lines.Insert(0, $"{key}={value}");
        else
            lines[matches[0]] = $"{key}={value}";
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    public static void SetValue(string path, string section, string key, string value, bool rejectDuplicates = true) =>
        SetValues(path, section, new Dictionary<string, string> { [key] = value }, rejectDuplicates);

    public static void SetValues(
        string path,
        string section,
        IReadOnlyDictionary<string, string> values,
        bool rejectDuplicates = false)
    {
        var lines = File.ReadAllLines(path).ToList();
        var sectionStart = -1;
        var sectionEnd = lines.Count;
        for (var index = 0; index < lines.Count; index++)
        {
            var match = SectionPattern.Match(lines[index]);
            if (!match.Success)
                continue;
            if (sectionStart >= 0)
            {
                sectionEnd = index;
                break;
            }
            if (match.Groups[1].Value.Equals(section, StringComparison.OrdinalIgnoreCase))
                sectionStart = index;
        }

        if (sectionStart < 0)
        {
            lines.Add(string.Empty);
            lines.Add($"[{section}]");
            sectionStart = lines.Count - 1;
            sectionEnd = lines.Count;
        }

        foreach (var (key, value) in values)
        {
            if (value.Contains('\r') || value.Contains('\n'))
                throw new InvalidDataException($"INI value {section}/{key} contains a line break.");

            var keyPattern = new Regex($"^\\s*{Regex.Escape(key)}\\s*=", RegexOptions.IgnoreCase);
            var matches = new List<int>();
            for (var index = sectionStart + 1; index < sectionEnd; index++)
            {
                if (keyPattern.IsMatch(lines[index]))
                    matches.Add(index);
            }

            if (rejectDuplicates && matches.Count > 1)
                throw new InvalidDataException($"Ambiguous duplicate {key} entries in [{section}] of {path}.");

            if (matches.Count == 0)
            {
                lines.Insert(sectionEnd, $"{key}={value}");
                sectionEnd++;
            }
            else
            {
                foreach (var index in matches)
                    lines[index] = $"{key}={value}";
            }
        }

        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    public static string GetTextureGroupFingerprint(string path)
    {
        var text = string.Join('\n', File.ReadLines(path).Where(line => line.StartsWith("TEXTUREGROUP_", StringComparison.Ordinal)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
