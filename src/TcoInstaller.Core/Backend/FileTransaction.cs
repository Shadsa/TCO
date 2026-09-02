namespace TcoInstaller.Backend;

/// <summary>
/// Captures files and directories before mutation and restores them on disposal unless committed.
/// </summary>
public sealed class FileTransaction : IDisposable
{
    private sealed record Entry(string Path, string BackupPath, bool Existed, bool IsDirectory);

    private readonly string _backupRoot = Path.Combine(Path.GetTempPath(), "tco-transaction-" + Guid.NewGuid().ToString("N"));
    private readonly List<Entry> _entries = [];
    private readonly HashSet<string> _captured = new(StringComparer.OrdinalIgnoreCase);
    private bool _committed;

    public FileTransaction() => Directory.CreateDirectory(_backupRoot);

    public void CaptureFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!_captured.Add(path))
            return;

        var backup = Path.Combine(_backupRoot, _entries.Count.ToString("D6"));
        var existed = File.Exists(path);
        if (existed)
            File.Copy(path, backup, true);
        _entries.Add(new Entry(path, backup, existed, false));
    }

    public void CaptureDirectory(string path)
    {
        path = PathIdentity.NormalizeDirectory(path);
        if (!_captured.Add(path))
            return;

        var backup = Path.Combine(_backupRoot, _entries.Count.ToString("D6"));
        var existed = Directory.Exists(path);
        if (existed)
            CopyDirectory(path, backup);
        _entries.Add(new Entry(path, backup, existed, true));
    }

    public void Commit()
    {
        _committed = true;
        Cleanup();
    }

    public void Dispose()
    {
        if (!_committed)
            Rollback();
        Cleanup();
    }

    private void Rollback()
    {
        foreach (var entry in _entries.AsEnumerable().Reverse())
        {
            if (entry.IsDirectory)
            {
                if (Directory.Exists(entry.Path))
                    Directory.Delete(entry.Path, true);
                if (entry.Existed)
                    CopyDirectory(entry.BackupPath, entry.Path);
                continue;
            }

            if (File.Exists(entry.Path))
                File.SetAttributes(entry.Path, FileAttributes.Normal);
            if (entry.Existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Path)!);
                File.Copy(entry.BackupPath, entry.Path, true);
            }
            else if (File.Exists(entry.Path))
            {
                File.Delete(entry.Path);
            }
        }
    }

    private void Cleanup()
    {
        if (Directory.Exists(_backupRoot))
            Directory.Delete(_backupRoot, true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
