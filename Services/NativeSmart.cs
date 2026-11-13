using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskSentinel.Services;

/// <summary>Win32 原生调用。全部走 DeviceIoControl，不依赖 WMI，快且不吃内存。</summary>
internal static class NativeSmart
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;

    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    public const uint IOCTL_SMART_RECEIVE_DRIVE_DATA = 0x0007C088;
    public const uint IOCTL_SMART_ENABLE_DRIVE_DATA = 0x0007C084;

    public const uint PropertyStandardQuery = 0;
    public const uint StorageDeviceProperty = 0;
    public const uint StorageDeviceProtocolSpecificProperty = 49;

    public const uint ProtocolTypeNvme = 3;
    public const uint NVMeDataTypeLogPage = 2;
    public const uint NVMeSmartHealthLogPage = 0x02;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static IntPtr Open(string path, bool write)
    {
        uint access = write ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ;
        return CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    }

    public static void Close(IntPtr h)
    {
        if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
    }

    public static bool IsValid(IntPtr h) => h != IntPtr.Zero && h != new IntPtr(-1);

    public static bool Ioctl(IntPtr h, uint code, byte[] input, byte[] output, out uint returned)
    {
        returned = 0;
        return DeviceIoControl(h, code, input, (uint)input.Length,
            output, (uint)output.Length, out returned, IntPtr.Zero);
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ---------------- 设备描述 ----------------

    public sealed class DeviceInfo
    {
        public int Index { get; set; }
        public string Vendor { get; set; } = "";
        public string Product { get; set; } = "";
        public string Revision { get; set; } = "";
        public string Serial { get; set; } = "";
        public uint BusType { get; set; }
        public bool Removable { get; set; }

        public string BusName => BusType switch
        {
            1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 6 => "Fibre",
            7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA",
            12 => "SD", 13 => "MMC", 14 => "虚拟", 15 => "文件虚拟",
            16 => "存储空间", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
            _ => "未知"
        };

        public string Model => string.Join(' ', new[] { Vendor, Product }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
    }

    public static DeviceInfo? QueryDeviceInfo(int index)
    {
        var h = Open($@"\\.\PhysicalDrive{index}", write: false);
        if (!IsValid(h)) return null;
        try
        {
            // STORAGE_PROPERTY_QUERY: PropertyId + QueryType + 额外 0 填充
            var input = new byte[12];
            BitConverter.GetBytes(StorageDeviceProperty).CopyTo(input, 0);
            BitConverter.GetBytes(PropertyStandardQuery).CopyTo(input, 4);

            var output = new byte[1024];
            if (!Ioctl(h, IOCTL_STORAGE_QUERY_PROPERTY, input, output, out _)) return null;

            uint vendorOff = BitConverter.ToUInt32(output, 12);
            uint productOff = BitConverter.ToUInt32(output, 16);
            uint revOff = BitConverter.ToUInt32(output, 20);
            uint serialOff = BitConverter.ToUInt32(output, 24);
            uint busType = BitConverter.ToUInt32(output, 28);
            bool removable = output[10] != 0;

            return new DeviceInfo
            {
                Index = index,
                Vendor = ReadAnsi(output, vendorOff),
                Product = ReadAnsi(output, productOff),
                Revision = ReadAnsi(output, revOff),
                Serial = ReadAnsi(output, serialOff),
                BusType = busType,
                Removable = removable
            };
        }
        catch { return null; }
        finally { Close(h); }
    }

    private static string ReadAnsi(byte[] buf, uint offset)
    {
        if (offset == 0 || offset >= buf.Length) return "";
        int end = (int)offset;
        while (end < buf.Length && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf, (int)offset, end - (int)offset).Trim();
    }

    /// <summary>盘符 → 物理磁盘号</summary>
    public static int? GetPhysicalDriveNumberForVolume(char letter)
    {
        var h = Open($@"\\.\{letter}:", write: false);
        if (!IsValid(h)) return null;
        try
        {
            var output = new byte[12];
            if (!Ioctl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, Array.Empty<byte>(), output, out _)) return null;
            return (int)BitConverter.ToUInt32(output, 4);
        }
        catch { return null; }
        finally { Close(h); }
    }

    // ---------------- NVMe 健康日志 ----------------

    /// <summary>返回 512 字节 NVMe SMART/Health 日志，失败返回 null。</summary>
    public static byte[]? ReadNvmeSmartLog(int index)
    {
        // 先尝试读写权限，失败再退到只读（部分系统只读也能查）
        foreach (var write in new[] { true, false })
        {
            var h = Open($@"\\.\PhysicalDrive{index}", write);
            if (!IsValid(h)) continue;
            try
            {
                var log = QueryNvmeLog(h);
                if (log != null) return log;
            }
            finally { Close(h); }
        }
        return null;
    }

    private static byte[]? QueryNvmeLog(IntPtr h)
    {
        const int hdr = 8;        // STORAGE_PROPERTY_QUERY
        const int spec = 40;      // STORAGE_PROTOCOL_SPECIFIC_DATA
        const int dataLen = 512;

        var input = new byte[hdr + spec];
        BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(input, 0);
        BitConverter.GetBytes(PropertyStandardQuery).CopyTo(input, 4);

        int o = hdr;
        BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(input, o + 0);
        BitConverter.GetBytes(NVMeDataTypeLogPage).CopyTo(input, o + 4);
        BitConverter.GetBytes(NVMeSmartHealthLogPage).CopyTo(input, o + 8);
        BitConverter.GetBytes(0u).CopyTo(input, o + 12);      // SubValue
        BitConverter.GetBytes((uint)spec).CopyTo(input, o + 16); // ProtocolDataOffset
        BitConverter.GetBytes((uint)dataLen).CopyTo(input, o + 20); // ProtocolDataLength

        var output = new byte[hdr + spec + dataLen + 4096];
        if (!Ioctl(h, IOCTL_STORAGE_QUERY_PROPERTY, input, output, out _)) return null;

        int retOff = BitConverter.ToInt32(output, hdr + 16);
        int start = hdr + retOff;
        if (retOff <= 0 || start + dataLen > output.Length) start = hdr + spec;
        if (start + dataLen > output.Length) return null;

        var log = new byte[dataLen];
        Array.Copy(output, start, log, 0, dataLen);
        return log;
    }

    // ---------------- SATA / ATA SMART ----------------

    /// <summary>返回 512 字节 ATA SMART 数据结构，失败返回 null。需要管理员。</summary>
    public static byte[]? ReadAtaSmart(int index)
    {
        var h = Open($@"\\.\PhysicalDrive{index}", write: true);
        if (!IsValid(h)) return null;
        try
        {
            // 先尝试启用 SMART（已启用会返回错误，忽略即可）
            TrySmartEnable(h);
            return TrySmartRead(h);
        }
        finally { Close(h); }
    }

    private static void TrySmartEnable(IntPtr h)
    {
        try
        {
            var input = BuildSendCmdIn(0x00, 0x00, 0x00, 0x00, 0x00, 0xA0, 0xD8);
            var output = new byte[16 + 512];
            Ioctl(h, IOCTL_SMART_ENABLE_DRIVE_DATA, input, output, out _);
        }
        catch { }
    }

    private static byte[]? TrySmartRead(IntPtr h)
    {
        try
        {
            // SMART READ DATA: features=D0, sectorCount=1, sectorNumber=1, cylLow=4F, cylHigh=C2, cmd=B0
            var input = BuildSendCmdIn(0x01, 0xD0, 0x01, 0x01, 0x4F, 0xC2, 0xB0);
            var output = new byte[16 + 512];
            if (!Ioctl(h, IOCTL_SMART_RECEIVE_DRIVE_DATA, input, output, out _)) return null;

            byte driverError = output[4];
            byte ideError = output[5];
            if (driverError != 0 || ideError != 0) return null;

            var data = new byte[512];
            Array.Copy(output, 16, data, 0, 512);
            return data;
        }
        catch { return null; }
    }

    private static byte[] BuildSendCmdIn(byte sectorCount, byte features, byte sectorNumber,
        byte cylLow, byte cylHigh, byte driveHead, byte command)
    {
        var b = new byte[32 + 512];
        BitConverter.GetBytes(512u).CopyTo(b, 0);   // cBufferSize
        b[4] = features;      // bFeaturesReg
        b[5] = sectorCount;   // bSectorCountReg
        b[6] = sectorNumber;  // bSectorNumberReg
        b[7] = cylLow;        // bCylLowReg
        b[8] = cylHigh;       // bCylHighReg
        b[9] = driveHead;     // bDriveHeadReg
        b[10] = command;      // bCommandReg
        b[11] = 0;            // bReserved
        b[12] = 0;            // bDriveNumber
        return b;
    }

    // ---------------- ATA SMART 属性解析 ----------------

    public sealed class AtaAttribute
    {
        public int Id { get; set; }
        public int Value { get; set; }
        public int Worst { get; set; }
        public byte[] Raw { get; set; } = Array.Empty<byte>();
        public long RawLow => Raw.Length >= 4 ? BitConverter.ToUInt32(Raw, 0) : 0;
    }

    public static Dictionary<int, AtaAttribute> ParseAtaAttributes(byte[] smartData)
    {
        var result = new Dictionary<int, AtaAttribute>();
        for (int i = 0; i < 30; i++)
        {
            int off = 2 + i * 12;
            if (off + 12 > smartData.Length) break;
            int id = smartData[off];
            if (id == 0) continue;

            var raw = new byte[6];
            Array.Copy(smartData, off + 5, raw, 0, 6);

            result[id] = new AtaAttribute
            {
                Id = id,
                Value = smartData[off + 3],
                Worst = smartData[off + 4],
                Raw = raw
            };
        }
        return result;
    }
}
