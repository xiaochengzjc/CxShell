using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CxShell.Controls;

public class LineChartControl : Control
{
    private INotifyCollectionChanged? _series1Collection;
    private INotifyCollectionChanged? _series2Collection;

    public static readonly StyledProperty<IList<double>?> Series1Property =
        AvaloniaProperty.Register<LineChartControl, IList<double>?>(nameof(Series1));

    public static readonly StyledProperty<IList<double>?> Series2Property =
        AvaloniaProperty.Register<LineChartControl, IList<double>?>(nameof(Series2));

    public static readonly StyledProperty<string?> Series1LabelProperty =
        AvaloniaProperty.Register<LineChartControl, string?>(nameof(Series1Label), "↓");

    public static readonly StyledProperty<string?> Series2LabelProperty =
        AvaloniaProperty.Register<LineChartControl, string?>(nameof(Series2Label), "↑");

    /// <summary>下载速度序列（蓝色）</summary>
    public IList<double>? Series1
    {
        get => GetValue(Series1Property);
        set => SetValue(Series1Property, value);
    }

    /// <summary>上传速度序列（绿色）</summary>
    public IList<double>? Series2
    {
        get => GetValue(Series2Property);
        set => SetValue(Series2Property, value);
    }

    public string? Series1Label
    {
        get => GetValue(Series1LabelProperty);
        set => SetValue(Series1LabelProperty, value);
    }

    public string? Series2Label
    {
        get => GetValue(Series2LabelProperty);
        set => SetValue(Series2LabelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == Series1Property)
        {
            SubscribeSeriesCollection(ref _series1Collection, Series1);
            InvalidateVisual();
        }
        else if (change.Property == Series2Property)
        {
            SubscribeSeriesCollection(ref _series2Collection, Series2);
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        SubscribeSeriesCollection(ref _series1Collection, null);
        SubscribeSeriesCollection(ref _series2Collection, null);
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeSeriesCollection(ref INotifyCollectionChanged? current, IList<double>? series)
    {
        if (current != null)
            current.CollectionChanged -= OnSeriesCollectionChanged;

        current = series as INotifyCollectionChanged;
        if (current != null)
            current.CollectionChanged += OnSeriesCollectionChanged;
    }

    private void OnSeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        const double padLeft = 4;
        const double padRight = 4;
        const double padTop = 4;
        const double padBottom = 4;

        double chartW = w - padLeft - padRight;
        double chartH = h - padTop - padBottom;

        if (chartW <= 0 || chartH <= 0) return;

        // 背景
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            new Rect(0, 0, w, h));

        // 网格线（3条水平虚线效果）
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 200, 200, 200)), 1);
        for (int i = 1; i <= 3; i++)
        {
            double y = padTop + chartH * i / 4;
            context.DrawLine(gridPen,
                new Point(padLeft, y),
                new Point(padLeft + chartW, y));
        }

        var s1 = Series1;
        var s2 = Series2;

        // 计算最大值（两个系列取最大，用于归一化）
        double maxVal = 1024; // 最小 1KB/s，避免除零
        if (s1 != null) foreach (var v in s1) if (v > maxVal) maxVal = v;
        if (s2 != null) foreach (var v in s2) if (v > maxVal) maxVal = v;

        // 绘制 Series1（蓝色，下载）
        if (s1 != null && s1.Count >= 2)
            DrawSeries(context, s1, maxVal, padLeft, padTop, chartW, chartH,
                Color.Parse("#69B1FF"), Color.FromArgb(40, 105, 177, 255));

        // 绘制 Series2（绿色，上传）
        if (s2 != null && s2.Count >= 2)
            DrawSeries(context, s2, maxVal, padLeft, padTop, chartW, chartH,
                Color.Parse("#95DE64"), Color.FromArgb(40, 149, 222, 100));
    }

    private static void DrawSeries(DrawingContext context,
        IList<double> series, double maxVal,
        double padLeft, double padTop, double chartW, double chartH,
        Color lineColor, Color fillColor)
    {
        int count = series.Count;
        if (count < 2) return;

        var points = new Point[count];
        for (int i = 0; i < count; i++)
        {
            double x = padLeft + chartW * i / (count - 1);
            double normalized = maxVal <= 0 ? 0 : series[i] / maxVal;
            double y = padTop + chartH * (1.0 - normalized);
            if (series[i] <= 0)
                y = padTop + chartH - 1;
            points[i] = new Point(x, y);
        }

        // 填充区域
        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, padTop + chartH), isFilled: true);
            ctx.LineTo(points[0]);
            for (int i = 1; i < count; i++)
                ctx.LineTo(points[i]);
            ctx.LineTo(new Point(points[count - 1].X, padTop + chartH));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(fillColor), null, fillGeo);

        // 折线
        var linePen = new Pen(new SolidColorBrush(lineColor), 1.5);
        for (int i = 1; i < count; i++)
            context.DrawLine(linePen, points[i - 1], points[i]);
    }
}
