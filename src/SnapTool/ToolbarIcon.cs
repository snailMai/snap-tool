using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SnapTool;

internal sealed class ToolbarIcon : FrameworkElement
{
    internal static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(ToolbarIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    private readonly string name;
    private static readonly IReadOnlyDictionary<string, Geometry> Outlines = CreateOutlines();

    internal ToolbarIcon(string name)
    { this.name = name; Width = Height = 24; IsHitTestVisible = false; }

    private static IReadOnlyDictionary<string, Geometry> CreateOutlines()
    {
        var paths = new Dictionary<string, string>
        {
            ["矩形"] = "M4,3 L20,3 20,21 4,21 Z",
            ["箭头"] = "M4,20 L20,4 M9,4 L20,4 20,15",
            ["文字"] = "M4,4 L20,4 M12,4 L12,21 M8,21 L16,21",
            ["撤销"] = "M9,5 L3,10 9,15 M3,10 L14,10 C23,10 23,21 14,21 L8,21",
            ["长截图"] = "M5,3 L5,21 19,21 19,3 Z M2,8 L7,8 M17,8 L22,8 M12,6 L12,17 M9,14 L12,17 15,14",
            ["录屏"] = "M3,6 L15,6 15,19 3,19 Z M15,10 L22,6 22,19 15,15",
            ["OCR"] = "M3,8 L3,3 8,3 M16,3 L21,3 21,8 M3,16 L3,21 8,21 M16,21 L21,21 21,16",
            ["贴图"] = "M10,3 L20,13 17,16 14,15 10,19 5,14 9,10 8,6 Z M7,17 L3,22",
            ["保存"] = "M12,2 L12,15 M7,10 L12,15 17,10 M4,16 L4,21 20,21 20,16",
            ["取消"] = "M5,5 L19,19 M19,5 L5,19",
            ["复制"] = "M8,3 L21,3 21,17 8,17 Z M8,7 L3,7 3,22 16,22 16,17"
        };
        var result = new Dictionary<string, Geometry>();
        foreach (var (key, path) in paths) { var geometry = Geometry.Parse(path); geometry.Freeze(); result.Add(key, geometry); }
        return result;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var pen = new Pen(Stroke, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (name == "马赛克")
        {
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
                if ((x + y) % 2 == 0) dc.DrawRectangle(Stroke, null, new Rect(3 + x * 6, 3 + y * 6, 6, 6));
        }
        else if (name == "序号")
        {
            dc.DrawEllipse(null, pen, new Point(12, 12), 9, 9);
            var text = new FormattedText("1", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 16, Stroke, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(12 - text.Width / 2, 12 - text.Height / 2));
        }
        else if (name == "OCR")
        {
            dc.DrawGeometry(null, pen, Outlines[name]);
            DrawLabel(dc, "OCR", "Segoe UI", 10, FontWeights.Bold, 20, 8);
        }
        else if (name == "翻译") DrawLabel(dc, "译", "Microsoft YaHei UI", 22, FontWeights.Normal, 20, 20, 0.35);
        else if (Outlines.TryGetValue(name, out var geometry)) dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawLabel(DrawingContext dc, string label, string font, double size, FontWeight weight, double width, double height, double outline = 0)
    {
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal), size, Stroke, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var glyph = text.BuildGeometry(new Point(0, 0));
        var bounds = glyph.Bounds;
        double factor = Math.Min(width / bounds.Width, height / bounds.Height);
        dc.PushTransform(new TranslateTransform(12 - (bounds.X + bounds.Width / 2) * factor, 12 - (bounds.Y + bounds.Height / 2) * factor));
        dc.PushTransform(new ScaleTransform(factor, factor));
        dc.DrawGeometry(Stroke, outline > 0 ? new Pen(Stroke, outline) { LineJoin = PenLineJoin.Round } : null, glyph);
        dc.Pop(); dc.Pop();
    }
}
