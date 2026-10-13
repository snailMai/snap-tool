using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal static class AnnotationRenderer
{
    // Filled edge strips keep the entire stroke inside the rectangle, even at crop edges.
    internal static void Draw(DrawingContext dc, IEnumerable<Annotation> marks)
    {
        foreach (var mark in marks)
        {
            var r = mark.Rectangle;
            var brush = new SolidColorBrush(Color.FromRgb((byte)(mark.Rgb >> 16), (byte)(mark.Rgb >> 8), (byte)mark.Rgb));
            if (mark.Kind != AnnotationKind.Rectangle)
            {
                if (mark.Length < 2) continue;
                var start = new Point(mark.Start.X, mark.Start.Y); var end = new Point(mark.End.X, mark.End.Y);
                var pen = new Pen(brush, mark.Thickness);
                if (mark.Kind == AnnotationKind.Arrow)
                {
                    var head = mark.ArrowHead();
                    var left = new Point(head.Left.X, head.Left.Y); var right = new Point(head.Right.X, head.Right.Y);
                    pen.Thickness = System.Math.Min(mark.Thickness, (left - right).Length);
                    // Stop the shaft inside the head so a thick stroke never protrudes past the tip.
                    dc.DrawLine(pen, start, new Point((left.X + right.X) / 2, (left.Y + right.Y) / 2));
                    var triangle = new StreamGeometry();
                    using (var context = triangle.Open())
                    { context.BeginFigure(end, true, true); context.LineTo(left, true, false); context.LineTo(right, true, false); }
                    dc.DrawGeometry(brush, null, triangle);
                }
                else dc.DrawLine(pen, start, end);
                continue;
            }
            if (r.IsEmpty) continue;
            double w = System.Math.Min(mark.Thickness, System.Math.Min(r.Width, r.Height) / 2.0);
            dc.DrawRectangle(brush, null, new Rect(r.Left, r.Top, r.Width, w));
            dc.DrawRectangle(brush, null, new Rect(r.Left, r.Bottom - w, r.Width, w));
            dc.DrawRectangle(brush, null, new Rect(r.Left, r.Top + w, w, r.Height - 2 * w));
            dc.DrawRectangle(brush, null, new Rect(r.Right - w, r.Top + w, w, r.Height - 2 * w));
        }
    }

    internal static BitmapSource Compose(DesktopSnapshot snapshot, PixelRect crop, IReadOnlyList<Annotation> marks)
    {
        var source = snapshot.Crop(crop);
        if (marks.Count == 0) return source;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, crop.Width, crop.Height));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, crop.Width, crop.Height)));
            dc.PushTransform(new TranslateTransform(-crop.Left, -crop.Top));
            Draw(dc, marks);
            dc.Pop(); dc.Pop();
        }
        var result = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
}
