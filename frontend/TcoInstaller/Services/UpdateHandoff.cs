using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TcoInstaller.Contracts;

namespace TcoInstaller.Services;

/// <summary>Atomically replaces the running executable with a verified staged update, then resumes the request.</summary>
public static class UpdateHandoff
{
    public static bool TryApply(IReadOnlyList<string> args)
    {
        var encoded = GetArgument(args, "--replace");
        if (encoded is null) return false;

        try
        {
            var envelope = Decode(encoded);
            Apply(envelope);
        }
        catch (Exception exception)
        {
            var log = Path.Combine(Path.GetTempPath(), $"tco-update-failed-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(log, exception.ToString(), new UTF8Encoding(false));
        }
        return true;
    }

    public static void Start(StagedUpdate update, InstallerRequest originalRequest)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        var request = originalRequest with { CheckForUpdates = false };
        var envelope = new ReplacementEnvelope(1, processPath, Environment.ProcessId, update.Sha256, request);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions)));
        var start = new ProcessStartInfo(update.ExecutablePath) { UseShellExecute = true };
        start.ArgumentList.Add("--replace");
        start.ArgumentList.Add(encoded);
        _ = Process.Start(start) ?? throw new InvalidOperationException("The update handoff could not be started.");
    }

    private static void Apply(ReplacementEnvelope envelope)
    {
        if (envelope.Schema != 1 || envelope.ParentProcessId <= 0 || !IsSha256(envelope.Sha256))
            throw new InvalidDataException("The update replacement request is invalid.");
        var source = Environment.ProcessPath
            ?? throw new InvalidOperationException("The staged executable path is unavailable.");
        var target = Path.GetFullPath(envelope.TargetPath);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update replacement target is invalid.");
        if (!HashFile(source).Equals(envelope.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged update failed its final integrity check.");

        try
        {
            using var parent = Process.GetProcessById(envelope.ParentProcessId);
            if (!parent.WaitForExit(60_000))
                throw new TimeoutException("The previous TCO process did not exit in time.");
        }
        catch (ArgumentException)
        {
            // The parent exited before this helper opened its process handle.
        }

        var directory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("The update target has no parent directory.");
        var incoming = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.new");
        var backup = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.bak");
        try
        {
            File.Copy(source, incoming, true);
            if (!HashFile(incoming).Equals(envelope.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The copied update failed integrity verification.");
            if (File.Exists(target))
                File.Replace(incoming, target, backup, true);
            else
                File.Move(incoming, target);
            if (!HashFile(target).Equals(envelope.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(backup)) File.Replace(backup, target, null, true);
                throw new InvalidDataException("The installed update failed integrity verification and was rolled back.");
            }
            if (File.Exists(backup)) File.Delete(backup);

            var start = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = directory };
            start.ArgumentList.Add("--request");
            start.ArgumentList.Add(ElevationService.CreatePayload(envelope.Request));
            Process.Start(start);
        }
        finally
        {
            if (File.Exists(incoming)) File.Delete(incoming);
        }
    }

    private static ReplacementEnvelope Decode(string encoded)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        return JsonSerializer.Deserialize<ReplacementEnvelope>(json, JsonOptions)
            ?? throw new InvalidDataException("The update replacement request is invalid.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string? GetArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record ReplacementEnvelope(int Schema, string TargetPath, int ParentProcessId, string Sha256, InstallerRequest Request);
}
