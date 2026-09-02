using Microsoft.Win32;

namespace TcoInstaller.Backend;

/// <summary>Abstracts the two ReShade Vulkan layer registry views for rollback and tests.</summary>
public interface IVulkanLayerRegistry
{
    int? Get64();
    int? Get32();
    void Set64(int? value);
    void Set32(int? value);
}

/// <summary>Reads and writes ReShade's 32-bit and 64-bit implicit Vulkan layer values.</summary>
public sealed class VulkanLayerRegistry : IVulkanLayerRegistry
{
    private const string KeyPath = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
    private const string Layer64Name = @"C:\ProgramData\ReShade\ReShade64.json";
    private const string Layer32Name = @"C:\ProgramData\ReShade\ReShade32.json";

    public int? Get64() => Get(RegistryView.Registry64, Layer64Name);
    public int? Get32() => Get(RegistryView.Registry32, Layer32Name);
    public void Set64(int? value) => Set(RegistryView.Registry64, Layer64Name, value);
    public void Set32(int? value) => Set(RegistryView.Registry32, Layer32Name, value);

    private static int? Get(RegistryView view, string name)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = baseKey.OpenSubKey(KeyPath, false);
        return key?.GetValue(name) is int value ? value : null;
    }

    private static void Set(RegistryView view, string name, int? value)
    {
        if (!OperatingSystem.IsWindows() || value is null)
            return;
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = baseKey.OpenSubKey(KeyPath, true);
        if (key?.GetValue(name) is null)
            return;
        key.SetValue(name, value.Value, RegistryValueKind.DWord);
    }
}
