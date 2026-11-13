using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DiskSentinel.Models;

namespace DiskSentinel.Services;

/// <summary>把原生数据整理成 SmartSnapshot。</summary>
public static class SmartReader
{
    public static List<SmartSnapshot> ScanAll()
    {
        var results = new List<SmartSnapshot>();
        var elevated = NativeSmart.IsElevated();

        // 盘符映射，用于显示
        var letterMap = BuildLetterMap();

        for (int i = 0; i < 32; i++)
        {
            var info = NativeSmart.QueryDeviceInfo(i);
            if (info == null) continue;
            if (string.IsNullOrWhiteSpace(info.Model) && string.IsNullOrWhiteSpace(info.Serial)) continue;

            var snap = new SmartSnapshot
            {
                Model = string.IsNullOrWhiteSpace(info.Model) ? $"磁盘 {i}" : info.Model,
                Serial = info.Serial,
                BusType = info.BusName,
                Key = MakeKey(info),
                TimestampLocal = DateTime.Now
            };

            snap.CapacityBytes = QueryCapacity(i);

            // 盘符
            var letters = letterMap.Where(kv => kv.Value == i).Select(kv => kv.Key).OrderBy(c => c).ToList();
            if (letters.Count > 0)
                snap.Note = "盘符 " + string.Join(" ", letters.Select(c => c + ":"));

            bool got = false;

            if (info.BusType == 17) // NVMe
            {
                got = FillFromNvme(snap, i);
            }
            else if (info.BusType == 3 || info.BusType == 11 || info.BusType == 2) // ATA / SATA
            {
                got = FillFromAta(snap, i);
            }

            if (!got)
            {
                // 降级：Windows 不让对已挂载卷的物理盘做写访问，ATA SMART 命令走不通。
                // 改用 storage 驱动在 root\wmi 暴露的原始 SMART 结构，内容与直连命令一致。
                //
                // 只在 SATA/ATA 总线上尝试：root\wmi 只暴露 ATA 设备，
                // 拿 NVMe 或 USB 盘的型号去匹配会误配到别的盘上。
                bool isSata = info.BusType == 3 || info.BusType == 11 || info.BusType == 2;
                if (isSata) got = FillFromWmi(snap, info.Model);

                if (!got)
                {
                    if (info.BusType == 7)
                        snap.Note = Append(snap.Note, "USB 桥接，通常读不到 SMART");
                    else if (!elevated)
                        snap.Note = Append(snap.Note, "需要管理员权限才能读取 SMART");
                    else
                        snap.Note = Append(snap.Note, "该设备未提供 SMART 数据");
                }
            }

            results.Add(snap);
        }

        return results;
    }

    private static string Append(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : a + " · " + b;

    private static string MakeKey(NativeSmart.DeviceInfo info)
    {
        var serial = (info.Serial ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(serial)) return serial;
        return $"{(info.Vendor ?? "").Trim()}|{(info.Product ?? "").Trim()}|{info.Index}";
    }

    private static Dictionary<char, int> BuildLetterMap()
    {
        var map = new Dictionary<char, int>();
        try
        {
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                if (d.DriveType != System.IO.DriveType.Fixed && d.DriveType != System.IO.DriveType.Removable) continue;
                if (string.IsNullOrEmpty(d.Name) || d.Name.Length < 2) continue;
                char letter = char.ToUpperInvariant(d.Name[0]);
                var num = NativeSmart.GetPhysicalDriveNumberForVolume(letter);
                if (num.HasValue) map[letter] = num.Value;
            }
        }
        catch { }
        return map;
    }

    private static long QueryCapacity(int index)
    {
        try
        {
            var h = NativeSmart.Open($@"\\.\PhysicalDrive{index}", write: false);
            if (!NativeSmart.IsValid(h)) return 0;
            try
            {
                // IOCTL_DISK_GET_LENGTH_INFO
                var output = new byte[8];
                if (!NativeSmart.Ioctl(h, 0x0007405C, Array.Empty<byte>(), output, out _)) return 0;
                return BitConverter.ToInt64(output, 0);
            }
            finally { NativeSmart.Close(h); }
        }
        catch { return 0; }
    }

    // ---------------- NVMe ----------------

    private static bool FillFromNvme(SmartSnapshot s, int index)
    {
        var log = NativeSmart.ReadNvmeSmartLog(index);
        if (log == null) return false;

        try
        {
            s.Source = "nvme";
            s.CriticalWarning = log[0];
            s.TemperatureC = KelvinToC(BitConverter.ToUInt16(log, 1));
            s.HasTemperature = s.TemperatureC > -100;
            s.AvailableSpare = log[3];
            s.SpareThreshold = log[4];
            s.PercentageUsed = log[5];

            ulong read = ReadU128Low(log, 32);
            ulong written = ReadU128Low(log, 48);
            s.DataReadTB = read * 512000.0 / 1e12;
            s.DataWrittenTB = written * 512000.0 / 1e12;

            s.ControllerBusyMin = BitConverter.ToUInt64(log, 96);
            s.PowerCycles = BitConverter.ToUInt64(log, 112);
            s.PowerOnHours = BitConverter.ToUInt64(log, 128);
            s.UnsafeShutdowns = BitConverter.ToUInt64(log, 144);
            s.MediaErrors = BitConverter.ToUInt64(log, 160);
            s.ErrorLogEntries = BitConverter.ToUInt64(log, 176);
            s.WarnTempMin = BitConverter.ToUInt32(log, 192);
            s.CritTempMin = BitConverter.ToUInt32(log, 196);

            s.HealthPercent = Math.Clamp(100 - s.PercentageUsed, 0, 100);
            return true;
        }
        catch { return false; }
    }

