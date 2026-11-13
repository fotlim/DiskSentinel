using System;
using System.Collections.Generic;
using System.Globalization;
using DiskSentinel.Models;

namespace DiskSentinel.Models;

/// <summary>磁盘列表项 + 详情展示所需的全部文本。全部预格式化，UI 直接绑定。</summary>
public sealed class DiskItemVm
{
    public string Key { get; init; } = "";
    public string Model { get; init; } = "";
    public string BusLine { get; init; } = "";
    public string HealthText { get; init; } = "—";
    public string LevelText { get; init; } = "未知";
    public string LevelColor { get; init; } = "#8A8A8A";
    public string TempText { get; init; } = "—";
    public string CapText { get; init; } = "—";
    public double HealthRatio { get; init; }
    public SmartSnapshot Snapshot { get; init; } = new();

    /// <summary>圆环用的虚线数组：整圈周长按比例切分。</summary>
    public string DashArray
    {
        get
        {
            // 周长 ≈ 2πr，r = (42-3)/2 ≈ 19.5 → 约 122.5
            const double circumference = 122.5;
            double on = circumference * Math.Clamp(HealthRatio, 0, 1);
            double off = Math.Max(0.01, circumference - on);
            return $"{on:0.##} {off:0.##}";
        }
    }
}

/// <summary>详情页的一行「名称 : 值」。</summary>
public sealed class DetailRow
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = "#E6E6E6";
}

public static class Format
{
    public static string LevelColor(HealthLevel l) => l switch
    {
        HealthLevel.Good => "#4CC38A",
        HealthLevel.Caution => "#E8A33D",
        HealthLevel.Bad => "#E5484D",
        _ => "#8A8A8A"
    };

    public static string Temp(double c, bool has) =>
        has ? $"{c:0}°C" : "—";

    public static string Tb(double tb) => tb >= 1000 ? $"{tb / 1000:0.##} PB" : $"{tb:0.##} TB";

    public static string Hours(ulong h)
    {
        if (h == 0) return "—";
        if (h < 24) return $"{h} 小时";
        var d = h / 24.0;
        if (d < 365) return $"{h} 小时 ({d:0} 天)";
        return $"{h} 小时 ({d / 365:0.0} 年)";
    }

    public static string Num(ulong v) => v.ToString("N0", CultureInfo.InvariantCulture);
}
