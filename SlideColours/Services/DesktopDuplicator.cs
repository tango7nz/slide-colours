using System.Runtime.InteropServices;

namespace SlideColours.Services;

/// <summary>A display that can be captured, as reported by DXGI.</summary>
public sealed record CaptureDisplay(string DeviceName, int Left, int Top, int Width, int Height)
{
    public bool IsPrimary => Left == 0 && Top == 0;

    /// <summary>"Display 2" — the number Windows shows in its own display settings.</summary>
    public string ShortName => MonitorInfo.ShortName(DeviceName);

    /// <summary>
    /// e.g. "Display 2 — BenQ TK800M, HDMI (1920×1080)". The monitor's own name and the cable it's
    /// on are what make this usable in a dark booth; a number and a size don't tell you which
    /// projector is which.
    ///
    /// Both the name and the size come from Windows rather than DXGI — see <see cref="MonitorInfo"/>
    /// for why DXGI's own rectangle can't be trusted for this.
    /// </summary>
    public string Label
    {
        get
        {
            var monitor = MonitorInfo.Describe(DeviceName);
            var size = MonitorInfo.Resolution(DeviceName);
            string pixels = size is { } s ? $"{s.Width}×{s.Height}" : $"{Width}×{Height}";

            string? who = monitor.Name is { } n && monitor.Connection is { } c ? $"{n}, {c}"
                        : monitor.Name ?? monitor.Connection;

            string detail = who is null ? pixels : $"{who} ({pixels})";
            return IsPrimary ? $"{ShortName} — {detail}, primary" : $"{ShortName} — {detail}";
        }
    }
}

/// <summary>Thrown when a display can't be opened for capture; the message is shown to the user.</summary>
public class CaptureException : Exception
{
    public CaptureException(string message) : base(message) { }
}

public enum CaptureResult
{
    /// <summary>A new frame was downscaled into <see cref="DesktopDuplicator.Pixels"/>.</summary>
    Updated,
    /// <summary>Nothing on that display changed within the timeout.</summary>
    NoChange,
    /// <summary>A frame arrived but carried no new image (the mouse cursor moved).</summary>
    Skipped,
    /// <summary>The duplication is dead (mode change, GPU reset, another app took over) — recreate it.</summary>
    Lost,
}

/// <summary>
/// Captures one display with the DXGI Desktop Duplication API and hands back a small
/// downscaled BGRA image of each new frame.
///
/// The downscale happens on the GPU: the desktop frame is copied into a texture with a full
/// mip chain, <c>GenerateMips</c> averages it down, and only a ~64px-wide mip level is read
/// back to the CPU. That's a few KB per frame instead of ~8 MB, which is what makes sampling
/// at 30fps essentially free.
///
/// D3D11/DXGI are reached through raw COM vtable calls so the app keeps its single self-contained
/// exe with no extra dependencies. The vtable slot numbers below come from the method order in
/// d3d11.h / dxgi.h and are fixed by COM's ABI — they can never change for a released interface.
/// </summary>
internal sealed unsafe class DesktopDuplicator : IDisposable
{
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int D3D11_SDK_VERSION = 7;

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // Aim for a grid roughly this wide. Small enough to read back for free, but still coarse
    // pixels rather than one average — the colour extractor needs to see vivid background
    // regions separately from washed-out lyric text.
    private const int TargetWidth = 64;

    private void* _device;
    private void* _context;
    private void* _duplication;
    private void* _mipTexture;
    private void* _mipView;
    private void* _staging;

    private int _sourceWidth, _sourceHeight;
    private int _mipLevel;

