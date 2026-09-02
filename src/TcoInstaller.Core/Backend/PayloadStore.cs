using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TcoInstaller.Backend;

/// <summary>
/// Reads the embedded package and verifies its one-to-one manifest mapping, byte count, and SHA-256
/// before any payload file can be copied into an installation.
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
        if (manifest.FileCount != manifest.Files.Count)
            throw new InvalidDataException("The embedded manifest file count is inconsistent.");
        if (manifest.Files.Any(entry =>
                !Normalize(entry.Path).StartsWith("payload/", StringComparison.OrdinalIgnoreCase) ||
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

        if (_entries.Count == 0)
            throw new InvalidDataException("The embedded manifest contains no payload entries.");
        var resourcePaths = _resources.Keys
            .Where(path => !path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!resourcePaths.SetEquals(_entries.Keys))
            throw new InvalidDataException("The embedded payload and manifest do not describe the same files.");
    }

    public string Version { get; }
    public IEnumerable<string> Files => _entries.Keys.Order(StringComparer.OrdinalIgnoreCase);

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        foreach (var relativePath in Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadVerifiedBytesAsync(relativePath, cancellationToken);
        }
    }

    public string ReadText(string relativePath)
    {
        var bytes = ReadVerifiedBytesAsync(relativePath, CancellationToken.None).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(bytes);
    }

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
        var entry = GetEntry(relativePath);
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Destination has no directory: {destination}");
        Directory.CreateDirectory(destinationDirectory);
        var temporary = Path.Combine(destinationDirectory, $".{Path.GetFileName(destination)}.tco-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var input = OpenRaw(relativePath))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, cancellationToken);

            var copiedBytes = await File.ReadAllBytesAsync(temporary, cancellationToken);
            Validate(entry, copiedBytes);

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
        CancellationToken cancellationToken = default)
    {
        relativePrefix = NormalizeRelative(relativePrefix).TrimEnd('/') + "/";
        var matches = Files.Where(path => path.StartsWith(relativePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            throw new InvalidDataException($"The embedded payload tree is missing: {relativePrefix}");

        foreach (var relativePath in matches)
        {
            var child = relativePath[relativePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            await CopyFileAsync(relativePath, Path.Combine(destinationRoot, child), transaction, cancellationToken);
        }
    }

    public bool Contains(string relativePath) => _entries.ContainsKey(NormalizeRelative(relativePath));

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
            : throw new FileNotFoundException("The embedded manifest does not describe this payload file.", relativePath);

    private static void Validate(ManifestEntry entry, byte[] bytes)
    {
        if (Matches(entry, bytes)) return;

        var normalized = NormalizeLineEndings(bytes);
        if (normalized.Length != bytes.Length && Matches(entry, normalized)) return;

        throw new InvalidDataException($"Embedded payload validation failed: {entry.Path}");
    }

    private static bool Matches(ManifestEntry entry, byte[] bytes) =>
        entry.Bytes == bytes.LongLength &&
        entry.Sha256.Equals(Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase);

    private static byte[] NormalizeLineEndings(byte[] bytes)
    {
        var crlfCount = 0;
        for (var index = 0; index < bytes.Length - 1; index++)
            if (bytes[index] == (byte)'\r' && bytes[index + 1] == (byte)'\n') crlfCount++;
        if (crlfCount == 0) return bytes;

        var normalized = new byte[bytes.Length - crlfCount];
        var target = 0;
        for (var source = 0; source < bytes.Length; source++)
        {
            if (bytes[source] == (byte)'\r' && source + 1 < bytes.Length && bytes[source + 1] == (byte)'\n')
                continue;
            normalized[target++] = bytes[source];
        }
        return normalized;
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