    private static double KelvinToC(ushort k) => k == 0 ? -999 : k - 273.15;

    private static ulong ReadU128Low(byte[] b, int off)
    {
        // 128 位小端，取低 64 位足够（18 EB 上限）
        return BitConverter.ToUInt64(b, off);
    }

    // ---------------- WMI 降级（SATA 原始 SMART） ----------------

    private static bool FillFromWmi(SmartSnapshot s, string model)
    {
        var smart = WmiFallback.QueryByModel(model);
        if (smart == null || !smart.HasAny) return false;

        try
        {
            s.Source = "ata-wmi";
            var a = smart.Attributes;

            // 关键坏道指标
            if (a.TryGetValue(5, out var v5)) s.ReallocatedSectors = v5.Raw;
            if (a.TryGetValue(197, out var v197)) s.PendingSectors = v197.Raw;
            if (a.TryGetValue(198, out var v198)) s.Uncorrectable = v198.Raw;
            if (a.TryGetValue(199, out var v199)) s.CrcErrors = v199.Raw;

            if (a.TryGetValue(9, out var v9)) s.PowerOnHours = (ulong)Math.Max(0, v9.Raw);
            if (a.TryGetValue(12, out var v12)) s.PowerCycles = (ulong)Math.Max(0, v12.Raw);

            // 温度：194 优先，其次 190
            if (a.TryGetValue(194, out var t194))
            {
                s.TemperatureC = t194.RawLowByte;
                s.HasTemperature = true;
            }
            else if (a.TryGetValue(190, out var t190))
            {
                s.TemperatureC = t190.RawLowByte;
                s.HasTemperature = true;
            }

            // 剩余寿命：231(SSD 剩余寿命) / 202(健康度) / 177(磨损范围) / 233
            int health = -1;
            foreach (var id in new[] { 231, 202, 177, 233 })
            {
                if (a.TryGetValue(id, out var attr) && attr.Value > 0)
                {
                    health = attr.Value;
                    break;
                }
            }

            if (health >= 0)
            {
                s.HealthPercent = Math.Clamp(health, 0, 100);
            }
            else
            {
                bool clean = s.ReallocatedSectors <= 0 && s.PendingSectors <= 0 && s.Uncorrectable <= 0;
                s.HealthPercent = clean ? 100 : 60;
            }

            // 累计写入 / 读取量。
            //
            // 单位说明（实测标定得出，不要凭印象改）：
            //   属性 241 = 累计写入，属性 242 = 累计读取，
            //   每个计数单位 = 32 MiB（65536 个 512 字节扇区），不是 512 字节。
            //   实测：写入 512 MiB → 241 增加 16；再写 256 MiB → 242 增加约 8。
            //   早期按 512 字节算，会把 9.76 TB 显示成 0 TB。
            const double LbaUnitBytes = 32.0 * 1024 * 1024;

            if (a.TryGetValue(241, out var w241) && w241.Raw > 0)
                s.DataWrittenTB = w241.Raw * LbaUnitBytes / 1e12;

            if (a.TryGetValue(242, out var r242) && r242.Raw > 0)
                s.DataReadTB = r242.Raw * LbaUnitBytes / 1e12;

            // 有些盘把累计量放在 245（通常是 241 的镜像），兜底
            if (s.DataWrittenTB <= 0 && a.TryGetValue(245, out var w245) && w245.Raw > 0)
                s.DataWrittenTB = w245.Raw * LbaUnitBytes / 1e12;

            return true;
        }
        catch { return false; }
    }

    // ---------------- ATA / SATA ----------------

    private static bool FillFromAta(SmartSnapshot s, int index)
    {
        var data = NativeSmart.ReadAtaSmart(index);
        if (data == null) return false;

        try
        {
            s.Source = "ata";
            var attrs = NativeSmart.ParseAtaAttributes(data);
            if (attrs.Count == 0) return false;

            // 关键属性
            if (attrs.TryGetValue(5, out var a5)) s.ReallocatedSectors = a5.RawLow;
            if (attrs.TryGetValue(197, out var a197)) s.PendingSectors = a197.RawLow;
            if (attrs.TryGetValue(198, out var a198)) s.Uncorrectable = a198.RawLow;
            if (attrs.TryGetValue(199, out var a199)) s.CrcErrors = a199.RawLow;

            if (attrs.TryGetValue(9, out var a9)) s.PowerOnHours = (ulong)Math.Max(0, a9.RawLow);
            if (attrs.TryGetValue(12, out var a12)) s.PowerCycles = (ulong)Math.Max(0, a12.RawLow);

            // 温度：194 优先，其次 190
            if (attrs.TryGetValue(194, out var t194))
            {
                s.TemperatureC = t194.RawLow & 0xFF;
                s.HasTemperature = true;
            }
            else if (attrs.TryGetValue(190, out var t190))
            {
                s.TemperatureC = t190.RawLow & 0xFF;
                s.HasTemperature = true;
            }

            // 寿命：按优先级取归一化值（100 = 全新）
            int health = -1;
            foreach (var id in new[] { 231, 202, 177, 233 })
            {
                if (attrs.TryGetValue(id, out var a) && a.Value > 0)
                {
                    health = a.Value;
                    break;
                }
            }
            if (health >= 0) s.HealthPercent = Math.Clamp(health, 0, 100);
            else
            {
                // 机械盘没有磨损计数：只要没坏道就算 100
                bool clean = s.ReallocatedSectors <= 0 && s.PendingSectors <= 0 && s.Uncorrectable <= 0;
                s.HealthPercent = clean ? 100 : 60;
            }

            return true;
        }
        catch { return false; }
    }
}
