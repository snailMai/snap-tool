using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SnapTool;
using SnapTool.Core;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Linq;
using System.Windows.Input;

namespace SnapTool.Tests;

internal static class RecordingChecks
{
    private static void Assert(bool condition, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    { if (!condition) throw new Exception($"Recording assertion failed at line {line}."); }
    private static string DirectoryPath
    {
        get { string path = Path.Combine(Program.FindRoot(), ".tmp", "recording-validation"); Directory.CreateDirectory(path); return path; }
    }
    private static SoftwareBitmap Solid(int width = 320, int height = 180)
    {
        byte[] bytes = new byte[width * height * 4];
        for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = 30; bytes[i + 1] = 100; bytes[i + 2] = 220; bytes[i + 3] = 255; }
        return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
    }
    internal static void Run(Action<string, Action> test)
    {
        RecordingCleanupChecks.Run(test);
        test("recording size fits 1080p bounds without upscaling or cropping odd dimensions", () =>
        {
            Assert(RecordingSize.Fit(new(0, 0, 3840, 2160)) == new RecordingSize(1920, 1080));
            Assert(RecordingSize.Fit(new(-101, -51, 0, 50)) == new RecordingSize(101, 101));
            Assert(RecordingSize.Fit(new(0, 0, 1080, 1920)) == new RecordingSize(607, 1080));
            foreach (var region in new[] { default(PixelRect), new PixelRect(0, 0, 15, 100) })
            { try { RecordingSize.Fit(region); throw new Exception("Expected invalid region"); } catch (ArgumentException) { } }
        });
        test("recording entry preserves selection on cancel failure annotations and cross-monitor", () =>
        {
            var bounds = new PixelRect(0, 0, 800, 500); int calls = 0, closes = 0; bool fail = false, accept = false;
            var session = new CaptureSession(new(bounds, [new(0, 0, 400, 500), new(400, 0, 800, 500)], OcrChecks.Sample(true, 800, 500)),
                () => closes++, (_, _) => { }, (_, _) => Task.FromResult(""), showRecording: _ => { calls++; if (fail) throw new Exception(); return accept; });
            session.StartRecording(); Assert(calls == 0);
            session.Selection.Select(bounds); session.StartRecording(); Assert(calls == 0);
            session.Selection.Select(new(20, 20, 340, 200)); session.StartRecording(); Assert(calls == 1 && closes == 0 && !session.Busy);
            fail = true; session.StartRecording(); Assert(calls == 2 && closes == 0 && !session.Busy);
            session.SetTool(AnnotationKind.Number); session.Begin(new(100, 100), 2); session.End(new(100, 100)); session.StartRecording(); Assert(calls == 2);
            session.UndoAnnotation(); fail = false; accept = true; session.StartRecording(); Assert(calls == 3 && closes == 1);
        });
        test("recording encoder writes a playable GIF with exact duration", CheckEncoder);
        test("recording GIF preserves one odd-sized frame and rejects an empty recording", () => OcrChecks.Pump(async () =>
        {
            string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".gif");
            int reads = 0;
            await RecordingEncoder.Encode(path, new(101, 99), _ => Task.FromResult<RecordingFrame?>(
                reads++ == 0 ? new(Solid(101, 99), TimeSpan.Zero) : null), CancellationToken.None);
            using (var file = File.OpenRead(path))
            using (var input = file.AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(input);
                Assert(decoder.FrameCount == 1 && decoder.PixelWidth == 101 && decoder.PixelHeight == 99);
            }
            string emptyPath = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".gif");
            var job = new RecordingJob(new(101, 99), DirectoryPath); job.Stop(); bool rejected = false;
            try { await job.Run(_ => throw new Exception("Unexpected read"), CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && !File.Exists(emptyPath) && job.PendingPath == null);
            Assert(!Directory.GetFiles(DirectoryPath, "." + Path.GetFileName(emptyPath) + ".*.recording.gif").Any());
        }));
        test("recording completion retains a GIF for copy and discard removes untransferred data", () => OcrChecks.Pump(async () =>
        {
            foreach (bool copy in new[] { false, true })
            {
                var job = new RecordingJob(new(320, 180), DirectoryPath); int reads = 0;
                await job.Run(_ => { if (++reads == 5) job.Stop(); return Task.FromResult(Solid()); }, CancellationToken.None);
                string path = job.PendingPath!;
                Assert(File.Exists(path));
                using (var file = File.OpenRead(path))
                using (var input = file.AsRandomAccessStream())
                {
                    var decoder = await BitmapDecoder.CreateAsync(input);
                    Assert(decoder.FrameCount == 5 && reads == 5);
                }
                if (copy) job.ReleaseToClipboard();
                job.Discard(); Assert(job.PendingPath == null && File.Exists(path) == copy);
            }
        }));
        test("recording cancellation and source failure preserve destination and remove partial files", () => OcrChecks.Pump(async () =>
        {
            foreach (bool cancel in new[] { false, true })
            {
                string directory = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "existing.gif"); File.WriteAllText(path, "existing");
                using var cancellation = new CancellationTokenSource(); var job = new RecordingJob(new(320, 180), directory); int reads = 0; bool failed = false;
                try
                {
                    await job.Run(token =>
                    {
                        if (++reads == 3) { if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); } throw new IOException("synthetic capture failure"); }
                        return Task.FromResult(Solid());
                    }, cancellation.Token);
                }
                catch { failed = true; }
                Assert(failed && job.PendingPath == null && File.ReadAllText(path) == "existing");
                Assert(Directory.GetFiles(directory).Length == 1);
            }
        }));
        test("GIF clipboard payload keeps file format and retries without writing the real clipboard", () => OcrChecks.Pump(async () =>
        {
            string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".gif");
            File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("GIF89a")); int attempts = 0;
            await ImageOutput.CopyGifWithRetry(path, CancellationToken.None, data =>
            {
                if (++attempts < 3) throw new System.Runtime.InteropServices.ExternalException("busy");
                Assert(data.GetFileDropList().Cast<string>().SequenceEqual(new[] { path }));
                Assert(!data.ContainsImage());
                Assert(data.GetData("Preferred DropEffect") is MemoryStream effect && effect.ToArray().SequenceEqual(BitConverter.GetBytes(1)));
            });
            Assert(attempts == 3);
            using var cancellation = new CancellationTokenSource(); attempts = 0;
            try
            {
                await ImageOutput.CopyGifWithRetry(path, cancellation.Token, _ =>
                { attempts++; cancellation.Cancel(); throw new System.Runtime.InteropServices.ExternalException("busy"); });
                throw new Exception("Cancellation was ignored");
            }
            catch (OperationCanceledException) { }
            Assert(attempts == 1);
        }));
    }

    internal static void CheckCapture()
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        OcrChecks.Pump(async () =>
        {
            var fixture = new System.Windows.Window { WindowStyle = System.Windows.WindowStyle.None, ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.Red, Width = 400, Height = 300, Topmost = true, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                fixture.Show(); var handle = new System.Windows.Interop.WindowInteropHelper(fixture).Handle;
                Native.SetWindowPos(handle, new IntPtr(-1), 100, 100, 400, 300, 0x0010);
                await Task.Delay(200);
                using var capture = new RecordingCapture(new(20, 20, 340, 200), new(0, 0, 400, 300), new(160, 90), handle);
                using var frame = await capture.Read(CancellationToken.None);
                byte[] pixels = new byte[160 * 90 * 4]; frame.CopyToBuffer(pixels.AsBuffer());
                if (frame.PixelWidth != 160 || frame.PixelHeight != 90 || pixels[2] < 240 || pixels[1] > 10 || pixels[0] > 10)
                    throw new Exception("Native capture crop/scale pixels differ from the synthetic window.");
                Console.WriteLine("PASS native WGC crop/scale: own red window 320x180 -> 160x90.");
                var cover = new System.Windows.Window { Width = 100, Height = 100, WindowStyle = System.Windows.WindowStyle.None,
                    Background = System.Windows.Media.Brushes.Blue, Topmost = true, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    ScrollCaptureView.Exclude(cover); cover.Show(); ScrollCaptureView.Position(cover, new(120, 120, 220, 220));
                    await Task.Delay(100);
                    var region = new PixelRect(120, 120, 440, 300);
                    using var monitorCapture = new RecordingCapture(region, Native.Monitors().Single(m => m.Intersect(region) == region), new(320, 180));
                    using var monitorFrame = await monitorCapture.Read(CancellationToken.None);
                    byte[] monitorPixels = new byte[320 * 180 * 4]; monitorFrame.CopyToBuffer(monitorPixels.AsBuffer());
                    Assert(monitorPixels[2] > 240 && monitorPixels[0] < 10);
                    Console.WriteLine("PASS monitor region capture excludes own overlay; only test-window region checked.");
                }
                finally { cover.Close(); }
                string? path = null;
                int notifications = 0, copies = 0;
                var shortcutProfile = ShortcutProfile.Default;
                using var videoCapture = new RecordingCapture(new(20, 20, 340, 200), new(0, 0, 400, 300), new(320, 180), handle);
                var recorder = new RecordingWindow(new(120, 120, 440, 300), (_, _) => notifications++, videoCapture.Read,
                    async (file, token) =>
                    {
                        path = file;
                        if (++copies == 1) throw new IOException("synthetic clipboard failure");
                        await Task.Delay(100, token);
                        await ImageOutput.CopyGifWithRetry(file, token, data => Assert(data.GetFileDropList()[0] == file));
                    }, DirectoryPath, getShortcuts: () => shortcutProfile);
                try
                {
                    recorder.Start();
                    var timeout = System.Diagnostics.Stopwatch.StartNew();
                    while (recorder.Frames < 5 && timeout.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(50);
                    Assert(recorder.Frames >= 5);
                    fixture.Background = System.Windows.Media.Brushes.Lime;
                    // The capture adapter reuses its destination, but previously returned frames remain independent.
                    await Task.Delay(100);
                    using (var changed = await capture.Read(CancellationToken.None))
                    {
                        byte[] changedPixels = new byte[160 * 90 * 4]; changed.CopyToBuffer(changedPixels.AsBuffer());
                        Assert(changedPixels[1] > changedPixels[2] + 100);
                        frame.CopyToBuffer(pixels.AsBuffer()); Assert(pixels[2] > pixels[1] + 100);
                        using var repeated = await capture.Read(CancellationToken.None);
                        repeated.CopyToBuffer(changedPixels.AsBuffer()); Assert(changedPixels[1] > changedPixels[2] + 100);
                    }
                    while (recorder.Frames < 15 && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
                    Assert(recorder.Frames >= 15);
                    foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Shift })
                        await ShortcutChecks.Press(recorder, Key.C, modifiers);
                    await ShortcutChecks.Press(recorder, Key.F2, ModifierKeys.Control); Assert(copies == 0);
                    var values = shortcutProfile.Export(); values[ShortcutAction.Copy] = new(Key.D, ModifierKeys.Control);
                    shortcutProfile = new(values); shortcutProfile.Validate(); recorder.RefreshShortcuts();
                    await ShortcutChecks.Press(recorder, Key.C, ModifierKeys.Control); Assert(copies == 0);
                    var stop = ShortcutChecks.Press(recorder, Key.D, ModifierKeys.Control);
                    await ShortcutChecks.Press(recorder, Key.D, ModifierKeys.Control); await stop;
                    Assert(path != null && File.Exists(path) && copies == 1 && notifications == 0 && recorder.IsVisible);
                    var retry = ShortcutChecks.Press(recorder, Key.D, ModifierKeys.Control);
                    await ShortcutChecks.Press(recorder, Key.D, ModifierKeys.Control); await retry;
                    Assert(copies == 2 && notifications == 1 && !recorder.IsVisible && !app.Windows.OfType<RecordingBorder>().Any());
                    using var file = File.OpenRead(path!); using var input = file.AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(input);
                    Assert(decoder.FrameCount == recorder.Frames);
                    foreach (bool last in new[] { false, true })
                    {
                        var decodedFrame = await decoder.GetFrameAsync(last ? decoder.FrameCount - 1 : 0);
                        using var bitmap = await decodedFrame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                        byte[] decodedPixels = new byte[320 * 180 * 4]; bitmap.CopyToBuffer(decodedPixels.AsBuffer());
                        Assert(last ? decodedPixels[1] > decodedPixels[2] + 100 : decodedPixels[2] > decodedPixels[1] + 100);
                    }
                    Console.WriteLine($"PASS live WGC to GIF: red/green frames decoded, frames={recorder.Frames}.");
                }
                finally { recorder.Close(); }
                var countdown = new RecordingWindow(new(120, 120, 440, 300), (_, _) => throw new Exception("Unexpected notification"),
                    _ => Task.FromResult(Solid()), (_, _) => throw new Exception("Unexpected copy"), DirectoryPath);
                countdown.Start(); await ShortcutChecks.Press(countdown, Key.C, ModifierKeys.Control); Assert(!countdown.IsVisible);
                string? discardedPath = null; int acceptedCopies = 0;
                var copying = new TaskCompletionSource();
                var canceled = new RecordingWindow(new(120, 120, 440, 300), (_, _) => throw new Exception("Unexpected notification"),
                    _ => Task.FromResult(Solid()), async (file, token) =>
                    { discardedPath = file; copying.SetResult(); await Task.Delay(Timeout.Infinite, token); acceptedCopies++; }, DirectoryPath);
                canceled.Start();
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (canceled.Frames < 3 && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
                Assert(canceled.Frames >= 3);
                var finishing = ShortcutChecks.Press(canceled, Key.C, ModifierKeys.Control); await copying.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await ShortcutChecks.Press(canceled, Key.Escape, ModifierKeys.None); await finishing;
                Assert(acceptedCopies == 0 && discardedPath != null && !File.Exists(discardedPath) && !canceled.IsVisible);
                Assert(!app.Windows.OfType<RecordingBorder>().Any());
                await RecordingCleanupChecks.CheckWindows();
                Console.WriteLine("PASS native recording controls: finish/copy, retry, repeated finish, countdown cancel, cancel pending copy, border cleanup.");
            }
            finally { fixture.Close(); }
        });
        app.Shutdown();
    }

    internal static void RenderPreview()
    {
        var screen = Native.Monitors()[0];
        var window = new RecordingWindow(new(screen.Left + 100, screen.Top + 100, screen.Left + 1380, screen.Top + 820),
            (_, _) => { });
        try
        {
            var content = (System.Windows.FrameworkElement)window.Content;
            content.Measure(new System.Windows.Size(440, 100)); content.Arrange(new System.Windows.Rect(0, 0, 440, 100)); content.UpdateLayout();
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(440, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawRectangle(window.Background, null, new System.Windows.Rect(0, 0, 440, 100));
            image.Render(visual); image.Render(content); ImageOutput.SavePng(image, Path.Combine(DirectoryPath, "controls.png"));
        }
        finally { window.Close(); }
    }

    internal static void CheckEncoder() => OcrChecks.Pump(async () =>
    {
        string dir = Path.Combine(Program.FindRoot(), ".tmp", "recording-validation"); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "synthetic-" + Guid.NewGuid().ToString("N") + ".gif");
        int frame = 0; var size = new RecordingSize(320, 180);
        await RecordingEncoder.Encode(path, size, _ =>
        {
            if (frame == 30) Assert(new FileInfo(path).Length > 0); // Frames reach disk before recording ends.
            if (frame == 60) return Task.FromResult<RecordingFrame?>(null);
            byte[] pixels = new byte[320 * 180 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(frame * 4); pixels[i + 1] = 100; pixels[i + 2] = 200; pixels[i + 3] = 255; }
            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, 320, 180, BitmapAlphaMode.Ignore);
            return Task.FromResult<RecordingFrame?>(new(bitmap, TimeSpan.FromSeconds(frame++ / 30.0)));
        }, CancellationToken.None);
        using var file = File.OpenRead(path); using var input = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input);
        Assert(decoder.FrameCount == 60 && decoder.PixelWidth == 320 && decoder.PixelHeight == 180);
        int totalDelay = 0; byte firstBlue = 0, lastBlue = 0;
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var decodedFrame = await decoder.GetFrameAsync(i);
            var metadata = await decodedFrame.BitmapProperties.GetPropertiesAsync(new[] { "/grctlext/Delay" });
            ushort delay = (ushort)metadata["/grctlext/Delay"].Value;
            Assert(delay is 3 or 4); totalDelay += delay;
            using var decoded = await decodedFrame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            byte[] pixels = new byte[320 * 180 * 4]; decoded.CopyToBuffer(pixels.AsBuffer());
            if (i == 0) firstBlue = pixels[0]; if (i == 59) lastBlue = pixels[0];
        }
        var loop = await decoder.BitmapContainerProperties.GetPropertiesAsync(new[] { "/appext/Application", "/appext/Data" });
        Assert(System.Text.Encoding.ASCII.GetString((byte[])loop["/appext/Application"].Value) == "NETSCAPE2.0");
        // WIC returns the data block without its on-disk zero terminator.
        Assert(((byte[])loop["/appext/Data"].Value).SequenceEqual(new byte[] { 3, 1, 0, 0 }));
        Assert(totalDelay == 200 && lastBlue > firstBlue + 150);
        Console.WriteLine($"PASS GIF encode/decode: 320x180, 60 frames, duration={totalDelay / 100.0:F2}s, infinite loop, path={path}");
    });
}
