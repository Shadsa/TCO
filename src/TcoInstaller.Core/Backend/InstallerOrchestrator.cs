using TcoInstaller.Contracts;
using TcoInstaller.Models;

namespace TcoInstaller.Backend;

/// <summary>
/// Application entry point for every installer action. The workflow is update check, preflight,
/// guarded mutation, read-only verification, commit, and final lock restoration.
/// </summary>
public sealed class InstallerOrchestrator
{
    private readonly PayloadStore _payload;
    private readonly EngineConfigurationService _engine;
    private readonly GraphicsPipelineService _graphics;
    private readonly ClassicPlusService _classicPlus;
    private readonly StatusService _status;
    private readonly ReleaseUpdateService _updates;

    public InstallerOrchestrator()
    {
        _payload = new PayloadStore();
        _engine = new EngineConfigurationService(_payload);
        _classicPlus = new ClassicPlusService(_payload);
        var displays = new DisplayResolutionService();
        var registry = new VulkanLayerRegistry();
        _graphics = new GraphicsPipelineService(_payload, _engine, displays, registry);
        _status = new StatusService(_engine, new GraphicsStatusInspector(displays), _classicPlus);
        _updates = new ReleaseUpdateService();
    }

    public IReadOnlyList<EngineConfiguration> EngineConfigurations => _engine.Configurations;
    public string DefaultEngineConfigurationId => _engine.DefaultConfigurationId;

    public async Task<InstallationSnapshot?> RunAsync(
        InstallerRequest request,
        IProgress<InstallerProgress> progress,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The TCO installation backend requires Windows.");

        var paths = new TeraPaths(request.TeraRoot);
        if (request.Action == InstallerAction.Apply && request.CheckForUpdates)
        {
            Report(progress, log, "update", "started", "Checking GitHub for a SHA-256-verified TCO executable.");
            try
            {
                var update = await _updates.CheckAndStageAsync(cancellationToken);
                if (update is not null)
                {
                    Report(progress, log, "update", "completed", $"TCO {update.Version} downloaded and verified; restarting to install it.");
                    throw new UpdateStagedException(update);
                }
                Report(progress, log, "update", "completed", "This TCO executable is current.");
            }
            catch (UpdateStagedException) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                Report(progress, log, "update", "skipped", $"Update was not applied; continuing with the embedded package. {exception.Message}");
            }
        }
        else
        {
            Report(progress, log, "update", "skipped", "No update is required for this action.");
        }
        Report(progress, log, "preflight", "started", "Validating executable payloads and target installation.");
        paths.Validate();
        await _payload.ValidateAsync(cancellationToken);
        Report(progress, log, "preflight", "completed", $"Embedded TCO {_payload.Version} runtime integrity validated.");

