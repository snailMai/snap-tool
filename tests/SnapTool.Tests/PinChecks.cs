using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class PinChecks
{
    private static void Assert(bool value) { if (!value) throw new Exception("Pin assertion failed"); }
    private static DesktopSnapshot Snapshot() => new(new(-100, -50, 300, 250), [new(-100, -50, 300, 250)], OcrChecks.Sample(true, 400, 300));
    private static CaptureSession Session(Action<BitmapSource, PixelRect> pin, Action? finished = null)
    {
        var s = new CaptureSession(Snapshot(), finished ?? (() => { }), (_, _) => { }, (_, _) => Task.FromResult(""), showPin: pin);
        s.Selection.Begin(new(-80, -30), 0); s.Selection.End(new(120, 120)); return s;
    }
    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4]; converted.CopyPixels(pixels, source.PixelWidth * 4, 0); return pixels;
    }
    internal static void Run(Action<string, Action> test)
    {
        test("pin large initial image fits negative monitor work area", () =>
        {
            var area = new PixelRect(-1920, -300, 0, 780); var g = new PinGeometry(3840, 2160, new(-100, 600), area);
            Assert(g.Bounds.Width == 1728 && g.Bounds.Height == 972 && g.Bounds.Intersect(area) == g.Bounds);
        });
        test("pin original size and zoom are independent of screen DPI", () =>
        {
            var area = new PixelRect(0, 0, 3840, 2160); var g = new PinGeometry(600, 400, new(100, 100), area);
            g.Zoom(1.5, area); Assert(g.Bounds.Width == 900 && g.Bounds.Height == 600);
            g.Move(new(-1900, 50), new(-1920, 0, 0, 1080)); g.Reset(new(-1920, 0, 0, 1080));
            Assert(g.Scale == 1 && g.Bounds.Width == 600 && g.Bounds.Height == 400 && g.Bounds.Left < 0);
        });
        test("pin zoom bounds and large reset retain reachable area", () =>
        {
            var area = new PixelRect(0, 0, 1920, 1040); var g = new PinGeometry(7680, 4320, new(0, 0), area);
            g.Zoom(1000, area); Assert(g.Scale == 4);
            g.Reset(area); Assert(g.Scale == 1 && g.Bounds.Intersect(area).Height >= 40);
            g.Move(new(-100000, -100000), area); Assert(g.Bounds.Intersect(area).Width >= 40 && g.Bounds.Intersect(area).Height >= 40);
            g.Zoom(0.0001, area); Assert(g.Bounds.Width >= 1 && g.Bounds.Height >= 1);
            g.Move(new(100000, 100000), area); Assert(g.Bounds.Intersect(area) == g.Bounds);
        });
        test("pin geometry rejects invalid dimensions and zoom", () =>
        {
            int rejected = 0;
            try { _ = new PinGeometry(0, 10, new(0, 0), new(0, 0, 100, 100)); } catch (ArgumentException) { rejected++; }
            var g = new PinGeometry(10, 10, new(0, 0), new(0, 0, 100, 100));
            foreach (double factor in new[] { 0, -1, double.NaN, double.PositiveInfinity })
                try { g.Zoom(factor, new(0, 0, 100, 100)); } catch (ArgumentException) { rejected++; }
            Assert(rejected == 5);
        });
        test("pin owns frozen pixels independent of mutable source", () =>
        {
            var source = new WriteableBitmap(4, 3, 96, 96, PixelFormats.Bgra32, null);
            var bytes = Enumerable.Repeat((byte)255, 48).ToArray(); source.WritePixels(new Int32Rect(0, 0, 4, 3), bytes, 16, 0);
            var image = PinWindow.Detach(new CroppedBitmap(source, new Int32Rect(1, 1, 2, 2)));
            source.WritePixels(new Int32Rect(0, 0, 4, 3), new byte[48], 16, 0);
            Assert(image.IsFrozen && image is not CroppedBitmap && image.PixelWidth == 2 && Pixels(image).All(p => p == 255));
        });
        test("pin final selection includes annotations and closes capture once", () =>
        {
            BitmapSource? received = null; PixelRect rect = default; int finished = 0, calls = 0;
            var s = Session((image, selection) => { received = image; rect = selection; calls++; }, () => finished++);
            s.SetTool(AnnotationKind.Rectangle); s.Begin(new(-70, -20), 2); s.End(new(100, 100)); var expected = Pixels(s.CreateOutput());
            s.Pin(); s.Pin();
            Assert(received != null && Pixels(received).SequenceEqual(expected) && rect == new PixelRect(-80, -30, 120, 120));
            Assert(finished == 1 && calls == 1 && !s.Busy);
        });
        test("pin ignores empty capture and both drawing gestures", () =>
        {
            int calls = 0; var s = Session((_, _) => calls++);
            s.Selection.Begin(new(200, 200), 0); s.Pin(); s.Selection.End(new(200, 200)); s.Pin();
            s.Selection.Begin(new(0, 0), 0); s.Selection.End(new(100, 100)); s.SetTool(AnnotationKind.Rectangle);
            s.Begin(new(10, 10), 2); s.Move(new(60, 60)); s.Pin(); Assert(calls == 0); s.Close(); s.Pin(); Assert(calls == 0);
        });
        test("pin creation failure retains selection and permits retry", () =>
        {
            int calls = 0, finished = 0; var s = Session((_, _) => { if (++calls == 1) throw new IOException("synthetic"); }, () => finished++);
            var selected = s.Selection.Rectangle; s.Pin();
            Assert(s.Status.Contains("失败") && !s.Busy && finished == 0 && s.Selection.Rectangle == selected);
            s.Pin(); Assert(calls == 2 && finished == 1);
        });
        test("pin is blocked during OCR", () => OcrChecks.Pump(async () =>
        {
            var result = new TaskCompletionSource<string>(); int calls = 0;
            var s = new CaptureSession(Snapshot(), () => { }, (_, _) => { }, (_, _) => result.Task, showPin: (_, _) => calls++);
            s.Selection.Begin(new(0, 0), 0); s.Selection.End(new(100, 100));
            var task = s.RecognizeText(); s.Pin(); Assert(calls == 0); result.SetResult(""); await task; s.Close();
        }));
        test("pin copy uses original resolution after display zoom", () => OcrChecks.Pump(async () =>
        {
            var source = PinWindow.Detach(OcrChecks.Sample(true, 100, 80)); BitmapSource? copied = null;
            var window = new PinWindow(source, new(0, 0, 100, 80), (image, _) => { copied = image; return Task.CompletedTask; });
            try { window.Zoom(2); await window.CopyImage(); Assert(window.Geometry.Bounds.Width == 200 && ReferenceEquals(source, copied)); }
            finally { window.Close(); }
            Assert(window.Original == null);
        }));
        test("pin failed copy retains image and permits retry", () => OcrChecks.Pump(async () =>
        {
            int calls = 0; var window = new PinWindow(OcrChecks.Sample(true, 100, 80), new(0, 0, 100, 80),
                (_, _) => ++calls == 1 ? Task.FromException(new IOException()) : Task.CompletedTask);
            try { await window.CopyImage(); Assert(window.Status.Contains("失败") && window.Original != null && !window.Busy); await window.CopyImage(); Assert(calls == 2); }
            finally { window.Close(); }
        }));
        test("pin close cancels pending copy and rejects duplicates", () => OcrChecks.Pump(async () =>
        {
            int calls = 0; CancellationToken captured = default; var pending = new TaskCompletionSource();
            var window = new PinWindow(OcrChecks.Sample(true, 100, 80), new(0, 0, 100, 80), (_, token) => { calls++; captured = token; return pending.Task; });
            var task = window.CopyImage(); await window.CopyImage(); Assert(calls == 1);
            DoubleClick(window, MouseButton.Left); Assert(captured.IsCancellationRequested && window.Original == null);
            pending.SetResult(); await task; Assert(window.Status == ""); await window.CopyImage(); Assert(calls == 1);
        }));
        test("pin cancelled clipboard operation never reaches system writer", () => OcrChecks.Pump(async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            bool cancelled = false;
            try { await ImageOutput.Copy(OcrChecks.Sample(true, 1, 1), cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled);
        }));
        test("pin PNG save preserves original size and failure keeps pin", () =>
        {
            string dir = Path.Combine(Program.FindRoot(), ".tmp", "pin-checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string? path = null; var window = new PinWindow(OcrChecks.Sample(true, 100, 80), new(0, 0, 100, 80), chooseSavePath: _ => path);
            try
            {
                window.Zoom(2); window.SaveImage(); Assert(Directory.GetFiles(dir).Length == 0 && window.Original != null);
                path = Path.Combine(dir, "missing", "pin.png"); window.SaveImage(); Assert(window.Status.Contains("失败") && !window.Busy);
                path = Path.Combine(dir, "pin.png"); window.SaveImage();
                using var stream = File.OpenRead(path); var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Assert(decoder.Frames[0].PixelWidth == 100 && decoder.Frames[0].PixelHeight == 80 && window.Original != null);
            }
            finally { window.Close(); Directory.Delete(dir, true); }
        });
        test("multiple pending pins close independently and close all", () =>
        {
            var manager = new PinWindows(); manager.Suspend(); var image = OcrChecks.Sample(true, 100, 80);
            var first = manager.Add(image, new(0, 0, 100, 80)); var second = manager.Add(image, new(150, 0, 250, 80));
            Assert(manager.Count == 2 && !first.IsVisible && !second.IsVisible);
            first.Close(); Assert(manager.Count == 1 && second.Original != null);
            manager.CloseAll(); manager.CloseAll(); Assert(manager.Count == 0 && second.Original == null); manager.Resume();
        });
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Native.Rect rectangle);
    private static void DoubleClick(PinWindow window, MouseButton button) =>
        window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
            { RoutedEvent = Control.MouseDoubleClickEvent });
    // Explicit integration entry: opens only synthetic test images, never captures the desktop or clipboard.
    internal static void CheckWindows()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        OcrChecks.Pump(async () =>
        {
            var manager = new PinWindows(); var image = PinWindow.Detach(OcrChecks.Sample());
            try
            {
                var screen = Native.Monitors()[0];
                var first = manager.Add(image, new(screen.Left + 100, screen.Top + 100, screen.Left + 1000, screen.Top + 320));
                var second = manager.Add(image, new(screen.Left + 150, screen.Top + 400, screen.Left + 1050, screen.Top + 620));
                await Task.Delay(200);
                Assert(first.IsVisible && second.IsVisible && first.Topmost && manager.Count == 2);
                CheckBounds(first);
                first.Zoom(1.5); await Task.Delay(100); CheckBounds(first); Assert(first.Geometry.Bounds.Width == 1350);
                ((MenuItem)first.ContextMenu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(100); CheckBounds(first); Assert(first.Geometry.Bounds.Width == 900);
                foreach (var monitor in Native.Monitors())
                {
                    first.Geometry.Move(new(monitor.Left + 60, monitor.Top + 60), monitor); first.Reposition();
                    await Task.Delay(150); CheckBounds(first);
                }
                manager.Suspend(); Assert(!first.IsVisible && !second.IsVisible);
                manager.Resume(); await Task.Delay(100); Assert(first.IsVisible && second.IsVisible);
                first.Zoom(1.5);
                DoubleClick(first, MouseButton.Right);
                Assert(manager.Count == 2 && first.Original != null && first.Geometry.Scale == 1.5);
                DoubleClick(first, MouseButton.Left);
                await Task.Delay(100);
                if (!(manager.Count == 1 && first.Original == null && first.Geometry.Scale == 1.5 && second.IsVisible))
                    throw new Exception($"Double-click check: windows={manager.Count}, imageReleased={first.Original == null}, scale={first.Geometry.Scale}, secondVisible={second.IsVisible}");
                manager.Suspend();
                var capture = Session((source, rectangle) => manager.Add(source, rectangle), manager.Resume);
                capture.Pin(); await Task.Delay(100); Assert(manager.Count == 2 && second.IsVisible);
                string dir = Path.Combine(Program.FindRoot(), ".tmp", "pin-preview"); Directory.CreateDirectory(dir);
                var content = (FrameworkElement)second.Content; content.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(second);
                var preview = new RenderTargetBitmap(900, 220, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32); preview.Render(content);
                Assert(Pixels(preview).SequenceEqual(Pixels(image)));
                ImageOutput.SavePng(preview, Path.Combine(dir, "pin.png"));
                second.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(second), Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert(manager.Count == 1 && second.Original == null);
                manager.CloseAll(); Assert(manager.Count == 0 && second.Original == null);
                Console.WriteLine("PASS native pin windows: size, zoom, context-menu reset, monitor moves, suspend/resume, double-click close, Esc close, close all");
            }
            finally { manager.CloseAll(); }
        });
        app.Shutdown();
    }
    private static void CheckBounds(PinWindow window)
    {
        Assert(GetWindowRect(new WindowInteropHelper(window).Handle, out var r));
        Assert(new PixelRect(r.Left, r.Top, r.Right, r.Bottom) == window.Geometry.Bounds);
    }
}
