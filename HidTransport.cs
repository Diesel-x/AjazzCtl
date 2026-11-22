using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AjazzCtl;

public enum ConnectionMode
{
    UsbWired = 0,
    Wireless24G = 2
}

public sealed record HidDeviceInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort VersionNumber,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportByteLength,
    ushort OutputReportByteLength,
    ushort FeatureReportByteLength,
    ConnectionMode Mode,
    string ProductString)
{
    public bool IsUsbControlFeature =>
        Mode == ConnectionMode.UsbWired && FeatureReportByteLength == 65;

    public bool IsUsbScreenBulk =>
        Mode == ConnectionMode.UsbWired && OutputReportByteLength == 4097;

    public bool IsWirelessVendor =>
        Mode == ConnectionMode.Wireless24G &&
        UsagePage >= 0xFF00 &&
        InputReportByteLength == 33 &&
        OutputReportByteLength == 33;
}

public sealed class AjazzHidSession : IDisposable
{
    public ConnectionMode Mode { get; }
    public HidDeviceInfo ControlDevice { get; }
    public HidDeviceInfo? BulkDevice { get; }

    private readonly SafeFileHandle _controlHandle;
    private readonly SafeFileHandle? _bulkHandle;

    public AjazzHidSession(
        ConnectionMode mode,
        HidDeviceInfo controlDevice,
        SafeFileHandle controlHandle,
        HidDeviceInfo? bulkDevice,
        SafeFileHandle? bulkHandle)
    {
        Mode = mode;
        ControlDevice = controlDevice;
        _controlHandle = controlHandle;
        BulkDevice = bulkDevice;
        _bulkHandle = bulkHandle;
    }

    public bool HasUsbBulkPipe => _bulkHandle is { IsInvalid: false, IsClosed: false };

    public void SendUsbFeature(byte[] payload64, bool expectAck, int maxAckAttempts = 10)
    {
        if (payload64.Length != 64)
            throw new ArgumentException("USB feature payload must be 64 bytes.", nameof(payload64));

        byte[] raw = new byte[65];
        raw[0] = 0x00;
        Array.Copy(payload64, 0, raw, 1, 64);

        if (!HidNative.HidD_SetFeature(_controlHandle, raw, raw.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_SetFeature failed.");
        }

        Thread.Sleep(AjazzProtocol.UsbCommandDelayMs);

        if (!expectAck)
            return;

        byte[] ack = new byte[65];
        for (int attempt = 0; attempt < maxAckAttempts; attempt++)
        {
            Array.Clear(ack);
            ack[0] = 0x00;
            if (TryGetFeature(ack) && ack[4] == 0x01)
            {
                return;
            }
            Thread.Sleep(AjazzProtocol.UsbCommandDelayMs);
        }

        throw new IOException($"USB device did not acknowledge command 0x{payload64[0]:X2} 0x{payload64[1]:X2} (last ack[4]=0x{ack[4]:X2}).");
    }

    private bool TryGetFeature(byte[] buffer)
    {
        if (HidNative.HidD_GetFeature(_controlHandle, buffer, buffer.Length))
            return true;

        return HidNative.DeviceIoControl(
            _controlHandle,
            HidNative.IOCTL_HID_GET_FEATURE,
            IntPtr.Zero,
            0,
            buffer,
            buffer.Length,
            out _,
            IntPtr.Zero);
    }

    public void SendUsbBulkChunk(byte[] chunk4096)
    {
        if (!HasUsbBulkPipe || _bulkHandle is null)
            throw new InvalidOperationException("USB 4097-byte bulk screen HID interface is not available.");
        if (chunk4096.Length != 4096)
            throw new ArgumentException("Bulk chunk must be 4096 bytes.", nameof(chunk4096));

        byte[] raw = new byte[4097];
        raw[0] = 0x00;
        Array.Copy(chunk4096, 0, raw, 1, 4096);

        HidNative.WriteOverlapped(_bulkHandle, raw, 1000);
        int ackLen = BulkDevice is { InputReportByteLength: > 0 } ? BulkDevice.InputReportByteLength : 65;
        byte[] ack = new byte[ackLen];
        if (!HidNative.TryReadOverlapped(_bulkHandle, ack, 500, out _))
        {
            Thread.Sleep(50);
        }
    }

    public void FlushInput() => HidNative.HidD_FlushQueue(_controlHandle);

    public void FlushBulkInput()
    {
        if (HasUsbBulkPipe && _bulkHandle is not null)
            HidNative.HidD_FlushQueue(_bulkHandle);
    }

    private static bool IsAckMatch(byte[] resp, int n, byte[] report33)
    {
        if (n < 4)
            return false;

        bool directMatch = resp[0] == report33[1] && resp[1] == report33[2] && resp[2] == report33[3];
        bool reportIdMatch = resp[0] == 0x00 && resp[1] == report33[1] && resp[2] == report33[2] && resp[3] == report33[3];
        return directMatch || reportIdMatch;
    }

    public bool SendWireless32(byte[] report33, int postDelayMs = 5, bool verifyAck = true)
    {
        if (report33.Length != 33)
            throw new ArgumentException("Wireless HID report must be 33 bytes.", nameof(report33));

        HidNative.WriteOverlapped(_controlHandle, report33, 500);
        if (postDelayMs > 0)
            Thread.Sleep(postDelayMs);

        if (!verifyAck)
            return true;

        byte[] resp = new byte[33];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Array.Clear(resp);
            if (HidNative.TryReadOverlapped(_controlHandle, resp, 8, out int readBytes) && IsAckMatch(resp, readBytes, report33))
                return true;
            Thread.Sleep(2);
        }

        return false;
    }

