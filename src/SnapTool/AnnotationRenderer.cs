using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal static class AnnotationRenderer
{
    // Filled edge strips keep the entire stroke inside the rectangle, even at crop edges.
    internal static void Draw(DrawingContext dc, IEnumerable<Annotation> marks, MosaicRenderer? mosaic = null)
    {
        mosaic?.Retain(marks);
        foreach (var mark in marks)
        {
            if (mark.Kind == AnnotationKind.Mosaic)
            {
                if (mosaic == null) throw new System.InvalidOperationException("马赛克渲染需要原始截图。");
                mosaic.Draw(dc, mark); continue;
            }
            var r = mark.Rectangle;
            var brush = new SolidColorBrush(Color.FromRgb((byte)(mark.Rgb >> 16), (byte)(mark.Rgb >> 8), (byte)mark.Rgb));
            if (mark.Kind == AnnotationKind.Text)
            { TextAnnotationRenderer.Draw(dc, mark, brush); continue; }
            if (mark.Kind == AnnotationKind.Number)
            {
                double radius = mark.NumberRadius;
                dc.DrawEllipse(brush, null, new Point(mark.Start.X, mark.Start.Y), radius, radius);
                var color = brush.Color;
                var foreground = (color.R * 299 + color.G * 587 + color.B * 114) / 1000 > 160 ? Brushes.Black : Brushes.White;
                var text = new FormattedText(mark.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), radius * 1.2, foreground, 1);
                double scale = System.Math.Min(1, radius * 1.5 / System.Math.Max(text.Width, text.Height));
                dc.PushTransform(new TranslateTransform(mark.Start.X, mark.Start.Y));
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawText(text, new Point(-text.Width / 2, -text.Height / 2));
                dc.Pop(); dc.Pop();
                continue;
            }
            if (mark.Kind != AnnotationKind.Rectangle)
            {
                if (mark.Length < 2) continue;
                var points = mark.ArrowOutline(); var arrow = new StreamGeometry();
                using (var context = arrow.Open())
                {
                    context.BeginFigure(new Point(mark.Start.X, mark.Start.Y), true, true);
                    for (int i = 1; i < points.Length; i++) context.LineTo(new Point(points[i].X, points[i].Y), true, false);
                }
                dc.DrawGeometry(brush, null, arrow);
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

    internal static BitmapSource Compose(DesktopSnapshot snapshot, PixelRect crop, IReadOnlyList<Annotation> marks, MosaicRenderer? mosaic = null)
    {
        var source = snapshot.Crop(crop);
        mosaic?.Retain(marks);
        if (marks.Count == 0) return source;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, crop.Width, crop.Height));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, crop.Width, crop.Height)));
            dc.PushTransform(new TranslateTransform(-crop.Left, -crop.Top));
            Draw(dc, marks, mosaic ?? new MosaicRenderer(snapshot));
            dc.Pop(); dc.Pop();
        }
        var result = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
}
