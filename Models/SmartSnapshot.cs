using System;
using System.Collections.Generic;

namespace DiskSentinel.Models;

/// <summary>单块磁盘在一次检测中的完整快照。所有字段都会写入历史，便于日后对比。</summary>
public sealed class SmartSnapshot
{
    public string Key { get; set; } = "";           // 稳定标识：优先序列号
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string BusType { get; set; } = "";
    public long CapacityBytes { get; set; }
    public DateTime TimestampLocal { get; set; } = DateTime.Now;

    /// <summary>数据来源：nvme / ata / reliability / none</summary>
    public string Source { get; set; } = "none";

    // ---- NVMe 健康日志 ----
    public int CriticalWarning { get; set; }
    public int PercentageUsed { get; set; }         // 0-255，255 表示已超设计寿命
    public int AvailableSpare { get; set; }
    public int SpareThreshold { get; set; }
    public ulong MediaErrors { get; set; }
    public ulong ErrorLogEntries { get; set; }
    public ulong UnsafeShutdowns { get; set; }
    public ulong PowerCycles { get; set; }
    public ulong PowerOnHours { get; set; }
    public double DataWrittenTB { get; set; }
    public double DataReadTB { get; set; }
    public ulong ControllerBusyMin { get; set; }
    public ulong WarnTempMin { get; set; }
    public ulong CritTempMin { get; set; }

    // ---- 通用 ----
    public double TemperatureC { get; set; }
    public double Sensor2C { get; set; }
    public bool HasTemperature { get; set; }

    // ---- SATA SMART 专有 ----
    public long ReallocatedSectors { get; set; } = -1;
    public long PendingSectors { get; set; } = -1;
    public long Uncorrectable { get; set; } = -1;
    public long CrcErrors { get; set; } = -1;

    /// <summary>健康度百分比 0-100。NVMe 用 100-PercentageUsed；SATA 用磨损属性或扣分估算。</summary>
    public int HealthPercent { get; set; } = -1;

    public bool HealthKnown => HealthPercent >= 0;

    /// <summary>读取失败时的说明，UI 直接显示。</summary>
    public string Note { get; set; } = "";

    public string CapacityText => CapacityBytes <= 0
        ? "—"
        : CapacityBytes >= 1_000_000_000_000L
            ? $"{CapacityBytes / 1e12:0.##} TB"
            : $"{CapacityBytes / 1e9:0} GB";
}

/// <summary>等级：用于颜色和文字。</summary>
public enum HealthLevel { Unknown, Good, Caution, Bad }

public static class HealthJudge
{
    public static HealthLevel Judge(SmartSnapshot s)
    {
        if (s.CriticalWarning != 0) return HealthLevel.Bad;
        if (s.MediaErrors > 0) return HealthLevel.Bad;
        if (s.SpareThreshold > 0 && s.AvailableSpare <= s.SpareThreshold) return HealthLevel.Bad;
        if (s.HealthKnown && s.HealthPercent <= 10) return HealthLevel.Bad;

        if (s.HealthKnown && s.HealthPercent <= 25) return HealthLevel.Caution;
        if (s.HasTemperature && s.TemperatureC >= 70) return HealthLevel.Caution;
        if (s.ReallocatedSectors > 0) return HealthLevel.Caution;
        if (s.PendingSectors > 0) return HealthLevel.Caution;
        if (s.Uncorrectable > 0) return HealthLevel.Caution;
        if (!s.HealthKnown) return HealthLevel.Unknown;
        return HealthLevel.Good;
    }

    public static string LevelText(HealthLevel l) => l switch
    {
        HealthLevel.Good => "良好",
        HealthLevel.Caution => "注意",
        HealthLevel.Bad => "危险",
        _ => "未知"
    };
}
