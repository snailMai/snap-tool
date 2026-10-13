using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class Program
{
    private static int passed, failed;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--fixture")) { new Application().Run(new FixtureWindow(args.Contains("--secondary"))); return 0; }
        if (args.Contains("--translation-preview")) { TranslationChecks.RenderPreview(); return 0; }
        if (args.Contains("--rectangle-preview")) { AnnotationChecks.RenderPreview(); return 0; }
        if (args.Contains("--pin-window-checks")) { PinChecks.CheckWindows(); return 0; }
        if (args.Contains("--screens"))
        { foreach (var screen in Native.Monitors()) Console.WriteLine(screen); return 0; }
        Test("PerMonitorV2 DPI context", () => Equal(true, Native.AreDpiAwarenessContextsEqual(Native.GetThreadDpiAwarenessContext(), new IntPtr(-4))));
        Test("reverse drag", () =>
        {
            var s = Model(); s.Begin(new(80, 60), 0); s.End(new(20, 10));
            Equal(new PixelRect(20, 10, 80, 60), s.Rectangle);
        });
        Test("drag clips to bounds", () =>
        {
            var s = Model(); s.Begin(new(10, 10), 0); s.End(new(200, 200));
            Equal(new PixelRect(10, 10, 100, 100), s.Rectangle);
        });
        Test("zero-width selection", () =>
        { var s = Model(); s.Begin(new(20, 20), 0); s.End(new(20, 60)); Equal(true, s.Rectangle.IsEmpty); });
        Test("single-pixel selection", () =>
        { var s = Model(); s.Begin(new(20, 20), 0); s.End(new(21, 21)); Equal(1, s.Rectangle.Width); Equal(1, s.Rectangle.Height); });
        Test("move preserves size at top-left", () =>
        { var s = Selected(); s.Begin(new(40, 40), 2); s.End(new(-100, -100)); Equal(new PixelRect(0, 0, 40, 40), s.Rectangle); });
        Test("move preserves size at bottom-right", () =>
        { var s = Selected(); s.Begin(new(40, 40), 2); s.End(new(200, 200)); Equal(new PixelRect(60, 60, 100, 100), s.Rectangle); });
        Test("resize crosses opposite edges", () =>
        { var s = Selected(); s.Begin(new(20, 20), 2); s.End(new(80, 90)); Equal(new PixelRect(60, 60, 80, 90), s.Rectangle); });
        Test("edge resize preserves other axis", () =>
        { var s = Selected(); s.Begin(new(60, 40), 2); s.End(new(95, 80)); Equal(new PixelRect(20, 20, 95, 60), s.Rectangle); });
        Test("outside click begins new selection", () =>
        { var s = Selected(); s.Begin(new(80, 80), 2); s.End(new(90, 95)); Equal(new PixelRect(80, 80, 90, 95), s.Rectangle); });
        Test("corner hit testing", () => Equal(SelectionHit.Top | SelectionHit.Left, Selected().HitTest(new(19, 21), 3)));
        Test("center hit testing", () => Equal(SelectionHit.Move, Selected().HitTest(new(40, 40), 3)));
        Test("outside hit testing", () => Equal(SelectionHit.None, Selected().HitTest(new(1, 1), 3)));
        Test("ended drag ignores later movement", () =>
        { var s = Selected(); s.Update(new(100, 100)); Equal(new PixelRect(20, 20, 60, 60), s.Rectangle); });
        Test("invalid bounds rejected", () => Throws<ArgumentException>(() => new SelectionModel(default)));
        Test("empty monitor list rejected", () => Throws<ArgumentException>(() => PixelRect.Union([])));
        Test("negative and offset monitor union", () => Equal(new PixelRect(-1920, -300, 3840, 2160), PixelRect.Union([
            new(-1920, -300, 0, 780), new(0, 0, 3840, 2160)])));
        Test("cross-monitor physical selection", () =>
        {
            var s = new SelectionModel(new(-1920, -300, 3840, 2160));
            s.Begin(new(-100, -100), 0); s.End(new(200, 200));
            Equal(new PixelRect(-100, -100, 200, 200), s.Rectangle); Equal(300, s.Rectangle.Width);
        });
        Test("disjoint intersection is empty", () => Equal(true, new PixelRect(0, 0, 10, 10).Intersect(new(20, 20, 30, 30)).IsEmpty));
        Test("rectangle right bottom are exclusive", () => Equal(false, new PixelRect(0, 0, 10, 10).Contains(new(10, 5))));
        Test("synthetic crop uses physical origin", () =>
        {
            var image = Synthetic();
            var snapshot = new DesktopSnapshot(new(-100, -50, -96, -47), [new(-100, -50, -96, -47)], image);
            var crop = snapshot.Crop(new(-99, -49, -97, -47));
            Equal(2, crop.PixelWidth); Equal(2, crop.PixelHeight);
            byte[] pixels = new byte[16]; crop.CopyPixels(pixels, 8, 0);
            Equal((byte)5, pixels[0]); Equal((byte)6, pixels[4]); Equal((byte)9, pixels[8]); Equal((byte)10, pixels[12]);
        });
        Test("crop rejects outside image", () => Throws<ArgumentOutOfRangeException>(() => Snapshot().Crop(new(-1, 0, 2, 2))));
        Test("crop rejects empty image", () => Throws<ArgumentOutOfRangeException>(() => Snapshot().Crop(new(1, 1, 1, 2))));
        string root = FindRoot();
        string output = Path.Combine(root, ".tmp", "regression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            Test("PNG roundtrip retains pixels", () =>
            {
                string path = Path.Combine(output, "sample.png"); ImageOutput.SavePng(Synthetic(), path);
                using var stream = File.OpenRead(path);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Equal(4, decoder.Frames[0].PixelWidth); Equal(3, decoder.Frames[0].PixelHeight);
                var decoded = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgr32, null, 0);
                byte[] pixels = new byte[48]; decoded.CopyPixels(pixels, 16, 0); Equal((byte)11, pixels[44]);
            });
            Test("explicit replacement writes valid PNG", () =>
            {
                string path = Path.Combine(output, "replace.png"); File.WriteAllText(path, "previous");
                ImageOutput.SavePng(Synthetic(), path); Equal((byte)137, File.ReadAllBytes(path)[0]);
            });
            Test("locked destination survives save failure", () =>
            {
                string path = Path.Combine(output, "locked.png"); File.WriteAllText(path, "previous");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    bool rejected = false;
                    try { ImageOutput.SavePng(Synthetic(), path); }
                    catch (IOException) { rejected = true; }
                    catch (UnauthorizedAccessException) { rejected = true; }
                    Equal(true, rejected);
                }
                Equal("previous", File.ReadAllText(path)); Equal(0, Directory.GetFiles(output, "*.tmp").Length);
            });
            Test("missing destination directory fails cleanly", () =>
                Throws<DirectoryNotFoundException>(() => ImageOutput.SavePng(Synthetic(), Path.Combine(output, "missing", "sample.png"))));
        }
        finally { Directory.Delete(output, true); }
        Test("geometry invariant across 1000 deterministic drags", () =>
        {
            var random = new Random(20261013); var s = new SelectionModel(new(-200, -100, 300, 200));
            for (int i = 0; i < 1000; i++)
            {
                s.Begin(new(random.Next(-300, 400), random.Next(-200, 300)), 4);
                s.End(new(random.Next(-300, 400), random.Next(-200, 300)));
                var r = s.Rectangle;
                Equal(true, r.Left >= -200 && r.Top >= -100 && r.Right <= 300 && r.Bottom <= 200);
                Equal(false, s.Dragging);
            }
        });
        OcrChecks.Run(Test);
        TranslationChecks.Run(Test);
        AnnotationChecks.Run(Test);
        LineAnnotationChecks.Run(Test);
        PinChecks.Run(Test);
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    internal static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path != null && !File.Exists(Path.Combine(path.FullName, "AGENTS.md"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Project root not found.");
    }
    private static SelectionModel Model() => new(new(0, 0, 100, 100));
    private static SelectionModel Selected() { var s = Model(); s.Begin(new(20, 20), 0); s.End(new(60, 60)); return s; }
    private static BitmapSource Synthetic()
    {
        var bytes = new byte[48]; for (int i = 0; i < 12; i++) { bytes[i * 4] = (byte)i; bytes[i * 4 + 3] = 255; }
        var image = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgr32, null, bytes, 16); image.Freeze(); return image;
    }
    private static DesktopSnapshot Snapshot() => new(new(0, 0, 4, 3), [new(0, 0, 4, 3)], Synthetic());
    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
}
