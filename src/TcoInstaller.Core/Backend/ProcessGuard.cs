using System.Diagnostics;

namespace TcoInstaller.Backend;

/// <summary>Prevents configuration changes while TERA or optional companion tools own their files.</summary>
public static class ProcessGuard
{
    private static readonly string[] TeraProcesses = ["TERA", "noctenium", "TERA Europe Classic+ Launcher"];
    private static readonly string[] ClassicPlusProcesses = ["TCC", "ShinraMeter"];

    public static void AssertClosed(bool includeClassicPlus)
    {
        var blocked = TeraProcesses.Concat(includeClassicPlus ? ClassicPlusProcesses : []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = Process.GetProcesses()
            .Select(process =>
            {
                try { return process.ProcessName; }
                catch { return string.Empty; }
                finally { process.Dispose(); }
            })
            .Where(blocked.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (running.Length > 0)
            throw new InvalidOperationException($"Close the affected TERA/Classic+ processes first. Running: {string.Join(", ", running)}");
    }
}
