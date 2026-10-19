using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class MemoryBenchmarks
{
    internal static void Run(string scenario)
    {
        if (scenario == "ocr") { OcrChecks.Pump(Ocr); return; }
        var image = OcrChecks.Sample(false, 3840, 2160);
        object? kept = null;
        Func<object> operation;
        switch (scenario)
        {
            case "frame": operation = () => ScrollImages.Frame(image); break;
            case "compose":
                var stitcher = Page(); operation = () => ScrollImages.Compose(stitcher, default); break;
            case "mosaic":
                var snapshot = new DesktopSnapshot(new(0, 0, 3840, 2160), [new(0, 0, 3840, 2160)], image);
                var renderer = new MosaicRenderer(snapshot);
                var mark = new Annotation(AnnotationKind.Mosaic, new(0, 0), new(3840, 2160), 0, 30);
                operation = () => { var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) renderer.Draw(dc, mark); return visual; };
                break;
            case "captures":
                operation = () =>
                {
                    BitmapSource? last = null;
                    for (int i = 0; i < 20; i++)
                    {
                        var capture = new CaptureSession(new(new(0, 0, 3840, 2160), [new(0, 0, 3840, 2160)], image),
                            () => { }, (_, _) => { }, (_, _) => Task.FromResult(""));
                        capture.Selection.Select(new(0, 0, 3840, 2160)); last = capture.CreateOutput(); capture.Close();
                    }
                    return last!;
                };
                break;
            default: throw new ArgumentException("Unknown memory scenario.");
        }
        // Only the benchmark normalizes collection; application code never forces GC.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = Snapshot(); long allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew(); kept = operation(); clock.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var after = Snapshot(); string digest = Digest(kept);
        Console.WriteLine(JsonSerializer.Serialize(new { scenario, allocatedBytes = bytes, elapsedMs = clock.Elapsed.TotalMilliseconds,
            before, after, digest })); GC.KeepAlive(kept);
    }

    private static object Snapshot()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { workingSetMiB = process.WorkingSet64 / 1048576.0, privateMiB = process.PrivateMemorySize64 / 1048576.0,
            peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0, managedMiB = GC.GetTotalMemory(false) / 1048576.0 };
    }

    private static string Digest(object value)
    {
        if (value is ScrollFrame frame) return Convert.ToHexString(SHA256.HashData(frame.Pixels.Span));
        if (value is DrawingVisual visual)
        {
            var rendered = new RenderTargetBitmap(3840, 2160, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual); value = rendered;
        }
        var source = (BitmapSource)value;
        byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static ScrollStitcher Page()
    {
        const int width = 1920, view = 1080, height = 8000;
        var data = new byte[width * height * 4]; uint seed = 7654321;
        for (int i = 0; i < data.Length; i += 4)
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            data[i] = (byte)seed; data[i + 1] = (byte)(seed >> 8); data[i + 2] = (byte)(seed >> 16); data[i + 3] = 255;
        }
        ScrollFrame Frame(int row)
        {
            var pixels = new byte[width * view * 4]; Buffer.BlockCopy(data, row * width * 4, pixels, 0, pixels.Length);
            return new(width, view, pixels);
        }
        var stitcher = new ScrollStitcher(Frame(0));
        for (int row = 500; row < height - view + 500; row += 500)
            if (stitcher.Append(Frame(Math.Min(row, height - view))) != ScrollAppendResult.Added)
                throw new Exception("Synthetic benchmark stitching failed.");
        return stitcher;
    }

    private static async Task Ocr()
    {
        var constructor = typeof(OcrService).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length).First();
        using var service = (OcrService)constructor.Invoke(constructor.GetParameters().Length == 1 ? [null] : [null, TimeSpan.FromSeconds(1)]);
        var engine = typeof(OcrService).GetField("engine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var image = OcrChecks.Sample(false, 3840, 2160); var before = Snapshot();
        var clock = Stopwatch.StartNew(); string original = await service.Recognize(image, default); long coldMs = clock.ElapsedMilliseconds;
        clock.Restart(); string warm = await service.Recognize(image, default); long warmMs = clock.ElapsedMilliseconds;
        var loaded = Snapshot(); await Task.Delay(3000);
        bool loadedAfterIdle = engine.GetValue(service) != null; var idle = Snapshot();
        clock.Restart(); string again = await service.Recognize(image, default); long nextMs = clock.ElapsedMilliseconds;
        Console.WriteLine(JsonSerializer.Serialize(new { scenario = "ocr", testIdleTimeoutSeconds = constructor.GetParameters().Length == 1 ? (int?)null : 1,
            coldMs, warmMs, nextMs, loadedAfterIdle, before, loaded, idle, textUnchanged = original == warm && warm == again }));
    }
}
