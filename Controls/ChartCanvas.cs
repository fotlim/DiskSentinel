using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace DiskSentinel.Controls;

/// <summary>
/// 手绘图表控件。不引第三方图表库，保证启动快、内存小。
/// 内部用 Canvas 承载，尺寸变化时由外部重新调用 Draw。
/// </summary>
public sealed class ChartCanvas : ContentControl
{
    private readonly Canvas _root = new();

    public ChartCanvas()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = _root;
        _root.SizeChanged += (_, e) =>
        {
            // 尺寸真变了才重绘，避免死循环
            if (Math.Abs(e.NewSize.Width - _lastW) > 1 || Math.Abs(e.NewSize.Height - _lastH) > 1)
            {
                _lastW = e.NewSize.Width;
                _lastH = e.NewSize.Height;
                _redraw?.Invoke();
            }
        };
    }

    private double _lastW, _lastH;
    private Action? _redraw;

    // ---------------- 折线 ----------------

    private IReadOnlyList<double>? _lineValues;
    private IReadOnlyList<string>? _lineLabels;
    private Color _lineColor;
    private double? _lineMax;
    private bool _lineFill;

    public void DrawLine(IReadOnlyList<double> values, IReadOnlyList<string> labels,
        Color lineColor, double? fixedMax = null, bool fillArea = true)
    {
        _lineValues = values;
        _lineLabels = labels;
        _lineColor = lineColor;
        _lineMax = fixedMax;
        _lineFill = fillArea;

        _redraw = RenderLine;
        RenderLine();
    }

    private void RenderLine()
    {
        _root.Children.Clear();
        if (_lineValues == null || _lineLabels == null) return;

        double w = _root.ActualWidth;
        double h = _root.ActualHeight;
        if (w < 30 || h < 30 || _lineValues.Count == 0) return;

        var values = _lineValues;
        var labels = _lineLabels;

        const double padL = 38, padR = 12, padT = 12, padB = 22;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        if (plotW <= 6 || plotH <= 6) return;

        double min = values.Min();
        double max = _lineMax ?? values.Max();
        if (max <= min) max = min + 1;

        double lo, hi;
        if (_lineMax.HasValue && _lineMax.Value == 100)
        {
            // 健康度：固定 0-100 区间，别让 100% 的盘把刻度压成 99.9~100.2
            lo = 0; hi = 100;
            // 但如果数据都在高位，放大到有意义的区间更易读
            if (min >= 80) { lo = Math.Max(0, Math.Floor((min - 5) / 10) * 10); hi = 100; }
        }
        else
        {
            double span = max - min;
            if (span <= 0) span = Math.Abs(max) > 0 ? Math.Abs(max) * 0.1 : 1;
            lo = Math.Max(0, min - span * 0.10);
            hi = max + span * 0.10;
        }
        if (hi <= lo) hi = lo + 1;

        double X(int i) => values.Count == 1
            ? padL + plotW / 2
            : padL + plotW * i / (values.Count - 1);
        double Y(double v) => padT + plotH * (1 - (v - lo) / (hi - lo));

        // 网格 + Y 轴刻度
        var gridBrush = new SolidColorBrush(Color.FromArgb(34, 140, 140, 140));
        var axisTextBrush = new SolidColorBrush(Color.FromArgb(150, 150, 150, 150));
        for (int g = 0; g <= 3; g++)
        {
            double yy = padT + plotH * g / 3;
            _root.Children.Add(new Line
            {
                X1 = padL, Y1 = yy, X2 = padL + plotW, Y2 = yy,
                Stroke = gridBrush, StrokeThickness = 1
            });

            double val = hi - (hi - lo) * g / 3;
            var tb = new TextBlock { Text = AxisText(val), FontSize = 10, Foreground = axisTextBrush };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, 2);
            Canvas.SetTop(tb, yy - tb.DesiredSize.Height / 2);
            _root.Children.Add(tb);
        }

        // 面积
        if (_lineFill && values.Count > 1)
        {
            var poly = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(28, _lineColor.R, _lineColor.G, _lineColor.B))
            };
            var pts = new PointCollection { new Point(X(0), padT + plotH) };
            for (int i = 0; i < values.Count; i++) pts.Add(new Point(X(i), Y(values[i])));
            pts.Add(new Point(X(values.Count - 1), padT + plotH));
            poly.Points = pts;
            _root.Children.Add(poly);
        }

        // 折线
        if (values.Count > 1)
        {
            var pl = new Polyline
            {
                Stroke = new SolidColorBrush(_lineColor),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };
            var pts = new PointCollection();
            for (int i = 0; i < values.Count; i++) pts.Add(new Point(X(i), Y(values[i])));
            pl.Points = pts;
            _root.Children.Add(pl);
        }

        // 数据点
        var dotBrush = new SolidColorBrush(_lineColor);
        for (int i = 0; i < values.Count; i++)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Fill = dotBrush };
            Canvas.SetLeft(dot, X(i) - 2.5);
            Canvas.SetTop(dot, Y(values[i]) - 2.5);
            _root.Children.Add(dot);
        }

        // X 轴标签（最多 6 个）
        if (labels.Count == values.Count && labels.Count > 0)
        {
            int step = Math.Max(1, (int)Math.Ceiling(labels.Count / 6.0));
            for (int i = 0; i < labels.Count; i += step)
            {
                var tb = new TextBlock { Text = labels[i], FontSize = 10, Foreground = axisTextBrush };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lx = X(i) - tb.DesiredSize.Width / 2;
                lx = Math.Clamp(lx, padL - 10, w - tb.DesiredSize.Width - 2);
                Canvas.SetLeft(tb, lx);
                Canvas.SetTop(tb, padT + plotH + 5);
                _root.Children.Add(tb);
            }
        }
    }

    // ---------------- 横向对比条 ----------------

    private IReadOnlyList<(string Label, double Value, Color Color, string ValueText)>? _bars;
    private double _barsMax;
    private double _barsBaseline;

    /// <summary>
    /// 画横向对比条。baseline 用于放大高位区间的视觉差异
    /// （例如健康度都在 85-100 时，用 70 作基线，89% 和 100% 才看得出区别）。
    /// </summary>
    public void DrawBars(
        IReadOnlyList<(string Label, double Value, Color Color, string ValueText)> items,
        double maxValue,
        double baseline = 0)
    {
        _bars = items;
        _barsMax = maxValue;
        _barsBaseline = baseline;
        _redraw = RenderBars;
        RenderBars();
    }

    private void RenderBars()
    {
        _root.Children.Clear();
        if (_bars == null || _bars.Count == 0) return;

        double w = _root.ActualWidth;
        double h = _root.ActualHeight;
        if (w < 40 || h < 20) return;

        var items = _bars;
        // 数值列放在条左边（紧贴条尾），避免被右边缘裁掉
        const double labelW = 130, valueW = 56, gap = 10;
        double barW = w - labelW - valueW - gap * 2;
        if (barW < 20) barW = Math.Max(20, w - labelW - valueW - gap * 2);

        double rowH = h / items.Count;
        double barH = Math.Min(16, rowH * 0.5);

        var labelBrush = new SolidColorBrush(Color.FromArgb(215, 220, 220, 220));
        var valueBrush = new SolidColorBrush(Color.FromArgb(235, 235, 235, 235));
        var trackBrush = new SolidColorBrush(Color.FromArgb(28, 140, 140, 140));

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            double cy = rowH * i + rowH / 2;

            var lbl = new TextBlock
            {
                Text = it.Label,
                FontSize = 12,
                Width = labelW - 6,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = labelBrush
            };
            Canvas.SetLeft(lbl, 0);
            Canvas.SetTop(lbl, cy - 9);
            _root.Children.Add(lbl);

            var track = new Rectangle
            {
                Width = barW,
                Height = barH,
                RadiusX = barH / 2,
                RadiusY = barH / 2,
                Fill = trackBrush
            };
            Canvas.SetLeft(track, labelW + gap);
            Canvas.SetTop(track, cy - barH / 2);
            _root.Children.Add(track);

            double span = _barsMax - _barsBaseline;
            if (span <= 0) span = _barsMax > 0 ? _barsMax : 1;
            double ratio = Math.Clamp((it.Value - _barsBaseline) / span, 0, 1);
            double fillW = Math.Max(barH, barW * ratio);
            var bar = new Rectangle
            {
                Width = fillW,
                Height = barH,
                RadiusX = barH / 2,
                RadiusY = barH / 2,
                Fill = new SolidColorBrush(it.Color)
            };
            Canvas.SetLeft(bar, labelW + gap);
            Canvas.SetTop(bar, cy - barH / 2);
            _root.Children.Add(bar);

            // 数值放在条的最右端外侧，但保证不超出控件宽度
            var val = new TextBlock
            {
                Text = it.ValueText,
                FontSize = 12,
                Foreground = valueBrush
            };
            val.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double vx = labelW + gap + barW + 8;
            if (vx + val.DesiredSize.Width > w) vx = w - val.DesiredSize.Width - 2;
            Canvas.SetLeft(val, vx);
            Canvas.SetTop(val, cy - 9);
            _root.Children.Add(val);
        }
    }

    private static string AxisText(double v)
    {
        // 刻度要能区分相邻值，别把 99.9 / 100 / 101 显示成一样
        if (Math.Abs(v - Math.Round(v)) < 0.05) return $"{Math.Round(v):0}";
        if (Math.Abs(v) >= 1000) return $"{v / 1000:0.#}k";
        if (Math.Abs(v) >= 10) return $"{v:0.#}";
        return $"{v:0.##}";
    }
}