    public bool SendWireless32Reliable(byte[] report33, int ackTimeoutMs = 60, int maxAttempts = 4)
    {
        if (report33.Length != 33)
            throw new ArgumentException("Wireless HID report must be 33 bytes.", nameof(report33));

        byte[] resp = new byte[33];
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            HidNative.WriteOverlapped(_controlHandle, report33, 500);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ackTimeoutMs)
            {
                int remaining = Math.Max(1, ackTimeoutMs - (int)sw.ElapsedMilliseconds);
                Array.Clear(resp);
                if (HidNative.TryReadOverlapped(_controlHandle, resp, remaining, out int n) && IsAckMatch(resp, n, report33))
                    return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _bulkHandle?.Dispose();
        _controlHandle.Dispose();
    }
}

public static class HidDiscovery
{
    public const ushort AjazzVendorId = 0x0C45;
    public static readonly ushort[] UsbProductIds = [0x8009, 0x800A];
    public const ushort WirelessProductId = 0xFDFD;

    public static List<HidDeviceInfo> EnumerateAjazzDevices()
    {
        var results = new List<HidDeviceInfo>();
        HidNative.HidD_GetHidGuid(out Guid hidGuid);

        IntPtr devInfoSet = HidNative.SetupDiGetClassDevsW(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            HidNative.DIGCF_PRESENT | HidNative.DIGCF_DEVICEINTERFACE);

        if (devInfoSet == new IntPtr(-1))
            return results;

        try
        {
            var interfaceData = new HidNative.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<HidNative.SP_DEVICE_INTERFACE_DATA>()
            };

            uint index = 0;
            while (HidNative.SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
            {
                index++;
                string? path = GetDevicePath(devInfoSet, ref interfaceData);
                if (string.IsNullOrEmpty(path))
                    continue;

                if (TryInspectDevice(path, out var info) && info is not null)
                {
                    results.Add(info);
                }
            }
        }
        finally
        {
            HidNative.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return results;
    }

    public static AjazzHidSession OpenSession(ConnectionMode? preferredMode = null)
    {
        var devices = EnumerateAjazzDevices();
        if (devices.Count == 0)
            throw new InvalidOperationException("AJAZZ AK820 Pro device (VID_0C45) not found. Connect via USB or 2.4G dongle.");

        if (preferredMode != ConnectionMode.Wireless24G)
        {
            var usbCtrl = devices.FirstOrDefault(d => d.IsUsbControlFeature);
            if (usbCtrl is not null)
            {
                var ctrlHandle = OpenHandle(usbCtrl.DevicePath, readWrite: true);
                var usbBulk = devices.FirstOrDefault(d => d.IsUsbScreenBulk);
                SafeFileHandle? bulkHandle = null;
                if (usbBulk is not null)
                {
                    try
                    {
                        bulkHandle = OpenHandle(usbBulk.DevicePath, readWrite: true, overlapped: true);
                    }
                    catch
                    {
                        // Bulk pipe may be unavailable or denied independently; control commands still work.
                    }
                }
                return new AjazzHidSession(ConnectionMode.UsbWired, usbCtrl, ctrlHandle, usbBulk, bulkHandle);
            }
        }

        if (preferredMode != ConnectionMode.UsbWired)
        {
            var wireless = devices.FirstOrDefault(d => d.IsWirelessVendor);
            if (wireless is not null)
            {
                var handle = OpenHandle(wireless.DevicePath, readWrite: true, overlapped: true);
                return new AjazzHidSession(ConnectionMode.Wireless24G, wireless, handle, null, null);
            }
        }

        throw new InvalidOperationException("AJAZZ HID device detected, but no usable control collection (65-byte Feature or 33-byte 2.4G Vendor HID) was found.");
    }

    private static string? GetDevicePath(IntPtr devInfoSet, ref HidNative.SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        HidNative.SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
        if (requiredSize == 0)
            return null;

        IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
            if (!HidNative.SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref interfaceData, detailBuffer, requiredSize, out _, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    private static bool TryInspectDevice(string path, out HidDeviceInfo? info)
    {
        info = null;
        SafeFileHandle? handle = null;
        try
        {
            handle = TryOpenMetadataHandle(path);
            if (handle is null || handle.IsInvalid)
                return false;

            var attrs = new HidNative.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HidNative.HIDD_ATTRIBUTES>() };
            if (!HidNative.HidD_GetAttributes(handle, ref attrs))
                return false;

            if (attrs.VendorID != AjazzVendorId)
                return false;

            ConnectionMode? mode = null;
            if (UsbProductIds.Contains(attrs.ProductID))
                mode = ConnectionMode.UsbWired;
            else if (attrs.ProductID == WirelessProductId)
                mode = ConnectionMode.Wireless24G;

            if (mode is null)
                return false;

            if (!HidNative.HidD_GetPreparsedData(handle, out IntPtr preparsed))
                return false;

            try
            {
                if (HidNative.HidP_GetCaps(preparsed, out var caps) < 0)
                    return false;

                string product = GetProductString(handle);
                info = new HidDeviceInfo(
                    path,
                    attrs.VendorID,
                    attrs.ProductID,
                    attrs.VersionNumber,
                    caps.UsagePage,
                    caps.Usage,
                    caps.InputReportByteLength,
                    caps.OutputReportByteLength,
                    caps.FeatureReportByteLength,
                    mode.Value,
                    product);
                return true;
            }
            finally
            {
                HidNative.HidD_FreePreparsedData(preparsed);
            }
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static string GetProductString(SafeFileHandle handle)
    {
        byte[] buf = new byte[256];
        if (!HidNative.HidD_GetProductString(handle, buf, buf.Length))
            return string.Empty;

        string s = Encoding.Unicode.GetString(buf);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private static SafeFileHandle? TryOpenMetadataHandle(string path)
    {
        var h = HidNative.CreateFileW(
            path,
            HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero,
            HidNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (!h.IsInvalid)
            return h;

        h.Dispose();
        h = HidNative.CreateFileW(
            path,
            0,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero,
            HidNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        return h.IsInvalid ? null : h;
    }

    private static SafeFileHandle OpenHandle(string path, bool readWrite, bool overlapped = false)
    {
        uint access = readWrite ? (HidNative.GENERIC_READ | HidNative.GENERIC_WRITE) : 0;
        uint flags = overlapped ? HidNative.FILE_FLAG_OVERLAPPED : 0;
        var h = HidNative.CreateFileW(
            path,
            access,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero,
            HidNative.OPEN_EXISTING,
            flags,
            IntPtr.Zero);

        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new Win32Exception(err, $"Failed to open HID device '{path}' (Win32 error {err}).");
        }
        return h;
    }
}

internal static class HidNative
{
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint IOCTL_HID_GET_FEATURE = 0x000B0192;
    private const int ERROR_IO_PENDING = 997;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    public static extern void HidD_GetHidGuid(out Guid HidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetFeature(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetProductString(SafeFileHandle HidDeviceObject, byte[] Buffer, int BufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_FlushQueue(SafeFileHandle HidDeviceObject);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(
        ref Guid ClassGuid,
        IntPtr Enumerator,
        IntPtr hwndParent,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet,
        IntPtr DeviceInfoData,
        ref Guid InterfaceClassGuid,
        uint MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize,
        IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool WriteFile(
        SafeFileHandle hFile,
        byte* lpBuffer,
        int nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(
        SafeFileHandle hFile,
        byte* lpBuffer,
        int nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetOverlappedResult(
        SafeFileHandle hFile,
        NativeOverlapped* lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool CancelIoEx(SafeFileHandle hFile, NativeOverlapped* lpOverlapped);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    public static IDisposable UseHighResTimer() => new HighResTimerScope();

    private sealed class HighResTimerScope : IDisposable
    {
        private bool _disposed;
        public HighResTimerScope() => TimeBeginPeriod(1);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TimeEndPeriod(1);
        }
    }

    public static void WriteOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs)
    {
        if (!TryIoOverlapped(handle, buffer, timeoutMs, isWrite: true, out int transferred))
        {
            throw new TimeoutException($"HID WriteFile timed out after {timeoutMs} ms.");
        }
        if (transferred != buffer.Length)
        {
            throw new IOException($"HID WriteFile wrote {transferred}/{buffer.Length} bytes.");
        }
    }

    public static void ReadOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs)
    {
        if (!TryIoOverlapped(handle, buffer, timeoutMs, isWrite: false, out _))
        {
            throw new TimeoutException($"HID ReadFile timed out after {timeoutMs} ms.");
        }
    }

    public static bool TryReadOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs, out int bytesRead)
    {
        return TryIoOverlapped(handle, buffer, timeoutMs, isWrite: false, out bytesRead);
    }

    private static unsafe bool TryIoOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs, bool isWrite, out int bytesTransferred)
    {
        bytesTransferred = 0;
        using var evt = new ManualResetEvent(false);
        var ov = new NativeOverlapped
        {
            EventHandle = evt.SafeWaitHandle.DangerousGetHandle()
        };

        fixed (byte* pBuf = buffer)
        {
            bool ok = isWrite
                ? WriteFile(handle, pBuf, buffer.Length, out uint n, &ov)
                : ReadFile(handle, pBuf, buffer.Length, out n, &ov);

            if (ok)
            {
                bytesTransferred = (int)n;
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_IO_PENDING)
            {
                throw new Win32Exception(err, $"HID {(isWrite ? "WriteFile" : "ReadFile")} failed (error {err}).");
            }

            if (!evt.WaitOne(timeoutMs))
            {
                CancelIoEx(handle, &ov);
                GetOverlappedResult(handle, &ov, out _, true);
                return false;
            }

            if (!GetOverlappedResult(handle, &ov, out uint transferred, false))
            {
                int ovErr = Marshal.GetLastWin32Error();
                throw new Win32Exception(ovErr, $"GetOverlappedResult failed (error {ovErr}).");
            }

            bytesTransferred = (int)transferred;
            return true;
        }
    }
}
