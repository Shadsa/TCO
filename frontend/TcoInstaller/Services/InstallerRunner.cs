using System.Text;
using TcoInstaller.Backend;
using TcoInstaller.Contracts;

namespace TcoInstaller.Services;

/// <summary>Runs the in-process orchestrator and records a timestamped per-action transcript.</summary>
public sealed class InstallerRunner
{
    private readonly InstallerOrchestrator _orchestrator;

    public InstallerRunner(InstallerOrchestrator orchestrator) => _orchestrator = orchestrator;

    public async Task<InstallerRunResult> RunAsync(
        InstallerRequest request,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppStorage.Logs);
        var logPath = Path.Combine(AppStorage.Logs, $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var sync = new object();

        void Log(string message)
        {
            var line = $"{DateTimeOffset.Now:o} {message}{Environment.NewLine}";
            lock (sync)
                File.AppendAllText(logPath, line, new UTF8Encoding(false));
        }

        Log($"TCO native installer; Action={request.Action}; TeraRoot={request.TeraRoot}; IncludeClassicPlus={request.IncludeClassicPlus}; NoBlur={request.NoBlur}");
        try
        {
            var snapshot = await _orchestrator.RunAsync(request, progress, Log, cancellationToken);
            Log("Installer action completed successfully.");
            string? reportPath = null;
            if (request.Action == InstallerAction.Status && snapshot is not null)
            {
                Directory.CreateDirectory(AppStorage.Reports);
                reportPath = Path.Combine(AppStorage.Reports, $"configuration-report-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                File.WriteAllText(reportPath, StatusService.FormatMarkdown(snapshot, DateTimeOffset.Now), new UTF8Encoding(false));
                Log($"Configuration report written to {reportPath}");
            }
            return new InstallerRunResult(0, logPath, snapshot, ReportPath: reportPath);
        }
        catch (UpdateStagedException exception)
        {
            Log($"Update {exception.Update.Version} staged successfully; handing off executable replacement.");
            return new InstallerRunResult(0, logPath, null, exception.Update);
        }
        catch (Exception exception)
        {
            Log(exception.ToString());
            throw;
        }
    }
}
