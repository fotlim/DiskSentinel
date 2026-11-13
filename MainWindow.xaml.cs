using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiskSentinel.Controls;
using DiskSentinel.Models;
using DiskSentinel.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace DiskSentinel;

public sealed partial class MainWindow : Window
{
    private List<SmartSnapshot> _snapshots = new();
    private List<DiskItemVm> _items = new();
    private List<SmartSnapshot> _history = new();
    private SmartSnapshot? _selected;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Title = "磁盘哨兵";

        // 窗口尺寸
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);

            // 按屏幕工作区自适应，默认占 88%，避免小屏溢出
            var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            var wa = area.WorkArea;
            int w = Math.Min(1300, (int)(wa.Width * 0.88));
            int h = Math.Min(960, (int)(wa.Height * 0.90));
            appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

            // 居中
            int x = wa.X + (wa.Width - w) / 2;
            int y = wa.Y + (wa.Height - h) / 2;
            appWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch { }

        // 统一深色，图表配色按深色背景调
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = ElementTheme.Dark;
            // 等布局完成再画图，否则 ActualWidth 还是 0
            root.Loaded += (_, _) =>
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (_selected != null) RenderTrend(_selected);
                    UpdateCompareChart();
                });
            };
        }

        _ = LoadAsync();
    }

    // ================= 数据加载 =================

    private async Task LoadAsync(bool rescan = true)
    {
        if (_busy) return;
        _busy = true;
        RefreshBtn.IsEnabled = false;

        try
        {
            SubtitleText.Text = "正在读取磁盘健康数据…";

            if (rescan)
            {
                // 扫描放后台，别卡 UI
                _snapshots = await Task.Run(() => SmartReader.ScanAll());
            }

            _history = await Task.Run(() => HistoryStore.LoadAll());

            _items = ReportBuilder.BuildItems(_snapshots);
            DiskList.ItemsSource = _items;

            if (_items.Count > 0)
            {
                int keep = _selected == null
                    ? 0
                    : _items.FindIndex(x => x.Key == _selected.Key);
                DiskList.SelectedIndex = keep >= 0 ? keep : 0;
            }

            UpdateCompareChart();

            int total = _items.Count;
            int warn = _items.Count(x => x.LevelText is "注意" or "危险");
            SubtitleText.Text = warn == 0
                ? $"{total} 块磁盘，全部正常 · {DateTime.Now:HH:mm} 检测"
                : $"{total} 块磁盘，其中 {warn} 块需要留意 · {DateTime.Now:HH:mm} 检测";

            int histCount = _history.Select(x => x.Key).Distinct().Count();
            FooterText.Text = $"历史记录 {_history.Count} 条（{histCount} 块盘） · 数据目录 {HistoryStore.DataDir}";

            UpdateAutoStartButton();
        }
        catch (Exception ex)
        {
            SubtitleText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    // ================= 列表选择 =================

    private void OnDiskSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DiskList.SelectedItem is not DiskItemVm item) return;
        _selected = item.Snapshot;
        RenderDetail(item.Snapshot);
    }

    private void RenderDetail(SmartSnapshot s)
    {
        DetailModel.Text = s.Model;
        DetailSub.Text = $"{s.BusType} · {s.CapacityText}" +
                         (string.IsNullOrWhiteSpace(s.Serial) ? "" : $" · 序列号 {s.Serial}");

        var level = HealthJudge.Judge(s);
        var color = ColorFromHex(Format.LevelColor(level));
        LevelBadge.Background = new SolidColorBrush(color);
        LevelBadgeText.Text = HealthJudge.LevelText(level);

        // 找这块盘上一次的记录
        var prev = _history
            .Where(x => x.Key == s.Key && x.TimestampLocal < s.TimestampLocal)
            .OrderByDescending(x => x.TimestampLocal)
            .FirstOrDefault();

        DetailRows.ItemsSource = ReportBuilder.BuildDetail(s, prev);

        RenderTrend(s);
    }

    // ================= 趋势图 =================

    private void RenderTrend(SmartSnapshot s)
    {
        var mine = _history
            .Where(x => x.Key == s.Key)
            .OrderBy(x => x.TimestampLocal)
            .ToList();

        // 把当前这次也算进去（还没落盘）
        if (mine.Count == 0 || mine[^1].TimestampLocal < s.TimestampLocal)
            mine.Add(s);

        // 只取最近 40 条，避免点太密
        if (mine.Count > 40) mine = mine.Skip(mine.Count - 40).ToList();

        string tag = (TrendSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "health";

        List<double> values;
        Color lineColor;
        double? fixedMax = null;

        switch (tag)
        {
            case "written":
                values = mine.Select(x => x.DataWrittenTB).ToList();
                lineColor = Color.FromArgb(255, 0x8A, 0xB4, 0xF8);
                break;
            case "temp":
                values = mine.Select(x => x.HasTemperature ? x.TemperatureC : 0).ToList();
                lineColor = Color.FromArgb(255, 0xE8, 0xA3, 0x3D);
                break;
            case "hours":
                values = mine.Select(x => (double)x.PowerOnHours).ToList();
                lineColor = Color.FromArgb(255, 0x9C, 0xA8, 0xB8);
                break;
            default:
                values = mine.Select(x => x.HealthKnown ? (double)x.HealthPercent : 0).ToList();
                lineColor = Color.FromArgb(255, 0x4C, 0xC3, 0x8A);
                fixedMax = 100;
                break;
        }

        // 全 0 或全空 → 显示提示
        bool noData = values.All(v => v == 0);
        if (noData || mine.Count < 2)
        {
            TrendChart.Visibility = Visibility.Collapsed;
            TrendEmptyHint.Visibility = Visibility.Visible;
            TrendEmptyHint.Text = mine.Count < 2
                ? "还没有足够的历史记录。每次开机检测会记一笔，攒几次就能看到趋势了。"
                : "这块盘暂时没有该项数据。";
            return;
        }

        TrendChart.Visibility = Visibility.Visible;
        TrendEmptyHint.Visibility = Visibility.Collapsed;

        var labels = mine.Select(x => x.TimestampLocal.ToString("M/d HH:mm")).ToList();
        TrendChart.DrawLine(values, labels, lineColor, fixedMax);
    }

    private void OnTrendChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected != null) RenderTrend(_selected);
    }

    // ================= 对比图 =================

    private void UpdateCompareChart()
    {
        var items = new List<(string, double, Color, string)>();

        foreach (var it in _items)
        {
            var s = it.Snapshot;
            var level = HealthJudge.Judge(s);
            var c = ColorFromHex(Format.LevelColor(level));
            string label = ShortModel(s.Model);
            double v = s.HealthKnown ? s.HealthPercent : 0;
            items.Add((label, v, c, s.HealthKnown ? $"{s.HealthPercent}%" : "未知"));
        }

        // 健康度都在 80-100 区间时，用 70 作基线把差异放大，否则 89% 和 100% 看着一样长
        CompareChart.DrawBars(items, 100, baseline: 70);
    }

    private static string ShortModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "未知";
        // 太长就截断，保持图表整齐
        var m = model.Replace("SAMSUNG ", "").Replace(" NVMe", "").Trim();
        return m.Length <= 13 ? m : m[..13] + "…";
    }

    // ================= 按钮 =================

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _selected = null;
        await LoadAsync();
    }

    private void OnOpenDataClick(object sender, RoutedEventArgs e)
    {
        try
        {
            HistoryStore.EnsureDir();
            Process.Start(new ProcessStartInfo
            {
                FileName = HistoryStore.DataDir,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        if (!NativeSmart.IsElevated())
        {
            await ShowDialog("需要管理员权限",
                "注册开机检测需要管理员权限。\n\n请右键以管理员身份运行本程序后再试。");
            return;
        }

        bool registered = BootTask.IsRegistered();
        if (registered)
        {
            var ok = await ConfirmDialog("取消开机检测",
                "已经注册了开机自动检测。\n\n要取消吗？");
            if (!ok) return;

            var r = BootTask.Unregister();
            await ShowDialog(r.Ok ? "已取消" : "取消失败", r.Message);
        }
        else
        {
            var ok = await ConfirmDialog("注册开机检测",
                "将创建一个计划任务：每次开机后静默检测一次磁盘健康，把结果记入历史，然后自动退出。\n\n" +
                "· 不常驻后台，不占用内存\n" +
                "· 不会弹出窗口打扰你\n" +
                "· 随时可以在这里取消\n\n继续吗？");
            if (!ok) return;

            var r = BootTask.Register();
            await ShowDialog(r.Ok ? "注册成功" : "注册失败", r.Message);
        }

        UpdateAutoStartButton();
    }

    private void UpdateAutoStartButton()
    {
        bool reg = BootTask.IsRegistered();
        AutoStartBtn.Content = reg ? "取消开机检测" : "注册开机检测";
    }

    private async Task ShowDialog(string title, string content)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = Content.XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };
        await dlg.ShowAsync();
    }

    private async Task<bool> ConfirmDialog(string title, string content)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = Content.XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private static Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
            return Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..], 16));
        return Colors.Gray;
    }
}
