using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TcoInstaller.Models;

namespace TcoInstaller.Services;

public sealed class InstallerRunner
{
    private const string EventPrefix = "TCO_EVENT ";

    public async Task<InstallerRunResult> RunAsync(
        string packageRoot,
        InstallerRequest request,
        IProgress<InstallerOutput> progress,
        CancellationToken cancellationToken = default)
    {
        var scriptPath = Path.Combine(packageRoot, "Install.ps1");
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("Install.ps1 was not found.", scriptPath);

        var logRoot = Path.Combine(packageRoot, "logs");
        Directory.CreateDirectory(logRoot);
        var logPath = Path.Combine(logRoot, $"install-ui-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var startInfo = new ProcessStartInfo
        {
            FileName = GetPowerShellPath(),
            WorkingDirectory = packageRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        AddArgument(startInfo, "-NoProfile");
        AddArgument(startInfo, "-ExecutionPolicy", "Bypass");
        AddArgument(startInfo, "-NonInteractive");
        AddArgument(startInfo, "-File", scriptPath);
        AddArgument(startInfo, "-Action", request.Action);
        AddArgument(startInfo, "-TeraRoot", request.TeraRoot);
        AddArgument(startInfo, "-LogPath", logPath);
        AddArgument(startInfo, "-OutputMode", "JsonLines");

        if (request.IncludeClassicPlus)
            AddArgument(startInfo, "-IncludeClassicPlus");
        if (!request.CheckForUpdates)
            AddArgument(startInfo, "-SkipUpdate");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("PowerShell could not be started.");

        var standardOutput = DrainAsync(process.StandardOutput, false, progress, cancellationToken);
        var standardError = DrainAsync(process.StandardError, true, progress, cancellationToken);
        await Task.WhenAll(standardOutput, standardError, process.WaitForExitAsync(cancellationToken));
        return new InstallerRunResult(process.ExitCode, logPath);
    }

    private static async Task DrainAsync(
        StreamReader reader,
        bool isError,
        IProgress<InstallerOutput> progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith(EventPrefix, StringComparison.Ordinal))
            {
                try
                {
                    var installerEvent = JsonSerializer.Deserialize<InstallerEvent>(
                        line[EventPrefix.Length..],
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    progress.Report(new InstallerOutput(line, isError, installerEvent));
                    continue;
                }
                catch (JsonException)
                {
                    // Preserve malformed event output as a normal log line.
                }
            }

            progress.Report(new InstallerOutput(line, isError));
        }
    }

    private static string GetPowerShellPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        return "pwsh";
    }

    private static void AddArgument(ProcessStartInfo startInfo, params string[] values)
    {
        foreach (var value in values)
            startInfo.ArgumentList.Add(value);
    }
}
