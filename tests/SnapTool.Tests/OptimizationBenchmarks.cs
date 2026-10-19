using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;
using RapidOcrNet;
using SkiaSharp;

namespace SnapTool.Tests;

internal static class OptimizationBenchmarks
{
    internal static void Run(string scenario) => OcrChecks.Pump(() => scenario switch
    {
        "scroll-samples" => ScrollSamples(),
        "recording-frames" => Recording(),
        "pin-detach" => PinDetach(),
        _ when scenario.StartsWith("capture-") => Capture(scenario),
        "ocr-default" or "ocr-no-arena" or "ocr-no-pattern" or "ocr-no-both" => Ocr(scenario),
        _ => throw new ArgumentException("Unknown optimization benchmark scenario.", nameof(scenario))
    });

    private static Task PinDetach()
    {
        var source = OcrChecks.Sample(false, 3840, 2160);
        byte[] pixels = new byte[3840 * 2160 * 4];
        // Prime both input and output reads before measuring; the product never forces GC.
        source.CopyPixels(pixels, 3840 * 4, 0);
        _ = PinWindow.Detach(OcrChecks.Sample(true, 8, 8));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = Snapshot(); long allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
        var image = PinWindow.Detach(source); image.CopyPixels(pixels, 3840 * 4, 0);
        clock.Stop(); long bytes = GC.GetTotalAllocatedBytes(true) - allocated; var after = Snapshot();
        Console.WriteLine(JsonSerializer.Serialize(new { scenario = "pin-detach", before, after, allocatedBytes = bytes,
            elapsedMs = clock.Elapsed.TotalMilliseconds, digest = Convert.ToHexString(SHA256.HashData(pixels)) }));
        GC.KeepAlive(image); GC.KeepAlive(source); return Task.CompletedTask;
    }

