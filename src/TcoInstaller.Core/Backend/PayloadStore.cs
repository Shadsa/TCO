using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TcoInstaller.Backend;

/// <summary>
/// Reads embedded package content while keeping executable runtimes and editable configuration
/// under separate trust policies. Only executable entries belong to the integrity manifest.
/// </summary>
public sealed class PayloadStore
{
    private const string ResourcePrefix = "Tco.Payload/";
    private readonly Assembly _assembly;
    private readonly Dictionary<string, string> _resources;
    private readonly Dictionary<string, ManifestEntry> _entries;

    public PayloadStore()
    {
        _assembly = typeof(PayloadStore).Assembly;
        _resources = _assembly.GetManifestResourceNames()
            .Where(name => Normalize(name).StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(name => Normalize(name)[ResourcePrefix.Length..], name => name, StringComparer.OrdinalIgnoreCase);

        using var manifestStream = OpenRaw("manifest.json");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestStream, JsonOptions)
            ?? throw new InvalidDataException("The embedded payload manifest is invalid.");
        Version = manifest.Version;
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.FileCount != manifest.Files.Count)
            throw new InvalidDataException("The embedded manifest file count is inconsistent.");
        if (manifest.Files.Any(entry =>
                !Normalize(entry.Path).StartsWith("payload/", StringComparison.OrdinalIgnoreCase) ||
                !IsExecutablePayload(Normalize(entry.Path)["payload/".Length..]) ||
                entry.Bytes < 0 ||
                entry.Sha256.Length != 64 ||
                !entry.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The embedded manifest contains an invalid payload entry.");

        try
        {
            _entries = manifest.Files.ToDictionary(
                entry => Normalize(entry.Path)["payload/".Length..],
                entry => entry,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The embedded manifest contains duplicate paths.", exception);
        }

        var resourcePaths = _resources.Keys
            .Where(path => !path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var executablePaths = resourcePaths.Where(IsExecutablePayload).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (executablePaths.Count == 0 || !executablePaths.SetEquals(_entries.Keys))
            throw new InvalidDataException("The integrity manifest must describe every executable payload, and only executable payloads.");
    }

    public string Version { get; }
    public IEnumerable<string> Files => _resources.Keys
        .Where(path => !path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>Verifies executable code only; editable package content is validated by its domain parser.</summary>
    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        foreach (var relativePath in _entries.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadVerifiedBytesAsync(relativePath, cancellationToken);
        }
    }

    public string ReadText(string relativePath)
    {
        using var stream = OpenRaw(relativePath);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    /// <summary>Reads an executable payload and rejects content that differs from its manifest entry.</summary>
    public async Task<byte[]> ReadVerifiedBytesAsync(string relativePath, CancellationToken cancellationToken)
    {
        relativePath = NormalizeRelative(relativePath);
        var entry = GetEntry(relativePath);
        await using var stream = OpenRaw(relativePath);
        using var memory = new MemoryStream(entry.Bytes > int.MaxValue ? 0 : (int)entry.Bytes);
        await stream.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        Validate(entry, bytes);
        return bytes;
    }

    public async Task CopyFileAsync(
        string relativePath,
        string destination,
        FileTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        relativePath = NormalizeRelative(relativePath);
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Destination has no directory: {destination}");
        Directory.CreateDirectory(destinationDirectory);
        var temporary = Path.Combine(destinationDirectory, $".{Path.GetFileName(destination)}.tco-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var input = OpenRaw(relativePath))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, cancellationToken);

            if (_entries.TryGetValue(relativePath, out var entry))
            {
                var copiedBytes = await File.ReadAllBytesAsync(temporary, cancellationToken);
                Validate(entry, copiedBytes);
            }

            transaction.CaptureFile(destination);
            if (File.Exists(destination))
                File.SetAttributes(destination, FileAttributes.Normal);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async Task CopyTreeAsync(
        string relativePrefix,
        string destinationRoot,
        FileTransaction transaction,
        bool overwriteExisting = true,
        CancellationToken cancellationToken = default)
    {
        relativePrefix = NormalizeRelative(relativePrefix).TrimEnd('/') + "/";
        var matches = Files.Where(path => path.StartsWith(relativePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            throw new InvalidDataException($"The embedded payload tree is missing: {relativePrefix}");

        foreach (var relativePath in matches)
        {
            var child = relativePath[relativePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.Combine(destinationRoot, child);
            if (!overwriteExisting && File.Exists(destination))
                continue;
            await CopyFileAsync(relativePath, destination, transaction, cancellationToken);
        }
    }

    public bool Contains(string relativePath) => _resources.ContainsKey(NormalizeRelative(relativePath));

    public string GetSha256(string relativePath) => GetEntry(NormalizeRelative(relativePath)).Sha256;

    private Stream OpenRaw(string relativePath)
    {
        relativePath = NormalizeRelative(relativePath);
        if (!_resources.TryGetValue(relativePath, out var resourceName))
            throw new FileNotFoundException("An embedded TCO resource is missing.", relativePath);
        return _assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("An embedded TCO resource could not be opened.", resourceName);
    }

    private ManifestEntry GetEntry(string relativePath) =>
        _entries.TryGetValue(relativePath, out var entry)
            ? entry
            : throw new FileNotFoundException("The executable integrity manifest does not describe this payload file.", relativePath);

    private static void Validate(ManifestEntry entry, byte[] bytes)
    {
        if (!Matches(entry, bytes))
            throw new InvalidDataException($"Executable payload validation failed: {entry.Path}");
    }

    private static bool Matches(ManifestEntry entry, byte[] bytes) =>
        entry.Bytes == bytes.LongLength &&
        entry.Sha256.Equals(Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutablePayload(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) => value.Replace('\\', '/');

    private static string NormalizeRelative(string value)
    {
        value = Normalize(value).TrimStart('/');
        if (Path.IsPathRooted(value) || value.Split('/').Any(part => part == ".."))
            throw new InvalidDataException($"Unsafe embedded payload path: {value}");
        return value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record PackageManifest(string Version, int FileCount, IReadOnlyList<ManifestEntry> Files);
    private sealed record ManifestEntry(string Path, long Bytes, string Sha256);
}
