using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TcoInstaller.Contracts;

namespace TcoInstaller.Backend;

/// <summary>Stages a newer GitHub release only after validating its declared SHA-256 digest.</summary>
public sealed class ReleaseUpdateService
{
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/Shadsa/TCO/releases/latest");
    private readonly HttpClient _http;
    private readonly string _updatesRoot;

    public ReleaseUpdateService(HttpClient? http = null, string? updatesRoot = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _updatesRoot = updatesRoot ?? AppStorage.Updates;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TCO-Installer", ThisVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static string ThisVersion =>
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(ReleaseUpdateService).Assembly)
        .GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<StagedUpdate?> CheckAndStageAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(LatestReleaseUri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("GitHub returned an invalid release document.");
        var tag = NormalizeVersion(release.TagName);
        if (string.IsNullOrWhiteSpace(tag) || !IsNewer(tag, ThisVersion))
            return null;

        var asset = SelectExecutable(release.Assets);
        var expectedHash = await ResolveHashAsync(release.Assets, asset, cancellationToken);
        var updateRoot = Path.Combine(_updatesRoot, SanitizeSegment(tag));
        Directory.CreateDirectory(updateRoot);
        var destination = Path.Combine(updateRoot, $"TCO.Installer-{Guid.NewGuid():N}.exe");

        try
        {
            await DownloadAsync(asset.BrowserDownloadUrl, destination, cancellationToken);
            var actualHash = await HashFileAsync(destination, cancellationToken);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded release SHA-256 does not match GitHub metadata.");
            return new StagedUpdate(tag, destination, actualHash);
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    private async Task<string> ResolveHashAsync(
        IReadOnlyList<GitHubAsset> assets,
        GitHubAsset executable,
        CancellationToken cancellationToken)
    {
        if (executable.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var digest = executable.Digest["sha256:".Length..];
            if (IsSha256(digest)) return digest.ToUpperInvariant();
        }

        var sidecar = assets.FirstOrDefault(asset =>
            asset.Name.Equals(executable.Name + ".sha256", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Release asset has no SHA-256 digest or sidecar: {executable.Name}");
        using var response = await _http.GetAsync(ValidateDownloadUri(sidecar.BrowserDownloadUrl), cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var candidate = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(IsSha256);
        return candidate?.ToUpperInvariant()
            ?? throw new InvalidDataException($"Invalid SHA-256 sidecar: {sidecar.Name}");
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(ValidateDownloadUri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static GitHubAsset SelectExecutable(IReadOnlyList<GitHubAsset> assets)
    {
        var executables = assets.Where(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        var preferred = executables.Where(asset =>
            asset.Name.StartsWith("TCO", StringComparison.OrdinalIgnoreCase) ||
            asset.Name.StartsWith("TERA-Complete", StringComparison.OrdinalIgnoreCase)).ToArray();
        return preferred.Length switch
        {
            1 => preferred[0],
            > 1 => throw new InvalidDataException("Latest release contains multiple matching TCO executables."),
            _ when executables.Length == 1 => executables[0],
            _ when executables.Length == 0 => throw new InvalidDataException("Latest release contains no executable asset."),
            _ => throw new InvalidDataException("Latest release contains multiple executable assets and none has a TCO name.")
        };
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static Uri ValidateDownloadUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("GitHub release contains an unsafe download URL.");
        return uri;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => Uri.IsHexDigit(character));

    private static string NormalizeVersion(string? value) => value?.Trim().TrimStart('v', 'V') ?? string.Empty;

    private static bool IsNewer(string candidate, string current)
    {
        var normalizedCandidate = NormalizeVersion(candidate);
        var normalizedCurrent = NormalizeVersion(current);
        if (Version.TryParse(normalizedCandidate, out var candidateVersion) &&
            Version.TryParse(normalizedCurrent, out var currentVersion))
            return candidateVersion > currentVersion;
        return !normalizedCandidate.Equals(normalizedCurrent, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);
    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}

/// <summary>Signals that control must hand off to a verified staged executable.</summary>
public sealed class UpdateStagedException(StagedUpdate update)
    : Exception($"TCO {update.Version} is ready to install.")
{
    public StagedUpdate Update { get; } = update;
}
