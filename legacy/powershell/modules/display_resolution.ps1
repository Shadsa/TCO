#Requires -Version 5.1

function Get-PrimaryDisplayResolution {
    if (-not ('TeraGraphics.NativeDisplay' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TeraGraphics
{
    public static class NativeDisplay
    {
        private const int EnumCurrentSettings = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
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
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FormName;
            public short LogPixels;
            public int BitsPerPel;
            public int PelsWidth;
            public int PelsHeight;
            public int DisplayFlags;
            public int DisplayFrequency;
            public int ICMMethod;
            public int ICMIntent;
            public int MediaType;
            public int DitherType;
            public int Reserved1;
            public int Reserved2;
            public int PanningWidth;
            public int PanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool EnumDisplaySettings(
            string deviceName,
            int modeNum,
            ref DevMode devMode);

        public static int[] GetPrimaryResolution()
        {
            DevMode mode = new DevMode();
            mode.Size = (short)Marshal.SizeOf(typeof(DevMode));
            if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read the primary display mode.");
            }
            if (mode.PelsWidth <= 0 || mode.PelsHeight <= 0)
            {
                throw new InvalidOperationException("The primary display returned an invalid resolution.");
            }
            return new[] { mode.PelsWidth, mode.PelsHeight };
        }
    }
}
'@
    }

    $resolution = [TeraGraphics.NativeDisplay]::GetPrimaryResolution()
    return [pscustomobject]@{
        Width = [int]$resolution[0]
        Height = [int]$resolution[1]
    }
}