        try
        {
            var context = new OperationContext(paths, request, progress, log, cancellationToken);
            return request.Action switch
            {
                InstallerAction.Apply => await ApplyAsync(context),
                InstallerAction.ApplyEngine => await ApplyEngineAsync(context),
                InstallerAction.RestoreEngine => await RestoreEngineAsync(context),
                InstallerAction.Status => Inspect(context),
                InstallerAction.EnableReShade => await EnableReShadeAsync(context),
                InstallerAction.DisableReShade => await DisableReShadeAsync(context),
                InstallerAction.EnableDxvk => await EnableDxvkAsync(context),
                InstallerAction.DisableDxvk => await DisableDxvkAsync(context),
                InstallerAction.RestoreReShade => await RestoreGraphicsAsync(context),
                InstallerAction.ApplyClassicPlus => await ApplyClassicPlusAsync(context),
                InstallerAction.ExportClassicPlus => ExportClassicPlus(context),
                InstallerAction.LockConfigs => SetLocks(paths, true, progress, log),
                InstallerAction.UnlockConfigs => SetLocks(paths, false, progress, log),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Action))
            };
        }
        catch (Exception exception)
        {
            Report(progress, log, CurrentPhase(request.Action), "failed", exception.Message, true);
            throw;
        }
    }

    private async Task<InstallationSnapshot> ApplyAsync(OperationContext context)
    {
        ProcessGuard.AssertClosed(context.Request.IncludeClassicPlus);
        if (context.Request.IncludeClassicPlus) _classicPlus.AssertInstalled();
        var configuration = _engine.Resolve(context.Request.EngineConfigurationId);
        return await ExecuteMutationAsync(context, true, async transaction =>
        {
            context.Report("engine", "started", $"Applying {configuration.Name}.");
            _engine.Apply(context.Paths, transaction, configuration.Id, context.Request.PcOnly);
            context.Report("engine", "completed", $"Applied {_engine.GetCount(configuration.Id)} engine settings from {configuration.Name}; PC Only is {(context.Request.PcOnly ? "enabled" : "disabled")}.");
            context.Report("graphics", "started", "Installing and validating DXVK and ReShade.");
            await _graphics.EnablePipelineAsync(context.Paths, transaction, context.CancellationToken);
            context.Report("graphics", "completed", "DXVK and ReShade installed.");
            if (context.Request.IncludeClassicPlus)
            {
                context.Report("classicplus", "started", "Applying TCC and Shinra profiles.");
                await _classicPlus.InstallAsync(transaction, context.CancellationToken);
                context.Report("classicplus", "completed", "TCC and Shinra profiles applied.");
            }
            else context.Skip("classicplus", "Classic+ configuration was not requested.");
        });
    }

    private Task<InstallationSnapshot> ApplyEngineAsync(OperationContext context)
    {
        ProcessGuard.AssertClosed(false);
        var configuration = _engine.Resolve(context.Request.EngineConfigurationId);
        return ExecuteMutationAsync(context, true, transaction =>
        {
            context.Report("engine", "started", $"Applying {configuration.Name}.");
            _engine.Apply(context.Paths, transaction, configuration.Id, context.Request.PcOnly);
            context.Report("engine", "completed", $"Applied {_engine.GetCount(configuration.Id)} engine settings from {configuration.Name}; PC Only is {(context.Request.PcOnly ? "enabled" : "disabled")}.");
            context.Skip("graphics", "Graphics configuration is not part of this action.");
            context.Skip("classicplus", "Classic+ configuration is not part of this action.");
            return Task.CompletedTask;
        });
    }

    private Task<InstallationSnapshot> RestoreEngineAsync(OperationContext context)
    {
        ProcessGuard.AssertClosed(false);
        return ExecuteMutationAsync(context, true, transaction =>
        {
            context.Report("engine", "started", "Restoring the engine configuration captured before TCO's first native apply.");
            _engine.RestoreOriginal(context.Paths, transaction);
            context.Report("engine", "completed", "Original engine configuration restored.");
            context.Skip("graphics", "Graphics configuration is not part of this action.");
            context.Skip("classicplus", "Classic+ configuration is not part of this action.");
            return Task.CompletedTask;
        });
    }

    private Task<InstallationSnapshot> EnableReShadeAsync(OperationContext context) =>
        ExecuteGraphicsMutationAsync(context, true, "Activating ReShade while preserving the current DXVK state.", "ReShade activated.",
            transaction => _graphics.EnableReShadeAsync(context.Paths, transaction, context.CancellationToken));

    private Task<InstallationSnapshot> DisableReShadeAsync(OperationContext context) =>
        ExecuteGraphicsMutationAsync(context, true, "Deactivating ReShade while preserving the current DXVK state.", "ReShade deactivated.",
            transaction => { _graphics.DisableReShade(context.Paths, transaction); return Task.CompletedTask; });

    private Task<InstallationSnapshot> EnableDxvkAsync(OperationContext context) =>
        ExecuteGraphicsMutationAsync(context, false, "Activating DXVK while preserving the current ReShade state.", "DXVK activated.",
            transaction => _graphics.EnableDxvkAsync(context.Paths, transaction, context.CancellationToken));

    private Task<InstallationSnapshot> DisableDxvkAsync(OperationContext context) =>
        ExecuteGraphicsMutationAsync(context, false, "Deactivating DXVK while preserving the current ReShade state.", "DXVK deactivated.",
            transaction => { _graphics.DisableDxvk(context.Paths, transaction); return Task.CompletedTask; });

    private Task<InstallationSnapshot> RestoreGraphicsAsync(OperationContext context) =>
        ExecuteGraphicsMutationAsync(context, true, "Restoring the original D3D9 state.", "Original D3D9 state restored.",
            transaction => { _graphics.Restore(context.Paths, transaction); return Task.CompletedTask; });

    private Task<InstallationSnapshot> ExecuteGraphicsMutationAsync(
        OperationContext context,
        bool manageConfigLocks,
        string startedMessage,
        string completedMessage,
        Func<FileTransaction, Task> mutate)
    {
        ProcessGuard.AssertClosed(false);
        context.Skip("engine", "Engine configuration is not part of this action.");
        return ExecuteMutationAsync(context, manageConfigLocks, async transaction =>
        {
            context.Report("graphics", "started", startedMessage);
            await mutate(transaction);
            context.Report("graphics", "completed", completedMessage);
            context.Skip("classicplus", "Classic+ configuration is not part of this action.");
        });
    }

    private Task<InstallationSnapshot> ApplyClassicPlusAsync(OperationContext context)
    {
        ProcessGuard.AssertClosed(true);
        _classicPlus.AssertInstalled();
        context.Skip("engine", "Engine configuration is not part of this action.");
        context.Skip("graphics", "Graphics configuration is not part of this action.");
        return ExecuteMutationAsync(context, false, async transaction =>
        {
            context.Report("classicplus", "started", "Applying TCC and Shinra profiles.");
            await _classicPlus.InstallAsync(transaction, context.CancellationToken);
            context.Report("classicplus", "completed", "TCC and Shinra profiles applied.");
        });
    }

    private InstallationSnapshot ExportClassicPlus(OperationContext context)
    {
        context.Skip("engine", "Engine configuration is not part of this action.");
        context.Skip("graphics", "Graphics configuration is not part of this action.");
        using var transaction = new FileTransaction();
        context.Report("classicplus", "started", "Exporting a sanitized Classic+ override profile.");
        var destination = _classicPlus.Export(transaction);
        context.Report("classicplus", "completed", $"Sanitized Classic+ profile saved to {destination}.");
        context.Skip("verification", "No installed TERA configuration was changed.");
        transaction.Commit();
        return _status.Inspect(context.Paths);
    }

    private InstallationSnapshot SetLocks(TeraPaths paths, bool locked, IProgress<InstallerProgress> progress, Action<string> log)
    {
        ProcessGuard.AssertClosed(false);
        Skip(progress, log, "engine", "No engine values are being changed.");
        Skip(progress, log, "graphics", "Graphics files are not part of this action.");
        Skip(progress, log, "classicplus", "Classic+ configuration is not part of this action.");
        Report(progress, log, "verification", "started", locked ? "Locking configuration files." : "Unlocking configuration files.");
        SetConfigLock(paths, locked);
        var snapshot = _status.Inspect(paths);
        Report(progress, log, "verification", "completed", locked ? "Configuration files locked." : "Configuration files unlocked.");
        return snapshot;
    }

    private InstallationSnapshot Inspect(OperationContext context)
    {
        context.Skip("engine", "Status mode does not change engine values.");
        context.Skip("graphics", "Status mode does not change graphics files.");
        context.Skip("classicplus", "Status mode does not change Classic+ profiles.");
        return Verify(context.Paths, context.Progress, context.Log);
    }

    /// <summary>
    /// Provides the common transaction boundary. Any exception before Commit rolls captured files
    /// back; managed INIs are re-locked in the finally block even when verification fails.
    /// </summary>
    private async Task<InstallationSnapshot> ExecuteMutationAsync(
        OperationContext context,
        bool manageConfigLocks,
        Func<FileTransaction, Task> mutate)
    {
        using var transaction = new FileTransaction();
        try
        {
            if (manageConfigLocks) SetConfigLock(context.Paths, false);
            await mutate(transaction);
            if (manageConfigLocks) SetConfigLock(context.Paths, true);
            var snapshot = Verify(context.Paths, context.Progress, context.Log);
            transaction.Commit();
            return snapshot;
        }
        finally
        {
            if (manageConfigLocks) SetConfigLock(context.Paths, true);
        }
    }

    private InstallationSnapshot Verify(TeraPaths paths, IProgress<InstallerProgress> progress, Action<string> log)
    {
        Report(progress, log, "verification", "started", "Inspecting the current configuration.");
        var snapshot = _status.Inspect(paths);
        log(StatusService.Format(snapshot));
        Report(progress, log, "verification", "completed", "Configuration inspection completed.");
        return snapshot;
    }

    private static void SetConfigLock(TeraPaths paths, bool locked)
    {
        foreach (var path in paths.ConfigFiles.Where(File.Exists))
        {
            var attributes = File.GetAttributes(path);
            File.SetAttributes(path, locked ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void Report(IProgress<InstallerProgress> progress, Action<string> log, string phase, string status, string message, bool error = false)
    {
        log($"[{phase}] [{status}] {message}");
        progress.Report(new InstallerProgress(phase, status, message, error));
    }

    private static void Skip(IProgress<InstallerProgress> progress, Action<string> log, string phase, string message) =>
        Report(progress, log, phase, "skipped", message);

    private static string CurrentPhase(InstallerAction action) => action switch
    {
        InstallerAction.Apply => "verification",
        InstallerAction.ApplyEngine or InstallerAction.RestoreEngine => "engine",
        InstallerAction.EnableReShade or InstallerAction.DisableReShade or InstallerAction.RestoreReShade or
            InstallerAction.EnableDxvk or InstallerAction.DisableDxvk => "graphics",
        InstallerAction.ApplyClassicPlus or InstallerAction.ExportClassicPlus => "classicplus",
        _ => "verification"
    };

    private sealed record OperationContext(
        TeraPaths Paths,
        InstallerRequest Request,
        IProgress<InstallerProgress> Progress,
        Action<string> Log,
        CancellationToken CancellationToken)
    {
        public void Report(string phase, string status, string message, bool error = false) =>
            InstallerOrchestrator.Report(Progress, Log, phase, status, message, error);

        public void Skip(string phase, string message) =>
            InstallerOrchestrator.Skip(Progress, Log, phase, message);
    }
}
