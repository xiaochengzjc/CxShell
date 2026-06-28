using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CxShell.Controls;

public class PieSegment
{
    public double Value { get; set; }
    public Color Color { get; set; }
    public string? Label { get; set; }
}

public class PieChartControl : Control
{
    public static readonly StyledProperty<IList<PieSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<PieChartControl, IList<PieSegment>?>(nameof(Segments));

    public IList<PieSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var segments = Segments;
        double width = Bounds.Width;
        double height = Bounds.Height;
        double size = Math.Min(width, height);
        double cx = width / 2;
        double cy = height / 2;
        double radius = size / 2 - 4;
        double innerRadius = radius * 0.55;

        if (segments == null || segments.Count == 0 || radius <= 0)
        {
            // 空圆环
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(60, 200, 200, 200)), 2);
            context.DrawEllipse(null, pen,
                new Point(cx, cy), radius, radius);
            return;
        }

        double total = 0;
        foreach (var seg in segments) total += seg.Value;
        if (total <= 0) return;

        double startAngle = -Math.PI / 2; // 从顶部开始

        foreach (var seg in segments)
        {
            if (seg.Value <= 0) continue;
            double sweepAngle = seg.Value / total * 2 * Math.PI;

            DrawArcSegment(context, cx, cy, radius, innerRadius,
                startAngle, sweepAngle, seg.Color);

            startAngle += sweepAngle;
        }

        // 中心文字：显示已用百分比
        double usedPercent = segments.Count > 0 ? segments[0].Value / total * 100 : 0;
        var text = $"{usedPercent:F0}%";
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            innerRadius * 0.5,
            new SolidColorBrush(Color.Parse("#E0E0E0")));

        context.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    private static void DrawArcSegment(DrawingContext context,
        double cx, double cy, double outerR, double innerR,
        double startAngle, double sweepAngle, Color color)
    {
        if (sweepAngle < 0.001) return;

        bool isLargeArc = sweepAngle > Math.PI;
        double endAngle = startAngle + sweepAngle;

        var outerStart = new Point(cx + outerR * Math.Cos(startAngle), cy + outerR * Math.Sin(startAngle));
        var outerEnd = new Point(cx + outerR * Math.Cos(endAngle), cy + outerR * Math.Sin(endAngle));
        var innerStart = new Point(cx + innerR * Math.Cos(endAngle), cy + innerR * Math.Sin(endAngle));
        var innerEnd = new Point(cx + innerR * Math.Cos(startAngle), cy + innerR * Math.Sin(startAngle));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(outerStart, isFilled: true);
            ctx.ArcTo(outerEnd, new Size(outerR, outerR), 0, isLargeArc, SweepDirection.Clockwise);
            ctx.LineTo(innerStart);
            ctx.ArcTo(innerEnd, new Size(innerR, innerR), 0, isLargeArc, SweepDirection.CounterClockwise);
            ctx.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(color), null, geo);
    }
}
