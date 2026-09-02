namespace TcoInstaller.Backend;

/// <summary>Resolves and validates every supported path beneath one TERA installation root.</summary>
public sealed class TeraPaths
{
    public TeraPaths(string root)
    {
        Root = NormalizeRoot(root);
        Binaries = Path.Combine(Root, "Binaries");
        Tools = Path.Combine(Root, "ReShadeTools");
        Config = Path.Combine(Root, "S1Game", "Config");
        EngineConfig = Path.Combine(Root, "Engine", "Config");
        TeraExecutable = Path.Combine(Binaries, "TERA.exe");
        S1Engine = Path.Combine(Config, "S1Engine.ini");
        SystemSettings = Path.Combine(Config, "S1SystemSettings.ini");
        S1Option = Path.Combine(Config, "S1Option.ini");
        S1Input = Path.Combine(Config, "S1Input.ini");
        BaseInput = Path.Combine(EngineConfig, "BaseInput.ini");
    }

    public string Root { get; }
    public string Binaries { get; }
    public string Tools { get; }
    public string Config { get; }
    public string EngineConfig { get; }
    public string TeraExecutable { get; }
    public string S1Engine { get; }
    public string SystemSettings { get; }
    public string S1Option { get; }
    public string S1Input { get; }
    public string BaseInput { get; }

    public IReadOnlyDictionary<string, string> EngineFiles => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["S1Engine.ini"] = S1Engine,
        ["S1SystemSettings.ini"] = SystemSettings,
        ["S1Option.ini"] = S1Option,
        ["BaseInput.ini"] = BaseInput,
        ["S1Input.ini"] = S1Input
    };

    public IReadOnlyList<string> ConfigFiles => [S1Engine, SystemSettings, S1Option, S1Input, BaseInput];

    /// <summary>Returns one stable installation identity regardless of a trailing directory separator.</summary>
    public static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public void Validate()
    {
        if (!Directory.Exists(Binaries) || !Directory.Exists(Config))
            throw new DirectoryNotFoundException($"The selected directory is not a TERA installation: {Root}");

        foreach (var path in ConfigFiles.Prepend(TeraExecutable))
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("A required TERA file is missing.", path);
        }
    }
}
