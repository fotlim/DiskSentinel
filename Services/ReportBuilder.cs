using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DiskSentinel.Models;
using DiskSentinel.Services;

namespace DiskSentinel.Services;

/// <summary>把扫描结果组装成 UI 需要的形式。</summary>
public static class ReportBuilder
{
    public static List<DiskItemVm> BuildItems(List<SmartSnapshot> snaps)
    {
        var list = new List<DiskItemVm>();
        foreach (var s in snaps)
        {
            var level = HealthJudge.Judge(s);
            list.Add(new DiskItemVm
            {
                Key = s.Key,
                Model = s.Model,
                BusLine = $"{s.BusType} · {s.CapacityText}" +
                          (string.IsNullOrWhiteSpace(s.Note) ? "" : $" · {s.Note}"),
                HealthText = s.HealthKnown ? $"{s.HealthPercent}%" : "—",
                LevelText = HealthJudge.LevelText(level),
                LevelColor = Format.LevelColor(level),
                TempText = Format.Temp(s.TemperatureC, s.HasTemperature),
                CapText = s.CapacityText,
                HealthRatio = s.HealthKnown ? s.HealthPercent / 100.0 : 0,
                Snapshot = s
            });
        }
        return list;
    }

    /// <summary>详情页的「关键指标」列表。</summary>
    public static List<DetailRow> BuildDetail(SmartSnapshot s, SmartSnapshot? prev)
    {
        var rows = new List<DetailRow>();
        var level = HealthJudge.Judge(s);
        var levelColor = Format.LevelColor(level);

        void Add(string name, string value, string? color = null)
            => rows.Add(new DetailRow { Name = name, Value = value, Color = color ?? "#E6E6E6" });

        // 结论
        string verdict = level switch
        {
            HealthLevel.Good => "运行正常，可以放心使用",
            HealthLevel.Caution => "有需要留意的迹象，建议备份重要数据",
            HealthLevel.Bad => "存在明确故障迹象，请立即备份并准备更换",
            _ => "该设备未提供足够的健康数据"
        };
        Add("健康结论", verdict, levelColor);

        if (s.CriticalWarning != 0)
            Add("严重警告位", $"0x{s.CriticalWarning:X2}", "#E5484D");

        // NVMe 专有
        if (s.Source == "nvme")
        {
            Add("剩余寿命", s.HealthKnown ? $"{s.HealthPercent}%" : "—", levelColor);
            Add("已用磨损", $"{s.PercentageUsed}%");
            Add("备用块", $"{s.AvailableSpare}% (阈值 {s.SpareThreshold}%)",
                s.AvailableSpare <= s.SpareThreshold ? "#E5484D" : "#E6E6E6");
            Add("介质错误", Format.Num(s.MediaErrors),
                s.MediaErrors > 0 ? "#E5484D" : "#4CC38A");
            Add("累计写入", Format.Tb(s.DataWrittenTB));
            Add("累计读取", Format.Tb(s.DataReadTB));
            Add("控制器忙碌", s.ControllerBusyMin == 0 ? "—" : $"{Format.Num(s.ControllerBusyMin)} 分钟");
        }

        // SATA 专有
        if (s.Source == "ata" || s.Source == "ata-wmi")
        {
            Add("剩余寿命", s.HealthKnown ? $"{s.HealthPercent}%" : "—", levelColor);
            if (s.ReallocatedSectors >= 0)
                Add("重映射扇区", s.ReallocatedSectors.ToString(),
                    s.ReallocatedSectors > 0 ? "#E8A33D" : "#4CC38A");
            if (s.PendingSectors >= 0)
                Add("待映射扇区", s.PendingSectors.ToString(),
                    s.PendingSectors > 0 ? "#E5484D" : "#4CC38A");
            if (s.Uncorrectable >= 0)
                Add("不可纠正错误", s.Uncorrectable.ToString(),
                    s.Uncorrectable > 0 ? "#E5484D" : "#4CC38A");
            if (s.CrcErrors >= 0)
                Add("接口 CRC 错误", s.CrcErrors.ToString(),
                    s.CrcErrors > 0 ? "#E8A33D" : "#4CC38A");
            if (s.DataWrittenTB > 0) Add("累计写入", Format.Tb(s.DataWrittenTB));
            if (s.DataReadTB > 0) Add("累计读取", Format.Tb(s.DataReadTB));

            // 日均写入，用通电时间算，比只看总量更能判断磨损速度
            if (s.DataWrittenTB > 0 && s.PowerOnHours >= 24)
            {
                double perDay = s.DataWrittenTB * 1000 / (s.PowerOnHours / 24.0);
                Add("日均写入", $"{perDay:0.#} GB/天",
                    perDay > 500 ? "#E8A33D" : "#8AB4F8");
            }
        }

        // 通用
        Add("当前温度", Format.Temp(s.TemperatureC, s.HasTemperature),
            s.HasTemperature && s.TemperatureC >= 70 ? "#E8A33D" : "#E6E6E6");
        Add("通电时间", Format.Hours(s.PowerOnHours));
        Add("通电次数", s.PowerCycles == 0 ? "—" : $"{Format.Num(s.PowerCycles)} 次");
        if (s.UnsafeShutdowns > 0)
            Add("不安全关机", $"{Format.Num(s.UnsafeShutdowns)} 次",
                s.UnsafeShutdowns > 100 ? "#E8A33D" : "#E6E6E6");
        if (s.ErrorLogEntries > 0)
            Add("错误日志条数", Format.Num(s.ErrorLogEntries));

        // 与上次对比
        if (prev != null)
        {
            var deltaTime = s.TimestampLocal - prev.TimestampLocal;
            var deltaWrite = s.DataWrittenTB - prev.DataWrittenTB;
            if (deltaTime.TotalMinutes > 1 && deltaWrite > 0.001)
            {
                var perDay = deltaWrite / deltaTime.TotalDays;
                string writeText = deltaWrite >= 1
                    ? $"{deltaWrite:0.##} TB"
                    : $"{deltaWrite * 1024:0} GB";
                string rateText = perDay >= 1
                    ? $"{perDay:0.#} TB/天"
                    : $"{perDay * 1024:0} GB/天";
                Add("上次至今写入", $"{writeText}  ({rateText})",
                    perDay > 0.5 ? "#E8A33D" : "#8AB4F8");
            }
            if (s.PowerOnHours > prev.PowerOnHours)
                Add("上次至今通电", $"{s.PowerOnHours - prev.PowerOnHours} 小时", "#8AB4F8");
        }

        return rows;
    }
}
