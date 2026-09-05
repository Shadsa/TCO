using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TcoInstaller.Backend;

/// <summary>Abstracts primary-display mode lookup for production code and deterministic tests.</summary>
public interface IDisplayResolutionService
{
    DisplayResolution GetPrimaryResolution();
}

/// <summary>Reads the active primary display resolution and refresh rate through the Windows display API.</summary>
public sealed class DisplayResolutionService : IDisplayResolutionService
{
    private const int EnumCurrentSettings = -1;

    public DisplayResolution GetPrimaryResolution()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("TERA display detection requires Windows.");

        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the primary display mode.");
        if (mode.PelsWidth <= 0 || mode.PelsHeight <= 0 || mode.DisplayFrequency is <= 1 or > 1000)
            throw new InvalidOperationException("The primary display returned an invalid display mode.");
        return new DisplayResolution(mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }
}

/// <summary>Primary display mode used by Generic Depth filtering and the engine FPS cap.</summary>
public sealed record DisplayResolution(int Width, int Height, int RefreshRateHz)
{
    public override string ToString() => $"{Width}x{Height}";
}
