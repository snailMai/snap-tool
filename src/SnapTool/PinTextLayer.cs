using System.Windows;
using System.Windows.Media;
using SnapTool.Core;

namespace SnapTool;

internal sealed class PinTextLayer(int width, int height) : FrameworkElement
{
    private static readonly Brush Highlight = new SolidColorBrush(Color.FromArgb(90, 51, 135, 255));
    internal OcrTextSelection? Selection { get; set; }
    protected override void OnRender(DrawingContext context)
    {
        if (Selection == null) return;
        double xScale = ActualWidth / width, yScale = ActualHeight / height;
        foreach (var glyph in Selection.Glyphs)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new(glyph.Corners[0].X * xScale, glyph.Corners[0].Y * yScale), true, true);
                for (int i = 1; i < glyph.Corners.Length; i++) path.LineTo(new(glyph.Corners[i].X * xScale, glyph.Corners[i].Y * yScale), true, false);
            }
            geometry.Freeze(); context.DrawGeometry(Highlight, null, geometry);
        }
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); InvalidateVisual(); }
}
