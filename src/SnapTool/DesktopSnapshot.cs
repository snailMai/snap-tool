using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Media;
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
        if ((long)bounds.Width * bounds.Height > 128_000_000)
            throw new InvalidOperationException("显示区域过大，无法安全分配截图内存。");
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Black);
            var screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                var targetDc = graphics.GetHdc();
                try
                {
                    foreach (var screen in monitors)
                        if (!Native.BitBlt(targetDc, screen.Left - bounds.Left, screen.Top - bounds.Top, screen.Width, screen.Height,
                            screenDc, screen.Left, screen.Top, 0x00CC0020 | 0x40000000))
                            throw new System.ComponentModel.Win32Exception();
                }
                finally { graphics.ReleaseHdc(targetDc); }
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null,
                data.Scan0, checked(data.Stride * data.Height), data.Stride);
            image.Freeze();
            return new(bounds, monitors, image);
        }
        finally { bitmap.UnlockBits(data); }
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
