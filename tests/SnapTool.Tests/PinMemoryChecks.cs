using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;

namespace SnapTool.Tests;

internal static class PinMemoryChecks
{
    private static void Assert(bool value) { if (!value) throw new Exception("Pin memory assertion failed."); }
    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var result = new byte[checked(source.PixelWidth * source.PixelHeight * 4)]; converted.CopyPixels(result, source.PixelWidth * 4, 0); return result;
    }

    internal static void Run(Action<string, Action> test)
    {
        test("strip pin copies preserve alpha formats remainder rows and single pixels", () =>
        {
            foreach (var (width, height) in new[] { (1, 1), (7, 3), (257, 149), (17001, 2) })
            foreach (var format in new[] { PixelFormats.Pbgra32, PixelFormats.Bgra32, PixelFormats.Bgr32 })
            {
                byte[] data = new byte[width * height * 4];
                for (int p = 0; p < data.Length; p += 4)
                {
                    byte alpha = (byte)(p / 4 % 256); data[p + 3] = alpha;
                    data[p] = (byte)(alpha / 3); data[p + 1] = (byte)(alpha / 2); data[p + 2] = alpha;
                }
                var source = BitmapSource.Create(width, height, 120, 144, format, null, data, width * 4); source.Freeze();
                var result = PinWindow.Detach(source);
                Assert(result.IsFrozen && result.PixelWidth == width && result.PixelHeight == height && result.Format == PixelFormats.Pbgra32);
                Assert(result.DpiX == 96 && result.DpiY == 96 && Pixels(result).SequenceEqual(Pixels(source)));
            }
            try { PinWindow.Detach(null!); throw new Exception("Expected null input rejection"); }
            catch (ArgumentNullException) { }
        });
        test("pin owns pixels without retaining cropped mutable source and encodes across threads", () => OcrChecks.Pump(async () =>
        {
            var (image, source) = DetachedCrop();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert(!source.TryGetTarget(out _));
            var before = Pixels(image); Assert(before[3] == 127 && before[2] == 127);
            var decoded = await Task.Run(() =>
            {
                using var stream = new MemoryStream(); var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(image)); png.Save(stream); stream.Position = 0;
                return new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            });
            Assert(decoded.PixelWidth == 4 && decoded.PixelHeight == 5 && Pixels(decoded).SequenceEqual(before));
        }));
        test("closed independent pins release image references and native mapping handles", () =>
        {
            // Warm the complete window path: first construction initializes WPF's shared resources.
            _ = ClosedPins();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess(); int handles = process.HandleCount;
            var weak = ClosedPins();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
            Console.WriteLine($"Pin cleanup after warmup: alive={weak.Count(reference => reference.TryGetTarget(out _))}, handles={handles}->{process.HandleCount}");
            Assert(weak.All(reference => !reference.TryGetTarget(out _)) && process.HandleCount <= handles + 8);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (BitmapSource Image, WeakReference<WriteableBitmap> Source) DetachedCrop()
    {
        var source = new WriteableBitmap(10, 10, 96, 96, PixelFormats.Pbgra32, null);
        byte[] pixels = new byte[400];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 127; pixels[i + 3] = 127; }
        source.WritePixels(new Int32Rect(0, 0, 10, 10), pixels, 40, 0);
        var image = PinWindow.Detach(new CroppedBitmap(source, new Int32Rect(2, 3, 4, 5)));
        source.WritePixels(new Int32Rect(0, 0, 10, 10), new byte[400], 40, 0);
        return (image, new(source));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<BitmapSource>[] ClosedPins()
    {
        return Enumerable.Range(0, 24).Select(_ =>
        {
            var image = PinWindow.Detach(OcrChecks.Sample(true, 16, 16));
            var window = new PinWindow(image, new(0, 0, 16, 16)); window.Close();
            Assert(window.Original == null); return new WeakReference<BitmapSource>(image);
        }).ToArray();
    }
}
