using System.Runtime.InteropServices;

namespace SlideColours.Services;

/// <summary>
/// Puts a human name and a truthful pixel size on a GDI display device (<c>\\.\DISPLAY3</c>).
///
/// DXGI is no help for either. Its <c>DesktopCoordinates</c> are virtual-desktop coordinates, and
/// because WPF apps are only system-DPI aware, Windows rescales the coordinates of any monitor
/// whose scaling differs from the primary's — a 1920x1080 projector on a machine running the
/// desktop at 125% comes back as 2400x1350. The display mode below is real hardware pixels, and
/// the EDID name is what's printed on the monitor.
/// </summary>
public static class MonitorInfo
{
    /// <summary>
    /// How a display identifies itself: the monitor's EDID name ("BenQ TK800M"), and how it's
    /// plugged in ("HDMI"). Either can be null — plenty of panels, including most built-in laptop
    /// screens, carry no name at all, and then the connection is the useful hint.
    /// </summary>
    public sealed record MonitorDetail(string? Name, string? Connection);

    /// <summary>"Display 3" from "\\.\DISPLAY3" — the number Windows shows in its display settings.</summary>
    public static string ShortName(string gdiDeviceName)
    {
        string digits = new(gdiDeviceName.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? $"Display {digits}" : gdiDeviceName;
    }

    /// <summary>
    /// "Display 3 — BenQ TK800M, HDMI" for a display that may not be plugged in right now. Used for
    /// tooltips and for a saved display that has since been disconnected.
    /// </summary>
    public static string Describe(string gdiDeviceName, string? suffix)
    {
        var monitor = Describe(gdiDeviceName);
        string? who = monitor.Name is { } n && monitor.Connection is { } c ? $"{n}, {c}"
                    : monitor.Name ?? monitor.Connection;

        string label = who is null ? ShortName(gdiDeviceName) : $"{ShortName(gdiDeviceName)} — {who}";
        return suffix is null ? label : $"{label} ({suffix})";
    }

    public static MonitorDetail Describe(string gdiDeviceName)
    {
        try
        {
            foreach (var path in ActivePaths())
            {
                if (!SourceGdiName(path).Equals(gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = TargetFriendlyName(path);
                return new MonitorDetail(
                    string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    ConnectionName(path.targetInfo.outputTechnology));
            }
        }
        catch
        {
            // Any interop failure just means we fall back to the plain "Display 3" label.
        }
        return new MonitorDetail(null, null);
    }

    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY, as something an operator would recognise.</summary>
    private static string? ConnectionName(uint technology) => technology switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "DisplayPort (embedded)",
        15 => "Wireless display",
        0x80000000 => "Built-in display",
        _ => null,
    };

    /// <summary>The display's real resolution in hardware pixels, or null if it can't be read.</summary>
    public static (int Width, int Height)? Resolution(string gdiDeviceName)
    {
        try
        {
            var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(gdiDeviceName, ENUM_CURRENT_SETTINGS, ref mode) &&
                mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
            {
                return ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    // ---- display configuration -------------------------------------------------------------

    private static IEnumerable<DISPLAYCONFIG_PATH_INFO> ActivePaths()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
            yield break;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            yield break;

        for (int i = 0; i < pathCount; i++)
            yield return paths[i];
    }

    private static string SourceGdiName(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            },
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.viewGdiDeviceName : "";
    }

    private static string TargetFriendlyName(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.monitorFriendlyDeviceName : "";
    }

    // ---- native ------------------------------------------------------------------------------

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint modeCount,
        [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        // DISPLAYCONFIG_RATIONAL is two UINT32s. It must stay two 4-byte fields: a single ulong
        // here raises the struct's alignment to 8 and pads DISPLAYCONFIG_PATH_INFO from 72 to 88
        // bytes, which silently corrupts every path past the first.
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>Only the header is read; the trailing union is skipped via the explicit size.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
