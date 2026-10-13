using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal static class AnnotationRenderer
{
    // Filled edge strips keep the entire stroke inside the rectangle, even at crop edges.
    internal static void Draw(DrawingContext dc, IEnumerable<RectangleAnnotation> rectangles)
    {
        foreach (var mark in rectangles)
        {
            var r = mark.Rectangle;
            if (r.IsEmpty) continue;
            var brush = new SolidColorBrush(Color.FromRgb((byte)(mark.Rgb >> 16), (byte)(mark.Rgb >> 8), (byte)mark.Rgb));
            double w = System.Math.Min(mark.Thickness, System.Math.Min(r.Width, r.Height) / 2.0);
            dc.DrawRectangle(brush, null, new Rect(r.Left, r.Top, r.Width, w));
            dc.DrawRectangle(brush, null, new Rect(r.Left, r.Bottom - w, r.Width, w));
            dc.DrawRectangle(brush, null, new Rect(r.Left, r.Top + w, w, r.Height - 2 * w));
            dc.DrawRectangle(brush, null, new Rect(r.Right - w, r.Top + w, w, r.Height - 2 * w));
        }
    }

    internal static BitmapSource Compose(DesktopSnapshot snapshot, PixelRect crop, IReadOnlyList<RectangleAnnotation> rectangles)
    {
        var source = snapshot.Crop(crop);
        if (rectangles.Count == 0) return source;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, crop.Width, crop.Height));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, crop.Width, crop.Height)));
            dc.PushTransform(new TranslateTransform(-crop.Left, -crop.Top));
            Draw(dc, rectangles);
            dc.Pop(); dc.Pop();
        }
        var result = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
}
