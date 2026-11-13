using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DiskSentinel.Services;

/// <summary>
/// SATA 盘读取方案。
///
/// 背景：Windows 拒绝对「已挂载卷」的物理盘做写访问（ERROR_ACCESS_DENIED），
/// 而 ATA SMART 的 DeviceIoControl 必须用读写句柄，所以原生路径在系统盘/数据盘上走不通。
/// NVMe 不受影响（只读句柄 + 协议查询即可）。
///
/// 出路：storage 驱动在 root\wmi 暴露了 MSStorageDriver_ATAPISmartData，
/// 里面就是原始 ATA SMART 结构（362 字节有效），与直连 ATA 命令拿到的内容一致。
/// 按型号关键词匹配到具体磁盘。
/// </summary>
internal static class WmiFallback
{
    public sealed class AtaSmart
    {
        public string InstanceName { get; set; } = "";
        public Dictionary<int, AtaAttr> Attributes { get; } = new();
        public bool HasAny => Attributes.Count > 0;
    }

    public sealed class AtaAttr
    {
        public int Id { get; set; }
        public int Value { get; set; }
        public int Worst { get; set; }
        public long Raw { get; set; }
        public int RawLowByte => (int)(Raw & 0xFF);
    }

    private static readonly List<AtaSmart> _all = new();
    private static bool _loaded;

    public static void Preload()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            // 输出每行：InstanceName|HEXHEXHEX...
            const string script =
                "$ProgressPreference='SilentlyContinue';" +
                "$ErrorActionPreference='SilentlyContinue';" +
                "Get-CimInstance -Namespace root\\wmi -ClassName MSStorageDriver_ATAPISmartData | ForEach-Object {" +
                "  $b=$_.VendorSpecific;" +
                "  if($b){ '{0}|{1}' -f $_.InstanceName, (($b | ForEach-Object { $_.ToString('X2') }) -join '') }" +
                "}";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -Command \"" + script + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi);
            if (p == null) return;

            var output = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();

            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch { }
                return;
            }

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                int sep = line.IndexOf('|');
                if (sep <= 0) continue;

                var instance = line[..sep];
                var hex = line[(sep + 1)..].Trim();
                if (hex.Length < 24) continue;

                var bytes = HexToBytes(hex);
                if (bytes == null || bytes.Length < 100) continue;

                var smart = ParseAtaSmart(bytes);
                if (!smart.HasAny) continue;
                smart.InstanceName = instance;
                _all.Add(smart);
            }
        }
        catch
        {
            // 拿不到就算了，上层会显示「详细属性不可用」
        }
    }

    /// <summary>
    /// 按型号关键词匹配。
    /// 注意：必须要求「型号词全部命中且实例名包含完整型号片段」，
    /// 否则像 "Mass Storage Device" 这种通用名会被错配到别的盘上。
    /// </summary>
    public static AtaSmart? QueryByModel(string model)
    {
        Preload();
        if (_all.Count == 0 || string.IsNullOrWhiteSpace(model)) return null;

        // 把型号拆成有意义的词（≥3 字符），全部命中才算匹配
        var keywords = model.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .ToList();
        if (keywords.Count == 0) return null;

        foreach (var s in _all)
        {
            bool all = keywords.All(kw =>
                s.InstanceName.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (all) return s;
        }

        // 退一步：整个型号（去空格）作为片段匹配
        var compact = model.Replace(" ", "");
        if (compact.Length >= 5)
        {
            foreach (var s in _all)
            {
                var instCompact = s.InstanceName.Replace(" ", "");
                if (instCompact.Contains(compact, StringComparison.OrdinalIgnoreCase)) return s;
            }
        }

        // 只有一块 SATA 盘时才允许兜底，且型号不能是通用词
        if (_all.Count == 1 && !IsGenericModel(model)) return _all[0];
        return null;
    }

    /// <summary>通用型号名，不能用来做唯一匹配。</summary>
    private static bool IsGenericModel(string model)
    {
        var m = model.Trim().ToLowerInvariant();
        string[] generic =
        {
            "mass storage", "usb", "flash", "disk", "drive",
            "removable", "generic", "storage device", "unknown"
        };
        return generic.Any(g => m.Contains(g));
    }

    private static AtaSmart ParseAtaSmart(byte[] data)
    {
        var result = new AtaSmart();
        for (int i = 0; i < 30; i++)
        {
            int off = 2 + i * 12;
            if (off + 12 > data.Length) break;

            int id = data[off];
            if (id == 0) continue;

            result.Attributes[id] = new AtaAttr
            {
                Id = id,
                Value = data[off + 3],
                Worst = data[off + 4],
                Raw = BitConverter.ToUInt32(data, off + 5)
            };
        }
        return result;
    }

    private static byte[]? HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        try
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
        catch { return null; }
    }
}
