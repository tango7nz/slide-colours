using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace SlideColours.Services;

/// <summary>
/// Chauvet Xpress 512, the Lighting Controller D512 (USB VID 1069, PID 1040).
/// A frame is 512 raw channel bytes on bulk OUT 0x01 — channel 1 is the first byte —
/// and the box answers with 512 bytes on bulk IN 0x82, which are read and discarded.
/// Needs the ShowXpress WinUSB driver. ShowXpress cannot have the box open at the same time.
/// </summary>
public sealed class Xpress512Output : IDmxOutput
{
    private static readonly Guid InterfaceGuid = new("9ef5175d-990d-48be-a584-57320b3dc2e8");

    private const byte OutPipe = 0x01;
    private const byte InPipe = 0x82;
    private const uint PipeTimeoutMs = 50;

    private readonly byte[] _out = new byte[512];
    private readonly byte[] _in = new byte[512];
    private IntPtr _device = InvalidHandle;
    private IntPtr _winusb;

    public Xpress512Output()
    {
        _device = OpenDevice();
        try
        {
            if (!WinUsb.Initialize(_device, out _winusb))
                throw UsbError("Could not open the Xpress 512");

            ExpectPipes();
            SetTimeout(OutPipe);
            SetTimeout(InPipe);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SendFrame(byte[] dmx512)
    {
        ObjectDisposedException.ThrowIf(_winusb == IntPtr.Zero, this);

        int count = Math.Min(dmx512.Length, _out.Length);
        Buffer.BlockCopy(dmx512, 0, _out, 0, count);
        if (count < _out.Length)
            Array.Clear(_out, count, _out.Length - count);

        if (!WinUsb.WritePipe(_winusb, OutPipe, _out, (uint)_out.Length, out uint written, IntPtr.Zero)
            || written != _out.Length)
            throw UsbError("Could not send DMX to the Xpress 512");

        // The box always replies with 512 bytes. Leaving that unread wedges the next write.
        if (!WinUsb.ReadPipe(_winusb, InPipe, _in, (uint)_in.Length, out uint read, IntPtr.Zero)
            || read != _in.Length)
            throw UsbError("The Xpress 512 did not answer a DMX frame");
    }

    public void Dispose()
    {
        if (_winusb != IntPtr.Zero)
        {
            WinUsb.Free(_winusb);
            _winusb = IntPtr.Zero;
        }
        if (_device != IntPtr.Zero && _device != InvalidHandle)
        {
            WinUsb.CloseHandle(_device);
            _device = IntPtr.Zero;
        }
    }

    private void ExpectPipes()
    {
        if (!WinUsb.QueryInterfaceSettings(_winusb, 0, out var iface))
            throw UsbError("Could not read the Xpress 512 USB interface");

        bool outFound = false;
        bool inFound = false;
        for (byte i = 0; i < iface.bNumEndpoints; i++)
        {
            if (!WinUsb.QueryPipe(_winusb, 0, i, out var pipe))
                throw UsbError("Could not read an Xpress 512 USB endpoint");
            if (pipe.PipeId == OutPipe)
                outFound = true;
            else if (pipe.PipeId == InPipe)
                inFound = true;
        }

        if (!outFound || !inFound)
            throw new IOException("The Xpress 512 did not expose the expected USB endpoints.");
    }

    private void SetTimeout(byte pipe)
    {
        uint timeout = PipeTimeoutMs;
        if (!WinUsb.SetPipePolicy(_winusb, pipe, WinUsb.PipeTransferTimeout, sizeof(uint), ref timeout))
            throw UsbError("Could not set a timeout on the Xpress 512");
    }

    private static IntPtr OpenDevice()
    {
        var list = WinUsb.SetupDiGetClassDevs(InterfaceGuid, IntPtr.Zero, IntPtr.Zero,
            WinUsb.Present | WinUsb.DeviceInterface);
        if (list == InvalidHandle)
            throw UsbError("Could not search for an Xpress 512");

        try
        {
            var data = new WinUsb.DeviceInterfaceData { cbSize = Marshal.SizeOf<WinUsb.DeviceInterfaceData>() };
            uint index = 0;
            while (WinUsb.SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, InterfaceGuid, index, ref data))
            {
                index++;
                string? path = InterfacePath(list, ref data);
                if (path == null || path.IndexOf("vid_1069&pid_1040", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var handle = WinUsb.CreateFile(path,
                    WinUsb.GenericRead | WinUsb.GenericWrite,
                    WinUsb.ShareRead | WinUsb.ShareWrite,
                    IntPtr.Zero, WinUsb.OpenExisting, WinUsb.Normal | WinUsb.Overlapped, IntPtr.Zero);
                if (handle == InvalidHandle)
                    throw OpenFailed();
                return handle;
            }

            throw new IOException(
                "No Chauvet Xpress 512 was found. Plug it in. If Windows has no driver for it, " +
                "install ShowXpress once so its USB driver is registered.");
        }
        finally
        {
            WinUsb.SetupDiDestroyDeviceInfoList(list);
        }
    }

    private static string? InterfacePath(IntPtr list, ref WinUsb.DeviceInterfaceData data)
    {
        WinUsb.SetupDiGetDeviceInterfaceDetail(list, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
        if (needed == 0)
            return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!WinUsb.SetupDiGetDeviceInterfaceDetail(list, ref data, buffer, needed, out _, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IOException OpenFailed()
    {
        int error = Marshal.GetLastWin32Error();
        if (error is 5 or 32)
            return new IOException("The Xpress 512 is already open in another program. Close ShowXpress and try again.");
        return new IOException($"Could not open the Xpress 512. {new Win32Exception(error).Message}");
    }

    private static IOException UsbError(string message)
    {
        int error = Marshal.GetLastWin32Error();
        string detail = error == 0 ? "" : $" {new Win32Exception(error).Message}";
        return new IOException(message + "." + detail);
    }

    private static readonly IntPtr InvalidHandle = new(-1);

    private static class WinUsb
    {
        public const uint Present = 0x02;
        public const uint DeviceInterface = 0x10;
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint ShareRead = 0x01;
        public const uint ShareWrite = 0x02;
        public const uint OpenExisting = 3;
        public const uint Normal = 0x80;
        public const uint Overlapped = 0x40000000;
        public const uint PipeTransferTimeout = 0x03;

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInterfaceData
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct InterfaceDescriptor
        {
            public byte bLength;
            public byte bDescriptorType;
            public byte bInterfaceNumber;
            public byte bAlternateSetting;
            public byte bNumEndpoints;
            public byte bInterfaceClass;
            public byte bInterfaceSubClass;
            public byte bInterfaceProtocol;
            public byte iInterface;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PipeInformation
        {
            public int PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [DllImport("winusb.dll", EntryPoint = "WinUsb_Initialize", SetLastError = true)]
        public static extern bool Initialize(IntPtr deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", EntryPoint = "WinUsb_Free", SetLastError = true)]
        public static extern bool Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", EntryPoint = "WinUsb_WritePipe", SetLastError = true)]
        public static extern bool WritePipe(IntPtr interfaceHandle, byte pipeId, byte[] buffer,
            uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", EntryPoint = "WinUsb_ReadPipe", SetLastError = true)]
        public static extern bool ReadPipe(IntPtr interfaceHandle, byte pipeId, byte[] buffer,
            uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", EntryPoint = "WinUsb_SetPipePolicy", SetLastError = true)]
        public static extern bool SetPipePolicy(IntPtr interfaceHandle, byte pipeId, uint policyType,
            uint valueLength, ref uint value);

        [DllImport("winusb.dll", EntryPoint = "WinUsb_QueryInterfaceSettings", SetLastError = true)]
        public static extern bool QueryInterfaceSettings(IntPtr interfaceHandle, byte alternateSetting,
            out InterfaceDescriptor descriptor);

        [DllImport("winusb.dll", EntryPoint = "WinUsb_QueryPipe", SetLastError = true)]
        public static extern bool QueryPipe(IntPtr interfaceHandle, byte alternateSetting, byte pipeIndex,
            out PipeInformation pipe);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(in Guid classGuid, IntPtr enumerator,
            IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            in Guid interfaceClassGuid, uint memberIndex, ref DeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref DeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string fileName, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
