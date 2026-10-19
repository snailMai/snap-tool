using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class MemoryChecks
{
    private static void Assert(bool value) { if (!value) throw new Exception("Memory optimization assertion failed."); }
    private static byte[] Pixels(BitmapSource image)
    {
        byte[] bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }

    internal static void Run(Action<string, Action> test)
    {
        CaptureMemoryChecks.Run(test);
        PinMemoryChecks.Run(test);
        test("OCR rejects invalid idle intervals without loading models", () =>
        {
            foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(uint.MaxValue) })
            {
                bool rejected = false;
                try { using var service = new OcrService(idleTimeout: timeout); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Assert(rejected);
            }
        });
        test("scroll frame pixels stay independent of external buffers and source bitmaps", () =>
        {
            byte[] pixels = new byte[64 * 80 * 4]; pixels[0] = 51;
            var frame = new ScrollFrame(64, 80, pixels); pixels[0] = 99; Assert(frame.Pixels.Span[0] == 51);
            var bitmap = new WriteableBitmap(64, 80, 96, 96, PixelFormats.Bgr32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, 64, 80), pixels, 64 * 4, 0);
            var captured = ScrollImages.Frame(bitmap); pixels[0] = 1;
            bitmap.WritePixels(new Int32Rect(0, 0, 64, 80), pixels, 64 * 4, 0);
            Assert(captured.Pixels.Span[0] == 99);
            bool rejected = false;
            try { ScrollFrame.FromOwnedPixels(64, 80, new byte[1]); } catch (ArgumentException) { rejected = true; }
            Assert(rejected);
        });
        test("chunked long image composition is exact frozen cancellable and repeatable", () => OcrChecks.Pump(async () =>
        {
            const int width = 64, view = 96, height = 180;
            var data = new byte[width * height * 4]; new Random(2345).NextBytes(data);
            ScrollFrame Frame(int row) => new(width, view, data.AsSpan(row * width * 4, width * view * 4).ToArray());
            var stitcher = new ScrollStitcher(Frame(0)); Assert(stitcher.Append(Frame(40)) == ScrollAppendResult.Added);
            Assert(stitcher.Append(Frame(84)) == ScrollAppendResult.Added);
            using var cancel = new CancellationTokenSource();
            try { stitcher.CopyChunks((_, _) => cancel.Cancel(), cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Assert(stitcher.Compose().SequenceEqual(data));
            var image = await Task.Run(() => ScrollImages.Compose(stitcher, default));
            Assert(image.IsFrozen && image.PixelWidth == width && image.PixelHeight == height && Pixels(image).SequenceEqual(data));
            Assert(image.DpiX == 96 && image.DpiY == 96);
            GC.Collect(); GC.WaitForPendingFinalizers();
            using (var stream = new MemoryStream())
            {
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); png.Save(stream); stream.Position = 0;
                var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var bgr = new FormatConvertedBitmap(decoded, PixelFormats.Bgr32, null, 0); var actual = Pixels(bgr);
                // PNG does not preserve the unused fourth byte of BGR32.
                for (int p = 0; p < actual.Length; p++) if ((p & 3) != 3) Assert(actual[p] == data[p]);
            }
            Assert(Pixels(ScrollImages.Compose(stitcher, default)).SequenceEqual(data));
            try { ScrollImages.Compose(stitcher, cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Assert(stitcher.Compose().SequenceEqual(data));
        }));
        test("mapped long images become collectible without retaining native section handles", () =>
        {
            var stitcher = new ScrollStitcher(new(64, 96, new byte[64 * 96 * 4]));
            _ = ScrollImages.Compose(stitcher, default);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess(); int before = process.HandleCount;
            var references = CreateTemporaryImages(stitcher);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
            Assert(references.All(reference => !reference.TryGetTarget(out _)) && process.HandleCount <= before + 8);
        });
        test("strip mosaic matches scalar averages with alpha partial blocks and large grains", () =>
        {
            const int width = 37, height = 19; byte[] data = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4; byte alpha = (byte)(80 + (x + y) % 100);
                data[p] = (byte)(x % alpha); data[p + 1] = (byte)(y % alpha); data[p + 2] = (byte)((x * y) % alpha); data[p + 3] = alpha;
            }
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, data, width * 4); source.Freeze();
            var snapshot = new DesktopSnapshot(new(0, 0, width, height), [new(0, 0, width, height)], source);
            foreach (int grain in new[] { 1, 8, 30 })
            {
                var visual = new DrawingVisual(); var renderer = new MosaicRenderer(snapshot);
                using (var dc = visual.RenderOpen()) renderer.Draw(dc, new(AnnotationKind.Mosaic, new(2, 3), new(35, 18), 0, grain));
                var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); result.Render(visual);
                var actual = Pixels(result);
                for (int y = 3; y < 18; y++) for (int x = 2; x < 35; x++) for (int channel = 0; channel < 4; channel++)
                {
                    int left = 2 + (x - 2) / grain * grain, top = 3 + (y - 3) / grain * grain;
                    int right = Math.Min(left + grain, 35), bottom = Math.Min(top + grain, 18); long sum = 0;
                    for (int yy = top; yy < bottom; yy++) for (int xx = left; xx < right; xx++) sum += data[(yy * width + xx) * 4 + channel];
                    Assert(actual[(y * width + x) * 4 + channel] == sum / ((right - left) * (bottom - top)));
                }
            }
        });
        test("idle OCR unloads models and reloads with identical recognition", () => OcrChecks.Pump(async () =>
        {
            using var service = new OcrService(idleTimeout: TimeSpan.FromMilliseconds(300));
            Assert(!service.IsLoaded); var image = OcrChecks.Sample();
            string original = await service.Recognize(image, default); Assert(service.IsLoaded);
            var clock = Stopwatch.StartNew();
            while (service.IsLoaded && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Assert(!service.IsLoaded);
            Assert(await service.Recognize(image, default) == original && service.IsLoaded);
        }));
        test("OCR idle expiry does not interrupt active recognition and disposal stays safe", () => OcrChecks.Pump(async () =>
        {
            var service = new OcrService(idleTimeout: TimeSpan.FromMilliseconds(100));
            try
            {
                var work = service.Recognize(OcrChecks.Sample(false, 3840, 2160), default);
                var clock = Stopwatch.StartNew();
                while (!service.IsLoaded && !work.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
                await Task.Delay(150); if (!work.IsCompleted) Assert(service.IsLoaded);
                Assert((await work).Contains("截图文字识别"));
                service.Dispose(); service.Dispose(); Assert(!service.IsLoaded);
                try { await service.Recognize(OcrChecks.Sample(), default); throw new Exception("Expected disposal"); }
                catch (ObjectDisposedException) { }
            }
            finally { service.Dispose(); }
        }));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<BitmapSource>[] CreateTemporaryImages(ScrollStitcher stitcher) =>
        Enumerable.Range(0, 32).Select(_ => new WeakReference<BitmapSource>(ScrollImages.Compose(stitcher, default))).ToArray();
}
