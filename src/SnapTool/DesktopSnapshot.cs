using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal sealed class DesktopSnapshot
{
    internal PixelRect Bounds { get; }
    internal IReadOnlyList<PixelRect> Monitors { get; }
    internal BitmapSource Image { get; }

    internal DesktopSnapshot(PixelRect bounds, IReadOnlyList<PixelRect> monitors, BitmapSource image)
    { Bounds = bounds; Monitors = monitors; Image = image; }

    internal static DesktopSnapshot Capture()
    {
        var monitors = Native.Monitors();
        var bounds = PixelRect.Union(monitors);
        return new(bounds, monitors, CapturePixels(bounds, monitors));
    }

    internal static BitmapSource CaptureRegion(PixelRect rectangle)
    {
        if (rectangle.Width < 32 || rectangle.Height < 64 || (long)rectangle.Width * rectangle.Height > ScrollStitcher.MaxPixels ||
            !Native.Monitors().Exists(m => m.Intersect(rectangle) == rectangle))
            throw new InvalidOperationException("长截图区域需要完整位于同一显示器内，且尺寸不能过大。");
        return CapturePixels(rectangle, [rectangle]);
    }

    private static BitmapSource CapturePixels(PixelRect bounds, IReadOnlyList<PixelRect> monitors)
    {
        if ((long)bounds.Width * bounds.Height > 128_000_000)
            throw new InvalidOperationException("显示区域过大，无法安全分配截图内存。");
        return MappedCaptureBitmap.Create(bounds.Width, bounds.Height, targetDc =>
        {
            var screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                foreach (var screen in monitors)
                    if (!Native.BitBlt(targetDc, screen.Left - bounds.Left, screen.Top - bounds.Top, screen.Width, screen.Height,
                        screenDc, screen.Left, screen.Top, 0x00CC0020 | 0x40000000))
                        throw new System.ComponentModel.Win32Exception();
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
        });
    }

    internal BitmapSource Crop(PixelRect rectangle)
    {
        if (rectangle.IsEmpty || rectangle.Intersect(Bounds) != rectangle)
            throw new ArgumentOutOfRangeException(nameof(rectangle));
        var crop = new CroppedBitmap(Image, new Int32Rect(rectangle.Left - Bounds.Left, rectangle.Top - Bounds.Top,
            rectangle.Width, rectangle.Height));
        crop.Freeze();
        return crop;
    }
}
