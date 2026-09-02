using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TcoInstaller.Models;

namespace TcoInstaller.Backend;

/// <summary>
/// Owns the durable pre-TCO engine snapshot. Restore validates identity, containment,
/// file coverage, and SHA-256 before replacing any INI.
/// </summary>
internal sealed class EngineBackupStore
{
    private const int CurrentSchema = 1;

    public void SaveIfMissing(TeraPaths paths, FileTransaction transaction)
    {
        var statePath = GetStatePath(paths);
        if (File.Exists(statePath))
        {
            _ = Read(paths);
            return;
        }

        var backupRoot = GetBackupRoot(paths);
        if (Directory.Exists(backupRoot) && Directory.EnumerateFileSystemEntries(backupRoot).Any())
            throw new InvalidDataException("An engine backup directory exists without valid state metadata.");

        transaction.CaptureDirectory(backupRoot);
        transaction.CaptureFile(statePath);
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(paths.Tools);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in paths.EngineFiles)
        {
            var backupPath = Path.Combine(backupRoot, pair.Key);
            File.Copy(pair.Value, backupPath, true);
            files[pair.Key] = HashFile(backupPath);
        }

        var state = new EngineConfiguration
        {
            Schema = CurrentSchema,
            Id = "original-backup",
            TeraRoot = paths.Root,
            CapturedAt = DateTimeOffset.Now,
            BackupFiles = files
        };
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
    }

    public void Restore(TeraPaths paths, FileTransaction transaction)
    {
        foreach (var backup in Read(paths).BackupFiles)
        {
            var destination = paths.EngineFiles[backup.Key];
            transaction.CaptureFile(destination);
            File.Copy(GetSafeBackupPath(paths, backup.Key), destination, true);
        }
    }

    public bool IsValid(TeraPaths paths)
    {
        try
        {
            _ = Read(paths);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException or JsonException)
        {
            return false;
        }
    }

    private static EngineConfiguration Read(TeraPaths paths)
    {
        var statePath = GetStatePath(paths);
        if (!File.Exists(statePath))
            throw new FileNotFoundException("No original engine configuration has been captured by TCO.", statePath);
        var state = ReadStateFile(statePath);
        if (state.Schema != CurrentSchema ||
            !Path.GetFullPath(state.TeraRoot).Equals(paths.Root, StringComparison.OrdinalIgnoreCase) ||
            state.BackupFiles.Count != paths.EngineFiles.Count)
            throw new InvalidDataException("The engine backup state does not match this installation.");

        foreach (var backup in state.BackupFiles)
        {
            if (!paths.EngineFiles.ContainsKey(backup.Key))
                throw new InvalidDataException($"Engine backup references an unsupported file: {backup.Key}");
            var path = GetSafeBackupPath(paths, backup.Key);
            if (!File.Exists(path) || !HashFile(path).Equals(backup.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Engine backup validation failed: {backup.Key}");
        }
        return state;
    }

    private static EngineConfiguration ReadStateFile(string statePath)
    {
        var json = File.ReadAllText(statePath);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("BackupFiles", out _))
            return JsonSerializer.Deserialize<EngineConfiguration>(json, JsonOptions)
                ?? throw new InvalidDataException("The engine backup state is invalid.");

        // Version 1.1 originally stored the same ledger as an array of file records.
        if (!document.RootElement.TryGetProperty("Files", out var legacyFiles) ||
            legacyFiles.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The engine backup state is invalid.");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in legacyFiles.EnumerateArray())
        {
            var name = file.GetProperty("File").GetString() ?? string.Empty;
            var backupName = file.GetProperty("BackupFile").GetString() ?? string.Empty;
            if (!name.Equals(backupName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The legacy engine backup uses an unsupported file mapping.");
            hashes[name] = file.GetProperty("Sha256").GetString() ?? string.Empty;
        }
        var root = document.RootElement;
        return new EngineConfiguration
        {
            Schema = root.GetProperty("Schema").GetInt32(),
            Id = "original-backup",
            TeraRoot = root.GetProperty("TeraRoot").GetString() ?? string.Empty,
            CapturedAt = root.TryGetProperty("CreatedAt", out var createdAt) ? createdAt.GetDateTimeOffset() : null,
            BackupFiles = hashes
        };
    }

    private static string GetSafeBackupPath(TeraPaths paths, string backupFile)
    {
        if (Path.IsPathRooted(backupFile) || backupFile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            backupFile.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("The engine backup contains an unsafe path.");
        var root = Path.GetFullPath(GetBackupRoot(paths)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, backupFile));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The engine backup path escapes its root.");
        return path;
    }

    private static string GetBackupRoot(TeraPaths paths) => Path.Combine(paths.Tools, "engine-original");
    private static string GetStatePath(TeraPaths paths) => Path.Combine(paths.Tools, "engine-profile-state.json");

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };
}