    /// <summary>Downscaled BGRA pixels of the most recent <see cref="CaptureResult.Updated"/> frame.</summary>
    public byte[] Pixels { get; private set; } = Array.Empty<byte>();
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }

    /// <summary>The display this instance is capturing.</summary>
    public CaptureDisplay Display { get; }

    /// <summary>HRESULT behind the most recent <see cref="CaptureResult.Lost"/>, for the status message.</summary>
    public int LastError { get; private set; }

    /// <summary>Every display DXGI can capture. Empty if there's no usable adapter at all.</summary>
    public static List<CaptureDisplay> List()
    {
        var displays = new List<CaptureDisplay>();
        void* factory = null;
        try
        {
            var iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(&iid, &factory) < 0)
                return displays;

            for (uint a = 0; ; a++)
            {
                void* adapter = null;
                if (EnumAdapters1(factory, a, &adapter) < 0)
                    break; // DXGI_ERROR_NOT_FOUND once we run off the end of the list
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        void* output = null;
                        if (EnumOutputs(adapter, o, &output) < 0)
                            break;
                        try
                        {
                            if (TryDescribe(output) is { } display)
                                displays.Add(display);
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
        }
        catch (DllNotFoundException)
        {
            // No DXGI (extremely unlikely on Win10/11) — screen capture simply isn't offered.
        }
        finally
        {
            if (factory != null) Release(factory);
        }
        return displays;
    }

    /// <param name="deviceName">DXGI device name (e.g. <c>\\.\DISPLAY2</c>); blank selects the primary display.</param>
    public DesktopDuplicator(string deviceName)
    {
        void* factory = null;
        void* adapter = null;
        void* output = null;
        try
        {
            var factoryIid = IID_IDXGIFactory1;
            Check(CreateDXGIFactory1(&factoryIid, &factory), "DXGI unavailable");

            if (!FindOutput(factory, deviceName, out adapter, out output, out var display))
                throw new CaptureException(string.IsNullOrWhiteSpace(deviceName)
                    ? "No display available to capture."
                    : $"Display '{deviceName}' isn't connected. Pick another one in Settings.");
            Display = display!;

            // The device must live on the adapter that drives this output, or duplication fails.
            void* device = null;
            void* context = null;
            int featureLevel = 0;
            Check(D3D11CreateDevice(adapter, 0 /* driver type: unknown, i.e. use the adapter */,
                    IntPtr.Zero, 0x20 /* BGRA_SUPPORT */, null, 0, D3D11_SDK_VERSION,
                    &device, &featureLevel, &context),
                "Could not open the graphics adapter");
            _device = device;
            _context = context;

            void* output1 = null;
            var outputIid = IID_IDXGIOutput1;
            Check(QueryInterface(output, &outputIid, &output1), "Display doesn't support capture");
            try
            {
                void* duplication = null;
                int hr = DuplicateOutput(output1, _device, &duplication);
                if (hr < 0)
                    throw new CaptureException(DescribeDuplicationFailure(hr));
                _duplication = duplication;
            }
            finally { Release(output1); }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (output != null) Release(output);
            if (adapter != null) Release(adapter);
            if (factory != null) Release(factory);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the display to change, and downscales the frame
    /// if it did. Desktop Duplication is event-driven, so a static screen costs nothing while we
    /// sit in here.
    ///
    /// Every frame that carries a new image is sampled. Rate limiting has to be done by pausing
    /// *between* calls, never by discarding a frame here: on an otherwise static screen a single
    /// frame may be the only notification that the picture changed, and dropping it would leave
    /// the lights on the previous colour indefinitely.
    /// </summary>
    public CaptureResult Capture(int timeoutMs)
    {
        if (_duplication == null)
            return CaptureResult.Lost;

        void* resource = null;
        FrameInfo info = default;
        int hr = AcquireNextFrame(_duplication, (uint)timeoutMs, &info, &resource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            return CaptureResult.NoChange;
        if (hr < 0)
        {
            LastError = hr;
            return CaptureResult.Lost;
        }

        try
        {
            // LastPresentTime stays 0 when only the mouse cursor moved — the image is unchanged.
            if (info.LastPresentTime == 0)
                return CaptureResult.Skipped;

            void* texture = null;
            var iid = IID_ID3D11Texture2D;
            if (QueryInterface(resource, &iid, &texture) < 0)
                return CaptureResult.Skipped;
            try
            {
                return Downscale(texture) ? CaptureResult.Updated : CaptureResult.Lost;
            }
            finally { Release(texture); }
        }
        finally
        {
            if (resource != null) Release(resource);
            ReleaseFrame(_duplication);
        }
    }

    private bool Downscale(void* desktop)
    {
        TextureDesc desc = default;
        GetTextureDesc(desktop, &desc);
        if (desc.Width == 0 || desc.Height == 0)
            return false;

        if (_mipTexture == null || desc.Width != _sourceWidth || desc.Height != _sourceHeight)
        {
            if (!CreateScalingTargets((int)desc.Width, (int)desc.Height))
                return false;
        }

        // Full-resolution frame -> mip 0, average it the whole way down, read back one small level.
        CopySubresourceRegion(_context, _mipTexture, 0, 0, 0, 0, desktop, 0, null);
        GenerateMips(_context, _mipView);
        CopySubresourceRegion(_context, _staging, 0, 0, 0, 0, _mipTexture, (uint)_mipLevel, null);

        MappedSubresource mapped = default;
        LastError = Map(_context, _staging, 0, 1 /* D3D11_MAP_READ */, 0, &mapped);
        if (LastError < 0)
            return false;
        try
        {
            int rowBytes = PixelWidth * 4;
            for (int y = 0; y < PixelHeight; y++)
                Marshal.Copy((IntPtr)((byte*)mapped.Data + (long)y * mapped.RowPitch),
                    Pixels, y * rowBytes, rowBytes);
        }
        finally
        {
            Unmap(_context, _staging, 0);
        }
        return true;
    }

    private bool CreateScalingTargets(int width, int height)
    {
        ReleaseScalingTargets();
        _sourceWidth = width;
        _sourceHeight = height;

        int levels = 1;
        while ((Math.Max(width, height) >> (levels - 1)) > 1)
            levels++;

        // Deepest level that still leaves us ~TargetWidth pixels across.
        _mipLevel = 0;
        while (_mipLevel + 1 < levels && (width >> (_mipLevel + 1)) >= TargetWidth)
            _mipLevel++;

        PixelWidth = Math.Max(1, width >> _mipLevel);
        PixelHeight = Math.Max(1, height >> _mipLevel);
        Pixels = new byte[PixelWidth * PixelHeight * 4];

        var mipDesc = new TextureDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = (uint)levels,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            Usage = 0,               // D3D11_USAGE_DEFAULT
            BindFlags = 0x8 | 0x20,  // SHADER_RESOURCE | RENDER_TARGET, both required by GenerateMips
            MiscFlags = 0x1,         // D3D11_RESOURCE_MISC_GENERATE_MIPS
        };
        void* mip = null;
        LastError = CreateTexture2D(_device, &mipDesc, null, &mip);
        if (LastError < 0)
            return false;
        _mipTexture = mip;

        void* view = null;
        LastError = CreateShaderResourceView(_device, _mipTexture, null, &view);
        if (LastError < 0)
            return false;
        _mipView = view;

        var stagingDesc = new TextureDesc
        {
            Width = (uint)PixelWidth,
            Height = (uint)PixelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            Usage = 3,                 // D3D11_USAGE_STAGING
            CpuAccessFlags = 0x20000,  // D3D11_CPU_ACCESS_READ
        };
        void* staging = null;
        LastError = CreateTexture2D(_device, &stagingDesc, null, &staging);
        if (LastError < 0)
            return false;
        _staging = staging;

        return true;
    }

    /// <summary>
    /// Locates the named display, falling back to the primary one. On success the caller owns a
    /// reference to both <paramref name="foundAdapter"/> and <paramref name="foundOutput"/>.
    /// </summary>
    private static bool FindOutput(void* factory, string deviceName,
        out void* foundAdapter, out void* foundOutput, out CaptureDisplay? found)
    {
        void* primaryAdapter = null;
        void* primaryOutput = null;
        CaptureDisplay? primaryDisplay = null;
        bool wantPrimary = string.IsNullOrWhiteSpace(deviceName);

        foundAdapter = null;
        foundOutput = null;
        found = null;

        for (uint a = 0; ; a++)
        {
            void* adapter = null;
            if (EnumAdapters1(factory, a, &adapter) < 0)
                break;

            bool keepAdapter = false;
            try
            {
                for (uint o = 0; ; o++)
                {
                    void* output = null;
                    if (EnumOutputs(adapter, o, &output) < 0)
                        break;

                    var display = TryDescribe(output);
                    if (display == null)
                    {
                        Release(output);
                        continue;
                    }

                    if (!wantPrimary && display.DeviceName == deviceName)
                    {
                        if (primaryOutput != null) Release(primaryOutput);
                        if (primaryAdapter != null) Release(primaryAdapter);
                        keepAdapter = true;
                        foundAdapter = adapter;
                        foundOutput = output;
                        found = display;
                        return true;
                    }

                    // Remember the primary as the fallback, but keep looking for the named display.
                    if (display.IsPrimary && primaryOutput == null)
                    {
                        AddRef(adapter);
                        primaryAdapter = adapter;
                        primaryOutput = output;
                        primaryDisplay = display;
                        continue;
                    }

                    Release(output);
                }
            }
            finally
            {
                if (!keepAdapter) Release(adapter);
            }
        }

        if (primaryOutput == null)
            return false;

        foundAdapter = primaryAdapter;
        foundOutput = primaryOutput;
        found = primaryDisplay;
        return true;
    }

    private static CaptureDisplay? TryDescribe(void* output)
    {
        OutputDesc desc = default;
        if (GetOutputDesc(output, &desc) < 0 || desc.AttachedToDesktop == 0)
            return null;

        string name = new string(desc.DeviceName, 0, 32).TrimEnd('\0');
        return new CaptureDisplay(name, desc.Left, desc.Top, desc.Right - desc.Left, desc.Bottom - desc.Top);
    }

    private static string DescribeDuplicationFailure(int hr) => hr switch
    {
        unchecked((int)0x887A0004) => // DXGI_ERROR_UNSUPPORTED
            "This display can't be captured. On a laptop with switchable graphics, try forcing "
            + "Slide Colours onto the same GPU as the display in Windows Graphics settings.",
        unchecked((int)0x887A0022) => // DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
            "Windows is already handing out the maximum number of screen captures for this display. "
            + "Close other capture or streaming software and try again.",
        unchecked((int)0x80070005) => // E_ACCESSDENIED
            "Access to this display was denied — usually a full-screen exclusive app, a secure "
            + "desktop prompt, or a session lock. It should recover on its own.",
        _ => $"Screen capture failed to start (0x{hr:X8}).",
    };

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new CaptureException($"{what} (0x{hr:X8}).");
    }

    private void ReleaseScalingTargets()
    {
        if (_staging != null) { Release(_staging); _staging = null; }
        if (_mipView != null) { Release(_mipView); _mipView = null; }
        if (_mipTexture != null) { Release(_mipTexture); _mipTexture = null; }
    }

    public void Dispose()
    {
        ReleaseScalingTargets();
        if (_duplication != null) { Release(_duplication); _duplication = null; }
        if (_context != null) { Release(_context); _context = null; }
        if (_device != null) { Release(_device); _device = null; }
    }

    // ---- Native structures -------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX, PointerY;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    // ---- COM plumbing ------------------------------------------------------------------------
    //
    // Each call below reads the object's vtable and invokes one slot. The slot index is the
    // method's position in the interface, counting inherited methods first.

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(Guid* riid, void** factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(void* adapter, int driverType, IntPtr software,
        uint flags, int* featureLevels, uint featureLevelCount, uint sdkVersion,
        void** device, int* featureLevel, void** immediateContext);

    private static void** Vtbl(void* self) => *(void***)self;

    // IUnknown: 0 QueryInterface, 1 AddRef, 2 Release
    private static int QueryInterface(void* self, Guid* iid, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Vtbl(self)[0])(self, iid, result);

    private static uint AddRef(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, uint>)Vtbl(self)[1])(self);

    private static uint Release(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, uint>)Vtbl(self)[2])(self);

    // IDXGIFactory1 (IDXGIObject 3-6, IDXGIFactory 7-11): 12 EnumAdapters1
    private static int EnumAdapters1(void* self, uint index, void** adapter) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vtbl(self)[12])(self, index, adapter);

    // IDXGIAdapter (IDXGIObject 3-6): 7 EnumOutputs
    private static int EnumOutputs(void* self, uint index, void** output) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vtbl(self)[7])(self, index, output);

    // IDXGIOutput (IDXGIObject 3-6): 7 GetDesc
    private static int GetOutputDesc(void* self, OutputDesc* desc) =>
        ((delegate* unmanaged[Stdcall]<void*, OutputDesc*, int>)Vtbl(self)[7])(self, desc);

    // IDXGIOutput1 (IDXGIOutput 7-18, then 19 GetDisplayModeList1, 20 FindClosestMatchingMode1,
    // 21 GetDisplaySurfaceData1): 22 DuplicateOutput
    private static int DuplicateOutput(void* self, void* device, void** duplication) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Vtbl(self)[22])(self, device, duplication);

    // IDXGIOutputDuplication (IDXGIObject 3-6, 7 GetDesc): 8 AcquireNextFrame, 14 ReleaseFrame
    private static int AcquireNextFrame(void* self, uint timeoutMs, FrameInfo* info, void** resource) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, FrameInfo*, void**, int>)Vtbl(self)[8])(self, timeoutMs, info, resource);

    private static int ReleaseFrame(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Vtbl(self)[14])(self);

    // ID3D11Device (IUnknown 0-2, 3 CreateBuffer, 4 CreateTexture1D): 5 CreateTexture2D,
    // (6 CreateTexture3D) 7 CreateShaderResourceView
    private static int CreateTexture2D(void* self, TextureDesc* desc, void* initialData, void** texture) =>
        ((delegate* unmanaged[Stdcall]<void*, TextureDesc*, void*, void**, int>)Vtbl(self)[5])(self, desc, initialData, texture);

    private static int CreateShaderResourceView(void* self, void* resource, void* desc, void** view) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, void*, void**, int>)Vtbl(self)[7])(self, resource, desc, view);

    // ID3D11Texture2D (ID3D11DeviceChild 3-6, ID3D11Resource 7-9): 10 GetDesc
    private static void GetTextureDesc(void* self, TextureDesc* desc) =>
        ((delegate* unmanaged[Stdcall]<void*, TextureDesc*, void>)Vtbl(self)[10])(self, desc);

    // ID3D11DeviceContext (ID3D11DeviceChild 3-6, then the draw/state methods):
    // 14 Map, 15 Unmap, 46 CopySubresourceRegion, 54 GenerateMips
    private static int Map(void* self, void* resource, uint subresource, int mapType, uint flags, MappedSubresource* mapped) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, uint, int, uint, MappedSubresource*, int>)Vtbl(self)[14])(self, resource, subresource, mapType, flags, mapped);

    private static void Unmap(void* self, void* resource, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, uint, void>)Vtbl(self)[15])(self, resource, subresource);

    private static void CopySubresourceRegion(void* self, void* dst, uint dstSubresource,
        uint dstX, uint dstY, uint dstZ, void* src, uint srcSubresource, void* srcBox) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, uint, void*, uint, void*, void>)Vtbl(self)[46])
            (self, dst, dstSubresource, dstX, dstY, dstZ, src, srcSubresource, srcBox);

    private static void GenerateMips(void* self, void* shaderResourceView) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, void>)Vtbl(self)[54])(self, shaderResourceView);
}
