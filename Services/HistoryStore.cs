using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DiskSentinel.Models;

namespace DiskSentinel.Services;

/// <summary>历史落盘：一行一个 JSON，追加写，永不常驻。</summary>
public static class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string? _dataDir;

    /// <summary>
    /// 数据目录。优先 %LOCALAPPDATA%\DiskSentinel，
    /// 写不进去时退到程序自身目录下的 Data（便携模式）。
    /// </summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;

            var preferred = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskSentinel");

            if (CanWrite(preferred))
            {
                _dataDir = preferred;
            }
            else
            {
                var portable = Path.Combine(AppContext.BaseDirectory, "Data");
                _dataDir = portable;
            }
            return _dataDir;
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write_test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static string HistoryFile => Path.Combine(DataDir, "history.jsonl");
    public static string LogFile => Path.Combine(DataDir, "bootcheck.log");

    public static void EnsureDir()
    {
        try { Directory.CreateDirectory(DataDir); } catch { /* 只读环境忽略 */ }
    }
    public static void Append(IEnumerable<SmartSnapshot> snaps)
    {
        try
        {
            EnsureDir();
            var list = snaps as IList<SmartSnapshot> ?? snaps.ToList();
            using var sw = new StreamWriter(HistoryFile, append: true, new System.Text.UTF8Encoding(false));
            foreach (var s in list)
                sw.WriteLine(JsonSerializer.Serialize(s, JsonOpts));
            sw.Flush();
            BootTask.Dbg($"HistoryStore.Append 成功，写入 {list.Count} 条 -> {HistoryFile}");
        }
        catch (Exception ex)
        {
            BootTask.Dbg($"HistoryStore.Append 失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static List<SmartSnapshot> LoadAll()
    {
        var list = new List<SmartSnapshot>();
        try
        {
            if (!File.Exists(HistoryFile)) return list;
            foreach (var line in File.ReadLines(HistoryFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var s = JsonSerializer.Deserialize<SmartSnapshot>(line, JsonOpts);
                    if (s != null && !string.IsNullOrEmpty(s.Key)) list.Add(s);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>同一块盘的历史，按时间升序。</summary>
    public static List<SmartSnapshot> ForDisk(List<SmartSnapshot> all, string key) =>
        all.Where(x => x.Key == key).OrderBy(x => x.TimestampLocal).ToList();

    public static SmartSnapshot? LastBefore(List<SmartSnapshot> all, string key, DateTime before)
    {
        var l = all.Where(x => x.Key == key && x.TimestampLocal < before)
                   .OrderByDescending(x => x.TimestampLocal)
                   .FirstOrDefault();
        return l;
    }

    /// <summary>裁剪历史，避免无限增长。只保留最近 4000 行。</summary>
    public static void TrimIfNeeded(int maxLines = 4000)
    {
        try
        {
            if (!File.Exists(HistoryFile)) return;
            var lines = File.ReadAllLines(HistoryFile);
            if (lines.Length <= maxLines) return;
            File.WriteAllLines(HistoryFile, lines.Skip(lines.Length - maxLines));
        }
        catch { }
    }

    public static void Log(string msg)
    {
        try
        {
            EnsureDir();
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false));
        }
        catch { }
    }
}
