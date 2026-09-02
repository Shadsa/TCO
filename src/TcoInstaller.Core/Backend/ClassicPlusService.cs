using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using TcoInstaller.Models;

namespace TcoInstaller.Backend;

/// <summary>
/// Installs, exports, sanitizes, and inspects the optional TCC/Shinra configuration bundle.
/// User-specific exports override the embedded defaults only when every required file is present.
/// </summary>
public sealed class ClassicPlusService(PayloadStore payload)
{
    private static readonly string[] RequiredProfileFiles =
    [
        "tcc/tcc-settings.json",
        "shinra/hotkeys.xml",
        "shinra/window.xml",
        "shinra/window_backup.xml",
        "shinra/server-overrides.txt"
    ];

    private string OverrideRoot => Path.Combine(AppStorage.Profiles, "classicplus");

    public void AssertInstalled()
    {
        var paths = GetPaths();
        if (!Directory.Exists(paths.TccRoot))
            throw new DirectoryNotFoundException($"TCC must be installed before running this action: {paths.TccRoot}");
        if (!Directory.Exists(paths.ShinraConfigRoot))
            throw new DirectoryNotFoundException($"Shinra Meter must be installed before running this action: {paths.ShinraConfigRoot}");
        AssertProfileAvailable();
    }

    public async Task InstallAsync(FileTransaction transaction, CancellationToken cancellationToken)
    {
        AssertInstalled();
        var paths = GetPaths();
        await WriteProfileFileAsync("tcc/tcc-settings.json", paths.TccConfig, transaction, cancellationToken);
        foreach (var name in new[] { "hotkeys.xml", "window.xml", "window_backup.xml", "server-overrides.txt" })
            await WriteProfileFileAsync($"shinra/{name}", Path.Combine(paths.ShinraConfigRoot, name), transaction, cancellationToken);

        var exportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShinraMeter") + Path.DirectorySeparatorChar;
        foreach (var name in new[] { "window.xml", "window_backup.xml" })
        {
            var path = Path.Combine(paths.ShinraConfigRoot, name);
            transaction.CaptureFile(path);
            var document = LoadXml(File.ReadAllText(path));
            SetNodeText(document, "//excel_save_directory", exportPath);
            SetNodeText(document, "//mute_sound", "true");
            WriteXml(document, path);
        }

        var hotkeyPath = Path.Combine(paths.ShinraConfigRoot, "hotkeys.xml");
        transaction.CaptureFile(hotkeyPath);
        var hotkeys = LoadXml(File.ReadAllText(hotkeyPath));
        SetNodeText(hotkeys, "/hotkeys/paste/ctrl", "True");
        SetNodeText(hotkeys, "/hotkeys/paste/key", "Home");
        WriteXml(hotkeys, hotkeyPath);
    }

    public string Export(FileTransaction transaction)
    {
        ProcessGuard.AssertClosed(true);
        var paths = GetPaths();
        foreach (var path in new[]
                 {
                     paths.TccConfig,
                     Path.Combine(paths.ShinraConfigRoot, "hotkeys.xml"),
                     Path.Combine(paths.ShinraConfigRoot, "window.xml"),
                     Path.Combine(paths.ShinraConfigRoot, "window_backup.xml"),
                     Path.Combine(paths.ShinraConfigRoot, "server-overrides.txt")
                 })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("A current Classic+ configuration file is missing.", path);
        }

