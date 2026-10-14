using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal sealed class MosaicRenderer(DesktopSnapshot snapshot)
{
    private readonly Dictionary<(PixelRect Region, int Size), DrawingGroup> cache = new();
    internal int CachedRegions => cache.Count;
    internal void Clear() => cache.Clear();

    internal void Retain(IEnumerable<Annotation> marks)
    {
        var active = marks.Where(m => m.Kind == AnnotationKind.Mosaic)
            .Select(m => (m.Rectangle.Intersect(snapshot.Bounds), m.Thickness)).ToHashSet();
        foreach (var key in cache.Keys.Where(k => !active.Contains(k)).ToArray()) cache.Remove(key);
    }

    internal void Draw(DrawingContext dc, Annotation mark)
    {
        var region = mark.Rectangle.Intersect(snapshot.Bounds);
        if (region.IsEmpty) return;
        var key = (region, mark.Thickness);
        if (!cache.TryGetValue(key, out var drawing)) cache[key] = drawing = Create(region, mark.Thickness);
        dc.DrawDrawing(drawing);
    }

    private DrawingGroup Create(PixelRect region, int blockSize)
    {
        if (blockSize < 1) throw new ArgumentOutOfRangeException(nameof(blockSize));
        var source = new FormatConvertedBitmap(snapshot.Crop(region), PixelFormats.Pbgra32, null, 0);
        int stride = checked(region.Width * 4);
        byte[] input = new byte[checked(stride * region.Height)]; source.CopyPixels(input, stride, 0);
        int columns = (region.Width + blockSize - 1) / blockSize;
        int rows = (region.Height + blockSize - 1) / blockSize;
        byte[] blocks = new byte[checked(columns * rows * 4)];
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                int left = column * blockSize, top = row * blockSize;
                int right = Math.Min(left + blockSize, region.Width), bottom = Math.Min(top + blockSize, region.Height);
                long b = 0, g = 0, r = 0, a = 0;
                for (int y = top; y < bottom; y++)
                    for (int x = left; x < right; x++)
                    {
                        int pixel = y * stride + x * 4;
                        b += input[pixel]; g += input[pixel + 1]; r += input[pixel + 2]; a += input[pixel + 3];
                    }
                int count = (right - left) * (bottom - top), index = (row * columns + column) * 4;
                blocks[index] = (byte)(b / count); blocks[index + 1] = (byte)(g / count);
                blocks[index + 2] = (byte)(r / count); blocks[index + 3] = (byte)(a / count);
            }
        var image = BitmapSource.Create(columns, rows, 96, 96, PixelFormats.Pbgra32, null, blocks, columns * 4);
        image.Freeze();
        var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(region.Left, region.Top, region.Width, region.Height)) };
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        group.Children.Add(new ImageDrawing(image, new Rect(region.Left, region.Top, columns * blockSize, rows * blockSize)));
        group.Freeze(); return group;
    }
}
