using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using TcoInstaller.Contracts;
using TcoInstaller.Backend;
using TcoInstaller.Models;

if (args.Length == 2 && args[0].Equals("--status", StringComparison.OrdinalIgnoreCase))
{
    var orchestrator = new InstallerOrchestrator();
    var snapshot = await orchestrator.RunAsync(
        new InstallerRequest(InstallerAction.Status, args[1], false, false),
        new Progress<InstallerProgress>(),
        _ => { },
        CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("embedded payload integrity", TestPayloadAsync),
    ("INI mutation and duplicate protection", TestIniAsync),
    ("path normalization and installation identity", TestPathNormalizationAsync),
    ("file transaction rollback and commit", TestTransactionAsync),
    ("engine configuration apply and rollback", TestEngineConfigurationAsync),
    ("engine configuration variants and durable restore", TestEngineConfigurationsAndRestoreAsync),
    ("independent ReShade and DXVK transitions", TestGraphicsTransitionsAsync),
    ("schema-1 graphics state migration", TestLegacyGraphicsStateMigrationAsync),
    ("configuration scan report", TestConfigurationReportAsync),
    ("release update staging and digest verification", TestReleaseUpdateAsync)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Environment.ExitCode = failures == 0 ? 0 : 1;

static async Task TestPayloadAsync()
{
    var payload = new PayloadStore();
    await payload.ValidateAsync();
    Assert(payload.Files.Any(), "No embedded payload files were found.");
    var presetFiles = payload.Files.Where(path => path.StartsWith(
        EngineConfigurationService.PresetFolder + "/",
        StringComparison.OrdinalIgnoreCase)).ToArray();
    Assert(presetFiles.Contains(EngineConfigurationService.PresetFolder + "/01-tco-standard.json", StringComparer.OrdinalIgnoreCase) &&
           presetFiles.Contains(EngineConfigurationService.PresetFolder + "/02-tco-no-dyn-light.json", StringComparer.OrdinalIgnoreCase),
        "The built-in engine configurations are not embedded.");
    Assert(payload.ReadText(presetFiles[0]).Contains("TCO Standard", StringComparison.Ordinal),
        "Editable engine configuration could not be read without an integrity entry.");
    Assert(payload.GetSha256("runtime/ReShade64.dll").Length == 64,
        "ReShade executable is missing from the integrity manifest.");
    Assert(payload.GetSha256("dxvk/d3d9.dll").Length == 64,
        "DXVK executable is missing from the integrity manifest.");
    Assert(!payload.Contains("engine-profile.json") && !payload.Contains("engine-profiles.json"),
        "The obsolete shared engine catalog is still embedded.");
}

static Task TestIniAsync()
{
    using var fixture = new TemporaryDirectory();
    var path = Path.Combine(fixture.Path, "test.ini");
    File.WriteAllText(path, "[One]\nValue=old\n\n[Two]\nOther=yes\n");
    IniFile.SetValue(path, "One", "Value", "new");
    IniFile.SetValue(path, "Three", "Added", "42");
    Assert(IniFile.GetValue(path, "One", "Value") == "new", "Existing INI value was not replaced.");
    Assert(IniFile.GetValue(path, "Two", "Other") == "yes", "Unrelated INI section changed.");
    Assert(IniFile.GetValue(path, "Three", "Added") == "42", "Missing INI section was not added.");

    File.WriteAllText(path, "[One]\nValue=a\nValue=b\n");
    AssertThrows<InvalidDataException>(() => IniFile.SetValue(path, "One", "Value", "c"));
    return Task.CompletedTask;
}

static Task TestTransactionAsync()
{
    using var fixture = new TemporaryDirectory();
    var existing = Path.Combine(fixture.Path, "existing.txt");
    var created = Path.Combine(fixture.Path, "created.txt");
    File.WriteAllText(existing, "before");
    using (var transaction = new FileTransaction())
    {
        transaction.CaptureFile(existing);
        transaction.CaptureFile(created);
        File.WriteAllText(existing, "after");
        File.WriteAllText(created, "new");
    }
    Assert(File.ReadAllText(existing) == "before", "Existing file was not rolled back.");
    Assert(!File.Exists(created), "Created file was not removed by rollback.");

    using (var transaction = new FileTransaction())
    {
        transaction.CaptureFile(existing);
        File.WriteAllText(existing, "committed");
        transaction.Commit();
    }
    Assert(File.ReadAllText(existing) == "committed", "Committed file was rolled back.");
    return Task.CompletedTask;
}

static Task TestPathNormalizationAsync()
{
    using var fixture = new TemporaryDirectory();
    var canonical = TeraPaths.NormalizeRoot(fixture.Path);
    var alternateSeparators = canonical.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var paths = new TeraPaths(alternateSeparators + Path.AltDirectorySeparatorChar + Path.AltDirectorySeparatorChar);

    Assert(paths.Root == canonical, "TERA root did not normalize separators and repeated trailing separators.");
    Assert(TeraPaths.NormalizeRoot(canonical.ToUpperInvariant())
        .Equals(paths.Root, StringComparison.OrdinalIgnoreCase), "TERA root identity became case-sensitive.");
    Assert(!TeraPaths.NormalizeRoot(fixture.Path + "-other")
        .Equals(paths.Root, StringComparison.OrdinalIgnoreCase), "Different installation roots collapsed to one identity.");
    return Task.CompletedTask;
}

static Task TestEngineConfigurationAsync()
{
    using var fixture = new TemporaryDirectory();
    var paths = CreateTeraFixture(fixture.Path);
    var originals = paths.ConfigFiles.ToDictionary(path => path, File.ReadAllText);
    var payload = new PayloadStore();
    var engine = new EngineConfigurationService(payload);
    using (var transaction = new FileTransaction())
    {
        engine.Apply(paths, transaction);
        Assert(engine.GetMismatches(paths).Count == 0, "Applied engine configuration has mismatches.");
        Assert(File.ReadLines(paths.SystemSettings).Contains("TEXTUREGROUP_World=(MinLODSize=1)"), "Texture group changed.");
    }
    foreach (var pair in originals)
        Assert(File.ReadAllText(pair.Key) == pair.Value, $"Rollback did not restore {Path.GetFileName(pair.Key)}.");
    Assert(!engine.HasValidBackup(paths), "Rolled-back engine apply left a durable backup state behind.");
    return Task.CompletedTask;
}

static Task TestEngineConfigurationsAndRestoreAsync()
{
    using var fixture = new TemporaryDirectory();
    var paths = CreateTeraFixture(fixture.Path);
    var originals = paths.ConfigFiles.ToDictionary(path => path, File.ReadAllText);
    var engine = new EngineConfigurationService(new PayloadStore());
    Assert(engine.Configurations.Select(profile => profile.Id).SequenceEqual(
        new[] { "tco-standard", "tco-no-dyn-light" }), "Engine configuration catalog is incomplete.");

    using (var transaction = new FileTransaction())
    {
        engine.Apply(paths, transaction, "tco-no-dyn-light", pcOnly: true);
        transaction.Commit();
    }
    Assert(engine.GetMismatches(paths, "tco-no-dyn-light").Count == 0,
        "The selected customizable engine configuration was not applied faithfully.");
    Assert(IniFile.GetValue(paths.S1Engine, "WinDrv.WindowsClient", "AllowJoystickInput") == "0", "PC Only was not enabled.");
    Assert(engine.HasValidBackup(paths), "Original engine backup was not captured.");
    Assert(engine.Inspect(paths) is { Id: "tco-no-dyn-light", PcOnly: true }, "Applied engine configuration or PC Only state was not detected.");

    var engineStatePath = Path.Combine(paths.Tools, "engine-profile-state.json");
    using (var state = JsonDocument.Parse(File.ReadAllText(engineStatePath)))
    {
        var legacyFiles = state.RootElement.GetProperty("BackupFiles").EnumerateObject()
            .Select(file => new { File = file.Name, BackupFile = file.Name, Sha256 = file.Value.GetString() })
            .ToArray();
        File.WriteAllText(engineStatePath, JsonSerializer.Serialize(new
        {
            Schema = 1,
            TeraRoot = paths.Root + Path.DirectorySeparatorChar,
            CreatedAt = DateTimeOffset.Now,
            Files = legacyFiles
        }));
    }
    Assert(engine.HasValidBackup(new TeraPaths(paths.Root + Path.DirectorySeparatorChar)),
        "The previous engine backup ledger is no longer readable with a trailing-separator root.");

    using (var transaction = new FileTransaction())
    {
        engine.Apply(paths, transaction, "tco-standard", pcOnly: false);
        transaction.Commit();
    }
    Assert(engine.GetMismatches(paths, "tco-standard").Count == 0,
        "TCO Standard was not applied from its current JSON content.");
    Assert(IniFile.GetValue(paths.S1Engine, "WinDrv.WindowsClient", "AllowJoystickInput") == "1", "PC Only was not disabled.");
    Assert(engine.Inspect(paths) is { Id: "tco-standard", PcOnly: false }, "TCO Standard was not detected.");

    using (var transaction = new FileTransaction())
    {
        engine.RestoreOriginal(paths, transaction);
        transaction.Commit();
    }
    foreach (var pair in originals)
        Assert(File.ReadAllText(pair.Key) == pair.Value, $"Durable restore did not recover {Path.GetFileName(pair.Key)}.");
    return Task.CompletedTask;
}

static async Task TestGraphicsTransitionsAsync()
{
    using var fixture = new TemporaryDirectory();
    var paths = CreateTeraFixture(fixture.Path);
    var payload = new PayloadStore();
    var engine = new EngineConfigurationService(payload);
    var registry = new FixtureVulkanRegistry();
    var displays = new FixtureDisplayResolution();
    var graphics = new GraphicsPipelineService(payload, engine, displays, registry);
    var status = new GraphicsStatusInspector(displays);

    using (var transaction = new FileTransaction())
    {
        await graphics.EnablePipelineAsync(paths, transaction, CancellationToken.None);
        transaction.Commit();
    }
    AssertState(status.Inspect(paths), reshade: true, dxvk: true, "complete pipeline");
    var reshadeStatePath = Path.Combine(paths.Tools, "reshade-configuration.json");
    var dxvkStatePath = Path.Combine(paths.Tools, "dxvk-configuration.json");
    Assert(File.Exists(reshadeStatePath), "Separated ReShade state was not created.");
    Assert(File.Exists(dxvkStatePath), "Separated DXVK state was not created.");
    var customReShadeSetting = "UserManagedSetting=keep-me";
    var customPresetSetting = "UserPresetSetting=keep-me";
    var customShader = Path.Combine(paths.Binaries, "reshade-shaders", "Shaders", "Deband.fx");
    File.AppendAllText(Path.Combine(paths.Binaries, "ReShade.ini"), Environment.NewLine + customReShadeSetting);
    File.AppendAllText(Path.Combine(paths.Binaries, "TERA_Natural_Clarity.ini"), Environment.NewLine + customPresetSetting);
    File.WriteAllText(customShader, "// user-customized shader");
    using (var transaction = new FileTransaction())
    {
        await graphics.EnablePipelineAsync(paths, transaction, CancellationToken.None);
        transaction.Commit();
    }
    Assert(File.ReadAllText(Path.Combine(paths.Binaries, "ReShade.ini")).Contains(customReShadeSetting, StringComparison.Ordinal),
        "Reapplying ReShade overwrote a user configuration value.");
    Assert(File.ReadAllText(Path.Combine(paths.Binaries, "TERA_Natural_Clarity.ini")).Contains(customPresetSetting, StringComparison.Ordinal),
        "Reapplying ReShade overwrote the user preset.");
    Assert(File.ReadAllText(customShader) == "// user-customized shader",
        "Reapplying ReShade overwrote a user-customized shader.");
    var reshadeState = JsonSerializer.Deserialize<ReShadeConfiguration>(File.ReadAllText(reshadeStatePath))!;
    var dxvkState = JsonSerializer.Deserialize<DxvkConfiguration>(File.ReadAllText(dxvkStatePath))!;
    File.WriteAllText(Path.Combine(paths.Tools, "tera-reshade-proxy-state.json"), JsonSerializer.Serialize(new
    {
        Schema = 2,
        CreatedAt = reshadeState.CapturedAt,
        TeraRoot = paths.Root,
        dxvkState.OriginalD3D9Kind,
        dxvkState.OriginalD3D9Backup,
        reshadeState.OriginalFxaa,
        DxvkSha256 = dxvkState.Sha256,
        ReShadeSha256 = reshadeState.Sha256,
        VulkanLayer64 = reshadeState.OriginalVulkanLayer64,
        VulkanLayer32 = reshadeState.OriginalVulkanLayer32
    }));
    File.Delete(reshadeStatePath);
    File.Delete(dxvkStatePath);

    using (var transaction = new FileTransaction())
    {
        graphics.DisableDxvk(paths, transaction);
        transaction.Commit();
    }
    AssertState(status.Inspect(paths), reshade: true, dxvk: false, "ReShade-only pipeline");

    using (var transaction = new FileTransaction())
    {
        await graphics.EnableDxvkAsync(paths, transaction, CancellationToken.None);
        transaction.Commit();
    }
    AssertState(status.Inspect(paths), reshade: true, dxvk: true, "reactivated DXVK pipeline");
    Assert(File.Exists(reshadeStatePath) && File.Exists(dxvkStatePath), "Legacy graphics state was not migrated.");

    using (var transaction = new FileTransaction())
    {
        graphics.DisableReShade(paths, transaction);
        transaction.Commit();
    }
    AssertState(status.Inspect(paths), reshade: false, dxvk: true, "DXVK-only pipeline");

    using (var transaction = new FileTransaction())
    {
        graphics.DisableDxvk(paths, transaction);
        transaction.Commit();
    }
    AssertState(status.Inspect(paths), reshade: false, dxvk: false, "native pipeline");

    using (var transaction = new FileTransaction())
    {
        await graphics.EnableReShadeAsync(paths, transaction, CancellationToken.None);
        transaction.Commit();
    }
    AssertState(status.Inspect(paths), reshade: true, dxvk: false, "ReShade reactivation without DXVK");
}

static async Task TestLegacyGraphicsStateMigrationAsync()
{
    using var fixture = new TemporaryDirectory();
    var selectedRoot = fixture.Path + Path.DirectorySeparatorChar;
    var paths = CreateTeraFixture(selectedRoot);
    Assert(paths.Root == Path.TrimEndingDirectorySeparator(selectedRoot), "TERA root retained a trailing separator.");
    var payload = new PayloadStore();
    var engine = new EngineConfigurationService(payload);
    var registry = new FixtureVulkanRegistry();
    var graphics = new GraphicsPipelineService(payload, engine, new FixtureDisplayResolution(), registry);
    Directory.CreateDirectory(paths.Tools);
    File.WriteAllText(Path.Combine(paths.Tools, "tera-reshade-proxy-state.json"), JsonSerializer.Serialize(new
    {
        Schema = 1,
        CreatedAt = "2026-08-29T18:29:43+02:00",
        TeraRoot = paths.Root + Path.DirectorySeparatorChar,
        OriginalFXAA = "True",
        DXVKSHA256 = payload.GetSha256("dxvk/d3d9.dll"),
        ReShadeSHA256 = payload.GetSha256("runtime/ReShade64.dll"),
        VulkanLayer64 = 0,
        VulkanLayer32 = 0
    }));

    using (var transaction = new FileTransaction())
    {
        await graphics.EnablePipelineAsync(paths, transaction, CancellationToken.None);
        transaction.Commit();
    }

    var reshade = JsonSerializer.Deserialize<ReShadeConfiguration>(
        File.ReadAllText(Path.Combine(paths.Tools, "reshade-configuration.json")))!;
    var dxvk = JsonSerializer.Deserialize<DxvkConfiguration>(
        File.ReadAllText(Path.Combine(paths.Tools, "dxvk-configuration.json")))!;
    Assert(reshade.OriginalFxaa == "True", "Legacy OriginalFXAA was not migrated case-insensitively.");
    Assert(reshade.Sha256 == payload.GetSha256("runtime/ReShade64.dll"), "Legacy ReShadeSHA256 was not migrated.");
    Assert(dxvk.Sha256 == payload.GetSha256("dxvk/d3d9.dll"), "Legacy DXVKSHA256 was not migrated.");
    Assert(dxvk.OriginalD3D9Kind == "DXVK", "Schema-1 proxy state did not retain its implicit DXVK origin.");
}

static void AssertState(
    (ReShadeConfiguration ReShade, DxvkConfiguration Dxvk) status,
    bool reshade,
    bool dxvk,
    string label)
{
    Assert(status.ReShade.Active == reshade, $"Unexpected ReShade state for {label}.");
    Assert(status.Dxvk.Active == dxvk, $"Unexpected DXVK state for {label}.");
}

static Task TestConfigurationReportAsync()
{
    var snapshot = new InstallationSnapshot(
        @"S:\TERA",
        "ReShade D3D9 -> DXVK -> Vulkan",
        new EngineConfiguration { Id = "tco-standard", Name = "TCO Standard", ChecksPassed = 10, ChecksTotal = 10, PcOnly = true },
        new ReShadeConfiguration { Active = true, ActiveD3D9 = "ReShade", DepthFormat = "D24S8", DepthResolution = "1920x1080" },
        new DxvkConfiguration { Active = true, ProxyTarget = "DXVK" },
        new ClassicPlusConfiguration { TccInstalled = true, ShinraInstalled = true });
    var report = StatusService.FormatMarkdown(snapshot, DateTimeOffset.Parse("2026-09-02T12:00:00Z"));
    Assert(report.Contains("| Engine configuration | Yes |", StringComparison.Ordinal), "Report omitted engine detection.");
    Assert(report.Contains("- PC Only: Enabled", StringComparison.Ordinal), "Report omitted PC Only state.");
    Assert(report.Contains("| ReShade | Yes | Active |", StringComparison.Ordinal), "Report omitted ReShade detection.");
    Assert(report.Contains("| DXVK | Yes | Active |", StringComparison.Ordinal), "Report omitted DXVK detection.");
    Assert(report.Contains("| TCC | Yes | Detected |", StringComparison.Ordinal), "Report omitted TCC detection.");
    Assert(report.Contains("| Shinra | Yes | Detected |", StringComparison.Ordinal), "Report omitted Shinra detection.");
    return Task.CompletedTask;
}

static async Task TestReleaseUpdateAsync()
{
    using var fixture = new TemporaryDirectory();
    var executable = Encoding.UTF8.GetBytes("verified update fixture");
    var hash = Convert.ToHexString(SHA256.HashData(executable));
    var release = JsonSerializer.Serialize(new
    {
        tag_name = "v99.0.0",
        assets = new[]
        {
            new
            {
                name = "TCO.Installer-win-x64.exe",
                browser_download_url = "https://downloads.example/TCO.Installer-win-x64.exe",
                digest = "sha256:" + hash
            }
        }
    });
    using var http = new HttpClient(new FixtureHttpHandler(release, executable));
    var service = new ReleaseUpdateService(http, fixture.Path);
    var staged = await service.CheckAndStageAsync(CancellationToken.None);
    Assert(staged is not null, "A newer release was not staged.");
    Assert(staged!.Sha256 == hash, "Staged update hash was not retained.");
    Assert(File.ReadAllBytes(staged.ExecutablePath).SequenceEqual(executable), "Staged executable bytes changed.");
}

static TeraPaths CreateTeraFixture(string root)
{
    var paths = new TeraPaths(root);
    Directory.CreateDirectory(paths.Binaries);
    Directory.CreateDirectory(paths.Config);
    Directory.CreateDirectory(paths.EngineConfig);
    File.WriteAllBytes(paths.TeraExecutable, []);
    foreach (var path in paths.ConfigFiles)
        File.WriteAllText(path, "[Seed]\nValue=1\n");
    File.AppendAllText(paths.SystemSettings, "TEXTUREGROUP_World=(MinLODSize=1)\n");
    return paths;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown.");
}

sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tco-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, true);
    }
}

sealed class FixtureHttpHandler(string releaseJson, byte[] executable) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.RequestUri?.Host == "api.github.com"
            ? new StringContent(releaseJson, Encoding.UTF8, "application/json")
            : new ByteArrayContent(executable);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }
}

sealed class FixtureDisplayResolution : IDisplayResolutionService
{
    public DisplayResolution GetPrimaryResolution() => new(1920, 1080);
}

sealed class FixtureVulkanRegistry : IVulkanLayerRegistry
{
    private int? _value64;
    private int? _value32;
    public int? Get64() => _value64;
    public int? Get32() => _value32;
    public void Set64(int? value) => _value64 = value;
    public void Set32(int? value) => _value32 = value;
}