        var tcc = JsonNode.Parse(ReadSharedText(paths.TccConfig))?.AsObject()
            ?? throw new InvalidDataException("TCC settings are not valid JSON.");
        if (tcc.ContainsKey("LastAccountNameHash"))
            tcc["LastAccountNameHash"] = string.Empty;
        WriteOverride("tcc/tcc-settings.json", tcc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), transaction);

        foreach (var name in new[] { "hotkeys.xml", "window.xml", "window_backup.xml" })
        {
            var document = LoadXml(ReadSharedText(Path.Combine(paths.ShinraConfigRoot, name)));
            var sensitiveNodes = document.SelectNodes("//token | //username");
            if (sensitiveNodes is not null)
                foreach (XmlNode node in sensitiveNodes)
                    node.InnerText = string.Empty;
            SetNodeText(document, "//excel_save_directory", @"__DOCUMENTS__\ShinraMeter\");
            if (name == "hotkeys.xml")
            {
                SetNodeText(document, "/hotkeys/paste/ctrl", "True");
                SetNodeText(document, "/hotkeys/paste/key", "Home");
            }
            WriteOverrideXml($"shinra/{name}", document, transaction);
        }

        WriteOverride("shinra/server-overrides.txt", ReadSharedText(Path.Combine(paths.ShinraConfigRoot, "server-overrides.txt")), transaction);
        AssertProfileAvailable();
        return OverrideRoot;
    }

    public ClassicPlusConfiguration Inspect()
    {
        var paths = GetPaths();
        var shortcut = "<missing>";
        var muted = false;
        try
        {
            var hotkeys = LoadXml(ReadSharedText(Path.Combine(paths.ShinraConfigRoot, "hotkeys.xml")));
            var ctrl = hotkeys.SelectSingleNode("/hotkeys/paste/ctrl")?.InnerText;
            var key = hotkeys.SelectSingleNode("/hotkeys/paste/key")?.InnerText ?? string.Empty;
            shortcut = ctrl?.Equals("True", StringComparison.OrdinalIgnoreCase) == true ? $"Ctrl+{key}" : key;
            var window = LoadXml(ReadSharedText(Path.Combine(paths.ShinraConfigRoot, "window.xml")));
            muted = window.SelectSingleNode("//mute_sound")?.InnerText.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            // Classic+ keeps these files exclusively locked while running.
        }

        return new ClassicPlusConfiguration
        {
            TccInstalled = File.Exists(paths.TccConfig),
            ShinraInstalled = File.Exists(Path.Combine(paths.ShinraConfigRoot, "window.xml")),
            PasteShortcut = shortcut,
            AudioMuted = muted,
            OverrideAvailable = HasCompleteOverride()
        };
    }

    private async Task WriteProfileFileAsync(string relativePath, string destination, FileTransaction transaction, CancellationToken cancellationToken)
    {
        var overridePath = Path.Combine(OverrideRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (HasCompleteOverride())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            transaction.CaptureFile(destination);
            File.Copy(overridePath, destination, true);
            return;
        }
        await payload.CopyFileAsync("classicplus/" + relativePath, destination, transaction, cancellationToken);
    }

    private void AssertProfileAvailable()
    {
        if (HasCompleteOverride())
            return;
        foreach (var path in RequiredProfileFiles)
        {
            if (!payload.Contains("classicplus/" + path))
                throw new InvalidDataException($"The embedded Classic+ profile is incomplete: {path}");
        }
    }

    private bool HasCompleteOverride() => RequiredProfileFiles.All(path => File.Exists(Path.Combine(OverrideRoot, path.Replace('/', Path.DirectorySeparatorChar))));

    private void WriteOverride(string relativePath, string content, FileTransaction transaction)
    {
        var path = Path.Combine(OverrideRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        transaction.CaptureFile(path);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private void WriteOverrideXml(string relativePath, XmlDocument document, FileTransaction transaction)
    {
        var path = Path.Combine(OverrideRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        transaction.CaptureFile(path);
        WriteXml(document, path);
    }

    private static (string TccRoot, string TccConfig, string ShinraConfigRoot) GetPaths()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crazy-eSports-ClassicPlus", "mods", "external");
        var tccRoot = Path.Combine(root, "classicplus.tcc");
        var shinraRoot = Path.Combine(root, "classicplus.shinra", "resources", "config");
        return (tccRoot, Path.Combine(tccRoot, "tcc-settings.json"), shinraRoot);
    }

    private static XmlDocument LoadXml(string text)
    {
        var document = new XmlDocument { PreserveWhitespace = false };
        document.LoadXml(text);
        return document;
    }

    private static void SetNodeText(XmlDocument document, string xpath, string value)
    {
        var node = document.SelectSingleNode(xpath);
        if (node is not null)
            node.InnerText = value;
    }

    private static void WriteXml(XmlDocument document, string path)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            NewLineChars = "\r\n"
        };
        using var writer = XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}
