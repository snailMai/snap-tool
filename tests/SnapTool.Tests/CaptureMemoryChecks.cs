using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class CaptureMemoryChecks
{
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    private static void Assert(bool value) { if (!value) throw new Exception("Capture memory assertion failed."); }

    internal static void Paint(IntPtr dc)
    {
        using var graphics = System.Drawing.Graphics.FromHdc(dc);
        graphics.FillRectangle(System.Drawing.Brushes.Red, 0, 0, 32, 24);
        graphics.FillRectangle(System.Drawing.Brushes.Blue, 32, 24, 32, 24);
    }

    internal static void Run(Action<string, Action> test)
    {
        test("mapped capture rejects invalid size and null drawing before native allocation", () =>
        {
            foreach (var (width, height) in new[] { (0, 10), (10, -1), (int.MaxValue, int.MaxValue), (128_000_001, 1) })
            {
                try { MappedCaptureBitmap.Create(width, height, Paint); throw new Exception("Expected invalid size"); }
                catch (ArgumentOutOfRangeException) { }
            }
            try { MappedCaptureBitmap.Create(1, 1, null!); throw new Exception("Expected null drawing"); }
            catch (ArgumentNullException) { }
        });
        test("mapped capture preserves top-down pixels black gaps and frozen crops after encoding", () => OcrChecks.Pump(async () =>
        {
            var image = await Task.Run(() => MappedCaptureBitmap.Create(64, 48, Paint));
            Assert(image.IsFrozen && image.PixelWidth == 64 && image.PixelHeight == 48 && image.DpiX == 96 && image.DpiY == 96);
            GC.Collect(); GC.WaitForPendingFinalizers();
            using var stream = new MemoryStream(); var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream); stream.Position = 0;
            var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoded, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            byte[] bytes = new byte[64 * 48 * 4]; converted.CopyPixels(bytes, 64 * 4, 0);
            for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++)
            {
                int offset = (y * 64 + x) * 4;
                Assert(bytes[offset] == (x >= 32 && y >= 24 ? 255 : 0));
                Assert(bytes[offset + 1] == 0 && bytes[offset + 2] == (x < 32 && y < 24 ? 255 : 0));
            }
            var snapshot = new DesktopSnapshot(new(-70, -30, -6, 18), [new(-70, -30, -6, 18)], image);
            var crop = snapshot.Crop(new(-38, -6, -6, 18)); byte[] cropped = new byte[32 * 24 * 4]; crop.CopyPixels(cropped, 32 * 4, 0);
            Assert(cropped[0] == 255 && cropped[2] == 0);
        }));
        test("mapped capture releases GDI and mapping handles on success and drawing failure", () =>
        {
            _ = MappedCaptureBitmap.Create(64, 48, Paint);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess(); int handles = process.HandleCount; uint gdi = GetGuiResources(process.Handle, 0);
            var weak = TemporaryImages();
            for (int i = 0; i < 16; i++)
            {
                try { MappedCaptureBitmap.Create(64, 48, _ => throw new InvalidOperationException("synthetic drawing failure")); }
                catch (InvalidOperationException) { }
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
            Assert(weak.All(reference => !reference.TryGetTarget(out _)) && process.HandleCount <= handles + 8 && GetGuiResources(process.Handle, 0) <= gdi + 2);
        });
        test("scroll recycling preserves first chunk reference frames and clipboard retry pixels", () => OcrChecks.Pump(async () =>
        {
            const int width = 64, view = 96, height = 180;
            byte[] data = new byte[width * height * 4]; new Random(8321).NextBytes(data);
            BitmapSource Image(int row)
            {
                var source = BitmapSource.Create(width, view, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null,
                    data.AsSpan(row * width * 4, width * view * 4).ToArray(), width * 4); source.Freeze(); return source;
            }
            int copies = 0; var current = Image(0);
            var window = new ScrollCaptureWindow(new(0, 0, width, view), current, (output, _, _) =>
            {
                byte[] pixels = new byte[width * height * 4]; output.CopyPixels(pixels, width * 4, 0);
                Assert(output.PixelHeight == height && pixels.SequenceEqual(data));
                if (++copies == 1) throw new ExternalException("synthetic clipboard busy");
                return Task.CompletedTask;
            }, capture: () => current);
            try
            {
                for (int i = 0; i < 8; i++) await window.SampleOnce();
                foreach (int row in new[] { 40, 84 })
                {
                    current = Image(row); for (int i = 0; i < 6; i++) await window.SampleOnce();
                }
                Assert(window.ImageHeight == height);
                await window.Finish(); Assert(copies == 1 && window.Status.Contains("复制失败"));
                await window.Finish(); Assert(copies == 2);
            }
            finally { window.Close(); }
        }));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<BitmapSource>[] TemporaryImages() => Enumerable.Range(0, 32)
        .Select(_ => new WeakReference<BitmapSource>(MappedCaptureBitmap.Create(64, 48, Paint))).ToArray();
}
