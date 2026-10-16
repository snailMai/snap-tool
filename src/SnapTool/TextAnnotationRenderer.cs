using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SnapTool.Core;

namespace SnapTool;

internal static class TextAnnotationRenderer
{
    internal static double Padding(TextAnnotationStyle style) => style.Outline ? Math.Max(2, style.FontSize * 0.08) : 1;

    private static FormattedText Format(string text, TextAnnotationStyle style)
    {
        style.Validate();
        return new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily(style.FontFamily), style.Italic ? FontStyles.Italic : FontStyles.Normal,
                style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), style.FontSize, Brushes.Black, 1);
    }

    internal static PixelPoint Measure(string text, TextAnnotationStyle style)
    {
        var formatted = Format(text, style); double pad = Padding(style);
        var ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        double left = ink.IsEmpty ? 0 : Math.Min(0, ink.Left), top = ink.IsEmpty ? 0 : Math.Min(0, ink.Top);
        return new((int)Math.Ceiling(Math.Max(formatted.WidthIncludingTrailingWhitespace, ink.IsEmpty ? 0 : ink.Right) - left + pad * 2),
            (int)Math.Ceiling(Math.Max(formatted.Height, ink.IsEmpty ? 0 : ink.Bottom) - top + pad * 2));
    }

    internal static void Draw(DrawingContext dc, Annotation mark, Brush color)
    {
        if (string.IsNullOrWhiteSpace(mark.Text)) return;
        var r = mark.Rectangle; double pad = Padding(mark.TextStyle);
        if (mark.TextStyle.Background) dc.DrawRectangle(Brushes.White, null, new Rect(r.Left, r.Top, r.Width, r.Height));
        var glyph = Format(mark.Text, mark.TextStyle).BuildGeometry(new Point(0, 0));
        var ink = glyph.Bounds;
        glyph.Transform = new TranslateTransform(mark.Start.X + pad - Math.Min(0, ink.Left), mark.Start.Y + pad - Math.Min(0, ink.Top));
        Pen? outline = mark.TextStyle.Outline ? new Pen(Brushes.White, pad * 2) { LineJoin = PenLineJoin.Round } : null;
        dc.DrawGeometry(color, outline, glyph);
        // Repaint the fill so the white outline only expands outwards.
        if (outline != null) dc.DrawGeometry(color, null, glyph);
    }
}
