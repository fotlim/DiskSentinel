using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DiskSentinel.Models;

namespace DiskSentinel.Services;

/// <summary>
/// 开机检测：跑一次、写历史、立即退出。不常驻、不留后台进程。
/// 用 Windows 计划任务实现，触发器为「登录后」。
/// </summary>
public static class BootTask
{
    public const string TaskName = "DiskSentinel_BootCheck";

    private static readonly string DebugLog =
        Path.Combine(AppContext.BaseDirectory, "boot_debug.log");

    private static bool _debugEnabled =
        Environment.GetCommandLineArgs().Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));

    /// <summary>程序是否以「静默检测」模式启动（由计划任务传入 --bootcheck）。</summary>
    public static bool IsBootCheckMode
    {
        get
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                Dbg("命令行参数: " + string.Join(" | ", args));
                bool hit = args.Any(a => a.Equals("--bootcheck", StringComparison.OrdinalIgnoreCase));
                Dbg("IsBootCheckMode = " + hit);
                return hit;
            }
            catch (Exception ex)
            {
                Dbg("解析参数异常: " + ex.Message);
                return false;
            }
        }
    }

    internal static void Dbg(string msg)
    {
        if (!_debugEnabled) return;
        try
        {
            File.AppendAllText(DebugLog,
                $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>静默检测：读一遍、落盘、退出。全程无窗口。</summary>
    public static void RunBootCheckAndExit()
    {
        Dbg("进入 RunBootCheckAndExit");
        try
        {
            Dbg("开始扫描磁盘…");
            var snaps = SmartReader.ScanAll();
            Dbg($"扫描完成，{snaps.Count} 块盘");

            HistoryStore.Append(snaps);
            Dbg("历史已写入");

            HistoryStore.TrimIfNeeded();

            var summary = string.Join("; ", snaps.Select(s =>
                $"{s.Model}={s.HealthPercent}%/{s.TemperatureC:0}C"));
            HistoryStore.Log($"开机检测完成，{snaps.Count} 块盘：{summary}");
            Dbg("日志已写入");

            var bad = snaps.Where(s => HealthJudge.Judge(s) == HealthLevel.Bad).ToList();
            if (bad.Count > 0)
            {
                HistoryStore.Log("警告：检测到危险状态 —— " +
                    string.Join("; ", bad.Select(s => s.Model)));
            }
        }
        catch (Exception ex)
        {
            Dbg("异常: " + ex);
            HistoryStore.Log("开机检测异常：" + ex.Message);
        }
        finally
        {
            Dbg("退出");
            Environment.Exit(0);
        }
    }

    /// <summary>查询计划任务是否已注册。</summary>
    public static bool IsRegistered()
    {
        try
        {
            var (code, output) = Run("schtasks.exe", $"/Query /TN \"{TaskName}\"");
            return code == 0 && output.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public sealed record Result(bool Ok, string Message);

    /// <summary>注册开机检测任务。需要管理员权限。</summary>
    public static Result Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return new Result(false, "拿不到程序路径，无法注册。");

            var xml = BuildTaskXml(exe);
            var tmp = Path.Combine(Path.GetTempPath(), $"ds_task_{Guid.NewGuid():N}.xml");
            File.WriteAllText(tmp, xml, new UnicodeEncoding(false, true));

            try
            {
                var (code, output) = Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
                if (code != 0)
                    return new Result(false, $"注册失败（代码 {code}）：\n{output}");

                return new Result(true,
                    "已注册开机检测。\n\n" +
                    "每次开机登录后约 30 秒，程序会静默检测一次并记录，然后立即退出，不占用内存。\n\n" +
                    "任务名：" + TaskName);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
        catch (Exception ex)
        {
            return new Result(false, "注册异常：" + ex.Message);
        }
    }

    public static Result Unregister()
    {
        try
        {
            var (code, output) = Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
            if (code != 0)
                return new Result(false, $"取消失败（代码 {code}）：\n{output}");
            return new Result(true, "已取消开机自动检测。");
        }
        catch (Exception ex)
        {
            return new Result(false, "取消异常：" + ex.Message);
        }
    }

    public static Result RunNow()
    {
        try
        {
            var (code, output) = Run("schtasks.exe", $"/Run /TN \"{TaskName}\"");
            return code == 0
                ? new Result(true, "已触发一次检测。")
                : new Result(false, $"触发失败（代码 {code}）：\n{output}");
        }
        catch (Exception ex)
        {
            return new Result(false, "触发异常：" + ex.Message);
        }
    }

    private static string BuildTaskXml(string exePath)
    {
        var exe = System.Security.SecurityElement.Escape(exePath) ?? exePath;

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>磁盘哨兵：开机后静默检测一次磁盘健康，记录后立即退出，不常驻后台。</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT30S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <GroupId>S-1-5-32-544</GroupId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>--bootcheck</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    private static (int Code, string Output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = Process.Start(psi);
        if (p == null) return (-1, "进程启动失败");

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();

        if (!p.WaitForExit(20000))
        {
            try { p.Kill(true); } catch { }
            return (-2, "超时");
        }

        var combined = (stdout + "\n" + stderr).Trim();
        return (p.ExitCode, combined);
    }
}