    private static Task Capture(string scenario)
    {
        int width = scenario.EndsWith("dual") ? 7680 : 3840; const int height = 2160;
        BitmapSource Create() => scenario.Contains("legacy") ? LegacyCapture(width, height) : MappedCaptureBitmap.Create(width, height, Draw);
        using (var warmup = new System.Drawing.Bitmap(1, 1)) { }
        byte[] pixels = new byte[width * height * 4];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = Snapshot(); long allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
        var image = Create(); image.CopyPixels(pixels, width * 4, 0);
        clock.Stop(); long bytes = GC.GetTotalAllocatedBytes(true) - allocated; var after = Snapshot();
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0;
        Console.WriteLine(JsonSerializer.Serialize(new { scenario, before, after, allocatedBytes = bytes,
            elapsedMs = clock.Elapsed.TotalMilliseconds, digest = Convert.ToHexString(SHA256.HashData(pixels)) }));
        GC.KeepAlive(image); return Task.CompletedTask;

        void Draw(IntPtr dc) { using var graphics = System.Drawing.Graphics.FromHdc(dc); graphics.Clear(System.Drawing.Color.DarkSlateBlue); }
        BitmapSource LegacyCapture(int w, int h)
        {
            using var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Black); var dc = graphics.GetHdc();
                try { Draw(dc); } finally { graphics.ReleaseHdc(dc); }
            }
            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, data.Scan0, checked(data.Stride * h), data.Stride);
                result.Freeze(); return result;
            }
            finally { bitmap.UnlockBits(data); }
        }
    }

    private static async Task Recording()
    {
        var fixture = new System.Windows.Window { Width = 1920, Height = 1080, WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode = System.Windows.ResizeMode.NoResize, Background = System.Windows.Media.Brushes.Red,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            fixture.Show(); var handle = new System.Windows.Interop.WindowInteropHelper(fixture).Handle;
            Native.SetWindowPos(handle, new IntPtr(-1), 100, 100, 1920, 1080, 0x0010); await Task.Delay(250);
            using var capture = new RecordingCapture(new(0, 0, 1920, 1080), new(0, 0, 1920, 1080), new(1920, 1080), handle);
            using (var warmup = await capture.Read(default)) { }
            byte[] pixels = new byte[1920 * 1080 * 4]; long readTicks = 0;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var before = Snapshot(); var clock = Stopwatch.StartNew();
            for (int i = 0; i < 60; i++)
            {
                // A static window only exercises cached-frame copies. Repaint every input frame.
                bool green = (i & 1) == 0;
                fixture.Background = green ? Brushes.Lime : Brushes.Red;
                await Task.Delay(50);
                long started = Stopwatch.GetTimestamp();
                using var frame = await capture.Read(CancellationToken.None);
                readTicks += Stopwatch.GetTimestamp() - started;
                frame.CopyToBuffer(pixels.AsBuffer());
                if (green ? pixels[1] < 200 || pixels[2] > 50 : pixels[2] < 200 || pixels[1] > 50)
                    throw new Exception("Recording frame did not update.");
            }
            clock.Stop(); Console.WriteLine(JsonSerializer.Serialize(new { scenario = "recording-frames", before, after = Snapshot(),
                frames = 60, elapsedMs = clock.Elapsed.TotalMilliseconds, readMs = readTicks * 1000.0 / Stopwatch.Frequency }));
        }
        finally { fixture.Close(); }
    }

    private static async Task ScrollSamples()
    {
        var image = OcrChecks.Sample(false, 3840, 2160);
        var window = new ScrollCaptureWindow(new(0, 0, 3840, 2160), image, (_, _, _) => Task.CompletedTask, capture: () => image);
        try
        {
            await window.SampleOnce(); await window.SampleOnce();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var before = Snapshot(); long allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) await window.SampleOnce();
            clock.Stop(); long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            Console.WriteLine(JsonSerializer.Serialize(new { scenario = "scroll-samples", before, after = Snapshot(),
                allocatedBytes = bytes, elapsedMs = clock.Elapsed.TotalMilliseconds, height = window.ImageHeight }));
        }
        finally { window.Close(); }
    }

    private static Task Ocr(string scenario)
    {
        using var options = RapidOcr.GetDefaultSessionOptions(Math.Min(4, Environment.ProcessorCount));
        options.EnableCpuMemArena = !scenario.Contains("no-arena") && scenario != "ocr-no-both";
        options.EnableMemoryPattern = !scenario.Contains("no-pattern") && scenario != "ocr-no-both";
        var image = OcrChecks.Sample(false, 3840, 2160);
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        using var input = new SKBitmap(image.PixelWidth, image.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        converted.CopyPixels(System.Windows.Int32Rect.Empty, input.GetPixels(), checked(input.RowBytes * input.Height), input.RowBytes);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = Snapshot(); var clock = Stopwatch.StartNew();
        using (var engine = new RapidOcr())
        {
            string Model(string name) => Path.Combine(AppContext.BaseDirectory, "models", "v5", name);
            engine.InitModels(Model("ch_PP-OCRv5_det_mobile.onnx"), Model("ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                Model("ch_PP-OCRv5_rec_mobile.onnx"), Model("ppocrv5_dict.txt"), options);
            var times = new double[3]; string text = "";
            for (int i = 0; i < times.Length; i++)
            {
                if (i > 0) clock.Restart();
                string actual = engine.Detect(input, RapidOcrOptions.Default with { ImgResize = 2048 }).StrRes;
                times[i] = clock.Elapsed.TotalMilliseconds;
                if (i > 0 && text != actual) throw new Exception("Recognition changed between runs.");
                text = actual;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { scenario, before, loaded = Snapshot(), times,
                digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))) }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { scenario, disposed = Snapshot() }));
        return Task.CompletedTask;
    }

    internal static object Snapshot()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { workingSetMiB = process.WorkingSet64 / 1048576.0, privateMiB = process.PrivateMemorySize64 / 1048576.0,
            peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0, managedMiB = GC.GetTotalMemory(false) / 1048576.0,
            handles = process.HandleCount };
    }
}
