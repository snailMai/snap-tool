using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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

internal static class ScrollChecks
{
    private const int Width = 96, ViewHeight = 128;
    private static void Assert(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null)
    { if (!value) throw new Exception("Scroll assertion failed: " + expression); }
    private static byte[] Document(int width, int height)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                uint n = unchecked((uint)(y * 472882027 + x * 776531419)); n ^= n >> 13; n *= 0x85ebca6b; n ^= n >> 16;
                int p = (y * width + x) * 4; bytes[p] = (byte)n; bytes[p + 1] = (byte)(n >> 8); bytes[p + 2] = (byte)(n >> 16);
            }
        return bytes;
    }
    private static ScrollFrame Frame(byte[] document, int top, int width = Width, int height = ViewHeight) =>
        new(width, height, document.AsSpan(top * width * 4, width * height * 4).ToArray());
    private static BitmapSource Bitmap(ScrollFrame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels.ToArray(), frame.Width * 4);
        bitmap.Freeze(); return bitmap;
    }
    private static readonly PixelRect Region = new(0, 0, Width, ViewHeight);

    internal static void Run(Action<string, Action> test)
    {
        test("scroll preview samples accepted chunks with bounded memory and cancellation", () =>
        {
            var data = Document(Width, 500); var stitcher = new ScrollStitcher(Frame(data, 0));
            foreach (int top in new[] { 50, 110, 180 }) Assert(stitcher.Append(Frame(data, top)) == ScrollAppendResult.Added);
            var preview = stitcher.Preview(48, 100);
            Assert(preview.Width <= 48 && preview.Height <= 100 && preview.Pixels.Length == preview.Width * preview.Height * 4);
            for (int y = 0; y < preview.Height; y++) for (int x = 0; x < preview.Width; x++)
                Assert(preview.Pixels.AsSpan((y * preview.Width + x) * 4, 4).SequenceEqual(data.AsSpan(
                    ((y * stitcher.Height / preview.Height) * Width + x * Width / preview.Width) * 4, 4)));
            Assert(stitcher.Preview(1024, 1024).Pixels.SequenceEqual(stitcher.Compose()));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { stitcher.Preview(48, 100, cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            try { stitcher.Preview(0, 100); throw new Exception("Expected invalid bound"); } catch (ArgumentOutOfRangeException) { }
        });
        test("scroll controls fit work area including negative and constrained coordinates", () =>
        {
            foreach (var work in new[] { new PixelRect(0, 0, 1920, 1080), new PixelRect(-1920, -100, 0, 980), new PixelRect(0, 0, 150, 100) })
            {
                var layout = ScrollViewLayout.Calculate(work, work, 480, 112);
                Assert(layout.Toolbar.Intersect(work) == layout.Toolbar && layout.Preview.Intersect(work) == layout.Preview);
                Assert(!layout.Toolbar.IsEmpty && !layout.Preview.IsEmpty);
            }
            var normal = ScrollViewLayout.Calculate(new(100, 100, 700, 700), new(0, 0, 1920, 1080), 480, 112);
            Assert(normal.Preview.Left > 700 && normal.Toolbar.Top > 700);
        });
        test("scroll rejects repaint differences beyond bounded color tolerance", () =>
        {
            var data = Document(Width, 400); var stitcher = new ScrollStitcher(Frame(data, 0));
            var shifted = Frame(data, 50).Pixels.ToArray();
            for (int p = 0; p < shifted.Length; p++) if (p % 4 != 3) shifted[p] = (byte)Math.Min(255, shifted[p] + 20);
            Assert(stitcher.Append(new(Width, ViewHeight, shifted)) == ScrollAppendResult.NoMatch);
            Assert(stitcher.Height == ViewHeight);
        });
        test("scroll tolerates small repaint differences without changing appended pixels", () =>
        {
            var data = Document(Width, 400); var stitcher = new ScrollStitcher(Frame(data, 0));
            var shifted = Frame(data, 50).Pixels.ToArray();
            for (int p = 0; p < shifted.Length; p++) if (p % 4 != 3) shifted[p] = (byte)Math.Min(255, shifted[p] + 2);
            Assert(stitcher.Append(new(Width, ViewHeight, shifted)) == ScrollAppendResult.Added);
            Assert(stitcher.Height == ViewHeight + 50);
            Assert(stitcher.Compose().Skip(Width * ViewHeight * 4).SequenceEqual(shifted.Skip(Width * (ViewHeight - 50) * 4)));
        });
        test("scroll stitches varying downward offsets with exact original pixels", () =>
        {
            var data = Document(Width, 600); var stitcher = new ScrollStitcher(Frame(data, 0));
            foreach (int top in new[] { 1, 33, 101, 150, 240, 300, 380, 450 })
            {
                Assert(stitcher.Append(Frame(data, top)) == ScrollAppendResult.Added);
                Assert(stitcher.Height == top + ViewHeight);
                Assert(stitcher.Compose().SequenceEqual(data.Take(stitcher.Height * Width * 4)));
            }
        });
        test("scroll repeated frames and unchanged bottom never add duplicate rows", () =>
        {
            var data = Document(Width, 400); var stitcher = new ScrollStitcher(Frame(data, 0));
            for (int i = 0; i < 4; i++) Assert(stitcher.Append(Frame(data, 0)) == ScrollAppendResult.Unchanged);
            Assert(stitcher.Append(Frame(data, 60)) == ScrollAppendResult.Added);
            for (int i = 0; i < 4; i++) Assert(stitcher.Append(Frame(data, 60)) == ScrollAppendResult.Unchanged);
            Assert(stitcher.Height == 188 && stitcher.Frames == 2);
        });
        test("scroll rejects excessive jumps upward movement unrelated content and fixed bars", () =>
        {
            var data = Document(Width, 500); var stitcher = new ScrollStitcher(Frame(data, 80));
            var original = stitcher.Compose();
            Assert(stitcher.Append(Frame(data, 0)) == ScrollAppendResult.NoMatch);
            Assert(stitcher.Append(Frame(data, 250)) == ScrollAppendResult.NoMatch);
            var fixedBar = Frame(data, 140).Pixels.ToArray();
            Array.Copy(Frame(data, 80).Pixels.ToArray(), fixedBar, Width * 16 * 4);
            Assert(stitcher.Append(new(Width, ViewHeight, fixedBar)) == ScrollAppendResult.NoMatch);
            Assert(stitcher.Compose().SequenceEqual(original));
            Assert(stitcher.Append(Frame(data, 140)) == ScrollAppendResult.Added);
        });
        test("scroll rejects ambiguous repeating patterns and accepts unchanged blank", () =>
        {
            var data = Document(Width, 500);
            for (int y = 16; y < 500; y++) Array.Copy(data, y % 16 * Width * 4, data, y * Width * 4, Width * 4);
            var stitcher = new ScrollStitcher(Frame(data, 0));
            Assert(stitcher.Append(Frame(data, 3)) == ScrollAppendResult.NoMatch);
            var blank = new ScrollFrame(Width, ViewHeight, new byte[Width * ViewHeight * 4]);
            Assert(new ScrollStitcher(blank).Append(blank) == ScrollAppendResult.Unchanged);
        });
        test("scroll validates dimensions owns input and cancellation preserves prior pixels", () =>
        {
            var data = Document(Width, 300); var first = Frame(data, 0); var stitcher = new ScrollStitcher(first);
            data[0] ^= 255; Assert(first.Pixels.Span[0] != data[0]);
            foreach (var action in new Action[] {
                () => new ScrollFrame(31, 128, []), () => new ScrollFrame(1000000, 100, []),
                () => stitcher.Append(new ScrollFrame(64, 64, new byte[64 * 64 * 4])) })
            { try { action(); throw new Exception("Expected invalid dimensions"); } catch (ArgumentException) { } }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { stitcher.Append(Frame(data, 50), cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            Assert(stitcher.Height == ViewHeight && stitcher.Compose().SequenceEqual(first.Pixels.ToArray()));
        });
        test("scroll height cap keeps all previously accepted rows", () =>
        {
            var data = Document(32, 30100); var stitcher = new ScrollStitcher(Frame(data, 0, 32, 64));
            int top = 32;
            while (stitcher.Append(Frame(data, top, 32, 64)) == ScrollAppendResult.Added) top += 32;
            Assert(stitcher.Height <= ScrollStitcher.MaxHeight && stitcher.Height + 32 > ScrollStitcher.MaxHeight);
            Assert(stitcher.Append(Frame(data, top, 32, 64)) == ScrollAppendResult.LimitReached);
            Assert(stitcher.Compose().SequenceEqual(data.Take(stitcher.Height * 32 * 4)));
        });
        test("scroll waits for stable content and finish includes the last scroll", () => OcrChecks.Pump(async () =>
        {
            var data = Document(Width, 400); int top = 0, outputs = 0; BitmapSource? output = null;
            var window = new ScrollCaptureWindow(Region, Bitmap(Frame(data, 0)), (image, warning, _) =>
                { outputs++; output = image; Assert(warning.Length == 0); return Task.CompletedTask; }, () => Bitmap(Frame(data, top)));
            try
            {
                top = 50; await window.SampleOnce(); Assert(window.ImageHeight == ViewHeight);
                await window.SampleOnce(); Assert(window.ImageHeight == ViewHeight + 50);
                Assert(window.LivePreview?.PixelHeight == ViewHeight + 50 && window.PreviewMatched);
                top = 100; await window.Finish(); Assert(outputs == 1 && output?.PixelHeight == ViewHeight + 100);
                Assert(ScrollImages.Frame(output!).Pixels.Span.SequenceEqual(data.AsSpan(0, Width * (ViewHeight + 100) * 4)));
            }
            finally { window.Close(); }
        }));
        test("scroll cancel ignores pending frame and never produces an output", () => OcrChecks.Pump(async () =>
        {
            var data = Document(Width, 300); int outputs = 0;
            var window = new ScrollCaptureWindow(Region, Bitmap(Frame(data, 0)), (_, _, _) => { outputs++; return Task.CompletedTask; }, () => Bitmap(Frame(data, 50)));
            var pending = window.SampleOnce(); window.Close(); await pending; await window.Finish();
            Assert(outputs == 0 && window.ImageHeight == 0 && window.LivePreview == null);
        }));
        test("scroll cancel during finish suppresses output and repeated finish", () => OcrChecks.Pump(async () =>
        {
            var image = Bitmap(Frame(Document(Width, 200), 0)); int outputs = 0;
            var window = new ScrollCaptureWindow(Region, image, (_, _, _) => { outputs++; return Task.CompletedTask; }, () => image);
            var pending = window.Finish(); await window.Finish(); window.Close(); await pending;
            Assert(outputs == 0 && window.ImageHeight == 0);
        }));
        test("scroll blocked target and no-match finish retain image with warning", () => OcrChecks.Pump(async () =>
        {
            var data = Document(Width, 500); bool visible = false; int captures = 0;
            BitmapSource? output = null; string? warning = null;
            var window = new ScrollCaptureWindow(Region, Bitmap(Frame(data, 0)), (image, text, _) => { output = image; warning = text; return Task.CompletedTask; },
                () => { captures++; return Bitmap(Frame(data, 240)); }, () => visible);
            try
            {
                await window.SampleOnce(); Assert(captures == 0 && window.ImageHeight == ViewHeight);
                visible = true; await window.SampleOnce(); await window.SampleOnce();
                await window.Finish(); Assert(output?.PixelHeight == ViewHeight && warning?.Length > 0);
            }
            finally { window.Close(); }
        }));
        test("scroll capture failure retains current image and allows retry", () => OcrChecks.Pump(async () =>
        {
            var data = Document(Width, 300); bool fail = true;
            var window = new ScrollCaptureWindow(Region, Bitmap(Frame(data, 0)), (_, _, _) => Task.CompletedTask,
                () => fail ? throw new InvalidOperationException() : Bitmap(Frame(data, 60)));
            try
            {
                await window.SampleOnce(); Assert(window.ImageHeight == ViewHeight && window.Status.Contains("失败"));
                fail = false; await window.SampleOnce(); await window.SampleOnce(); Assert(window.ImageHeight == ViewHeight + 60);
            }
            finally { window.Close(); }
        }));
        test("long screenshot entry validates selection and preserves it on startup failure", () =>
        {
            var data = Document(Width, ViewHeight); int starts = 0, closes = 0;
            var session = new CaptureSession(new(Region, [Region], Bitmap(Frame(data, 0))), () => closes++, (_, _) => { }, (_, _) => Task.FromResult(""),
                showScroll: (_, _) => { starts++; throw new InvalidOperationException("Synthetic startup failure"); });
            session.StartScroll(); Assert(starts == 0);
            session.Selection.Select(new(0, 0, 20, 20)); session.StartScroll(); Assert(starts == 0);
            session.Selection.Select(Region); session.SetTool(AnnotationKind.Rectangle); session.Begin(new(10, 10), 0); session.End(new(70, 90));
            session.StartScroll(); Assert(starts == 0); session.UndoAnnotation(); session.StartScroll();
            Assert(starts == 1 && closes == 0 && session.Selection.Rectangle == Region && !session.Busy); session.Close();
        });
        test("long screenshot toolbar hands off frozen region and closes capture once", () =>
        {
            var data = Document(Width, ViewHeight); int starts = 0, closes = 0;
            var session = new CaptureSession(new(Region, [Region], Bitmap(Frame(data, 0))), () => closes++, (_, _) => { }, (_, _) => Task.FromResult(""),
                showScroll: (region, image) => { starts++; Assert(region == Region && image.PixelHeight == ViewHeight); });
            session.Selection.Select(Region); var window = new OverlayWindow(session, Region);
            try
            {
                RefreshChecks.Descendants<Button>((DependencyObject)window.Content).Single(b => Equals(b.Tag, "长截图"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                session.StartScroll(); Assert(starts == 1 && closes == 1);
            }
            finally { window.Close(); session.Close(); }
        });
        test("long finish waits for copy and ignores duplicate completion", () => OcrChecks.Pump(async () =>
        {
            var data = Document(Width, 200); var image = Bitmap(Frame(data, 0));
            var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); int copies = 0, closes = 0;
            var window = new ScrollCaptureWindow(Region, image, async (source, _, token) =>
            {
                copies++; Assert(ScrollImages.Frame(source).Pixels.Span.SequenceEqual(ScrollImages.Frame(image).Pixels.Span));
                entered.SetResult(); await release.Task.WaitAsync(token);
            }, () => image);
            window.Closed += (_, _) => closes++;
            try
            {
                foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Shift })
                    await ShortcutChecks.Press(window, Key.C, modifiers);
                await ShortcutChecks.Press(window, Key.F2, ModifierKeys.Control); Assert(copies == 0);
                var pending = ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); await entered.Task;
                Assert(window.LivePreview != null && closes == 0);
                await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); Assert(copies == 1);
                release.SetResult(); await pending;
                Assert(closes == 1 && window.LivePreview == null && window.ImageHeight == 0);
            }
            finally { window.Close(); }
        }));
        test("long finish cancel aborts clipboard retry without writing", () => OcrChecks.Pump(async () =>
        {
            var image = Bitmap(Frame(Document(Width, 200), 0)); var entered = new TaskCompletionSource(); int attempts = 0, writes = 0;
            var window = new ScrollCaptureWindow(Region, image, (source, _, token) => ImageOutput.CopyWithRetry(source, token, _ =>
            {
                if (++attempts == 1) { entered.SetResult(); throw new System.Runtime.InteropServices.ExternalException(); }
                writes++;
            }), () => image);
            var pending = ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); await entered.Task;
            await ShortcutChecks.Press(window, Key.Escape, ModifierKeys.None); await pending;
            Assert(attempts == 1 && writes == 0 && window.LivePreview == null);
        }));
        test("long finish failed copy preserves pixels and allows successful retry", () => OcrChecks.Pump(async () =>
        {
            var image = Bitmap(Frame(Document(Width, 200), 0)); int attempts = 0;
            var window = new ScrollCaptureWindow(Region, image, (source, _, _) =>
            {
                Assert(ScrollImages.Frame(source).Pixels.Span.SequenceEqual(ScrollImages.Frame(image).Pixels.Span));
                return ++attempts == 1 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask;
            }, () => image);
            try
            {
                await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); Assert(window.ImageHeight == ViewHeight && window.LivePreview != null && window.Status.Contains("复制失败"));
                await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); Assert(attempts == 2 && window.LivePreview == null && window.ImageHeight == 0);
            }
            finally { window.Close(); }
        }));
    }
    internal static void CheckNativeWindows()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        OcrChecks.Pump(async () =>
        {
            var source = new Image { Source = PageImage(), Width = 600, Height = 2000, Stretch = Stretch.Fill };
            var viewer = new ScrollViewer { Content = source, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var fixture = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Width = 640, Height = 480, Topmost = true, ShowActivated = false, ShowInTaskbar = false, Content = viewer };
            ScrollCaptureWindow? panel = null;
            var blocker = new Window { Width = 100, Height = 100, WindowStyle = WindowStyle.None, Topmost = true,
                ShowActivated = false, ShowInTaskbar = false, Background = Brushes.Red };
            try
            {
                var screen = Native.Monitors()[0]; int x = screen.Left + 100, y = screen.Top + 100;
                fixture.Show(); var handle = new WindowInteropHelper(fixture).Handle;
                Assert(Native.SetWindowPos(handle, new IntPtr(-1), x, y, 640, 480, 0x0010));
                await Task.Delay(200);
                var target = WindowTargets.Capture().Single(t => t.Handle == handle);
                var region = new PixelRect(x + 30, y + 30, x + 350, y + 270);
                Assert(WindowTargets.CanCaptureRegion(target, region, IntPtr.Zero));
                var first = DesktopSnapshot.CaptureRegion(region); BitmapSource? output = null; string? warning = null;
                panel = new ScrollCaptureWindow(region, first, (image, text, _) => { output = image; warning = text; return Task.CompletedTask; },
                    canCapture: () => panel != null && WindowTargets.CanCaptureRegion(target, region, panel.CaptureWindows));
                panel.Start(); var panelHandle = new WindowInteropHelper(panel).Handle;
                // Deliberately cover the test content to verify capture exclusion, without sampling any user window.
                Assert(Native.SetWindowPos(panelHandle, new IntPtr(-1), x + 50, y + 50, 480, 280, 0x0010));
                await Task.Delay(200);
                Assert(WindowTargets.CanCaptureRegion(target, region, panel.CaptureWindows));
                Assert(panel.CaptureWindows.All(h => h != IntPtr.Zero));
                Assert((Native.GetWindowLongPtr(panel.CaptureWindows[1], -20).ToInt64() & 0x08000020) == 0x08000020);
                Assert(ScrollImages.Frame(first).SimilarTo(ScrollImages.Frame(DesktopSnapshot.CaptureRegion(region))));
                for (int offset = 30; offset <= 120; offset += 30)
                {
                    viewer.ScrollToVerticalOffset(offset); fixture.UpdateLayout(); await Task.Delay(80);
                    await panel.SampleOnce(); await panel.SampleOnce();
                }
                Assert(panel.ImageHeight > region.Height);
                blocker.Show(); var blockerHandle = new WindowInteropHelper(blocker).Handle;
                Assert(Native.SetWindowPos(blockerHandle, new IntPtr(-1), x + 30, y + 30, 100, 100, 0x0010)); await Task.Delay(80);
                Assert(!WindowTargets.CanCaptureRegion(target, region, panel.CaptureWindows));
                int height = panel.ImageHeight; await panel.SampleOnce(); Assert(panel.ImageHeight == height);
                blocker.Close(); await Task.Delay(80);
                await panel.Finish();
                Assert(panel.LivePreview == null && !app.Windows.OfType<ScrollRegionWindow>().Any() && !app.Windows.OfType<ScrollPreviewWindow>().Any());
                Assert(output != null && output.PixelHeight == region.Height + (int)Math.Round(120 * VisualTreeHelper.GetDpi(fixture).DpiScaleY) && warning == "");
                fixture.WindowState = WindowState.Minimized; await Task.Delay(80);
                Assert(!WindowTargets.CanCaptureRegion(target, region, IntPtr.Zero));
                Console.WriteLine($"PASS native long screenshot: excluded panel, stable manual scroll, blocked target, minimized target, final height={output!.PixelHeight}");
            }
            finally { panel?.Close(); blocker.Close(); fixture.Close(); }
        });
        app.Shutdown();
    }

    private static BitmapSource PageImage()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 2000));
            for (int row = 0; row < 40; row++)
            {
                var brush = new SolidColorBrush(Color.FromRgb((byte)(30 + row * 3), (byte)(100 + row * 2), (byte)(150 + row)));
                dc.DrawRectangle(brush, null, new Rect(0, row * 50, 600, 3));
                var text = new FormattedText($"{row + 1:D2}  长截图测试 · Scroll capture {row * 137 + 19}",
                    System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 20, Brushes.Black, 1);
                dc.DrawText(text, new Point(15, row * 50 + 12));
            }
        }
        var image = new RenderTargetBitmap(600, 2000, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }

    internal static void RenderPreview()
    {
        var viewport = new CroppedBitmap(PageImage(), new Int32Rect(0, 1380, 600, 620)); viewport.Freeze();
        var panel = new ScrollCaptureWindow(new(30, 30, 630, 650), viewport, (_, _, _) => Task.CompletedTask);
        try
        {
            string dir = Path.Combine(Program.FindRoot(), ".tmp", "scroll-validation"); Directory.CreateDirectory(dir);
            var panelContent = (FrameworkElement)panel.Content;
            panelContent.Measure(new Size(480, 112)); panelContent.Arrange(new Rect(0, 0, 480, 112)); panelContent.UpdateLayout();
            var panelPreview = new RenderTargetBitmap(480, 112, 96, 96, PixelFormats.Pbgra32); panelPreview.Render(panelContent);
            ImageOutput.SavePng(panelPreview, Path.Combine(dir, "panel.png"));
            var scene = new Canvas { Width = 1100, Height = 800, Background = Brushes.White };
            var page = new Image { Source = viewport, Width = 600, Height = 620, Stretch = Stretch.Fill };
            Canvas.SetLeft(page, 30); Canvas.SetTop(page, 30); scene.Children.Add(page);
            scene.Children.Add(new ScrollRegionSurface(new(30, 30, 630, 650), new(0, 0, 1100, 800)) { Width = 1100, Height = 800 });
            var live = new ScrollPreviewSurface(); live.Update(PageImage(), 2000, 620);
            var frame = new Border { Width = 320, Height = 620, Background = Brushes.White, BorderBrush = ScrollCaptureView.Blue,
                BorderThickness = new Thickness(2), Child = live };
            Canvas.SetLeft(frame, 646); Canvas.SetTop(frame, 30); scene.Children.Add(frame);
            var bar = new Image { Source = panelPreview, Width = 480, Height = 112 };
            Canvas.SetLeft(bar, 30); Canvas.SetTop(bar, 660); scene.Children.Add(bar);
            scene.Measure(new Size(1100, 800)); scene.Arrange(new Rect(0, 0, 1100, 800)); scene.UpdateLayout();
            var liveImage = new RenderTargetBitmap(1100, 800, 96, 96, PixelFormats.Pbgra32); liveImage.Render(scene);
            ImageOutput.SavePng(liveImage, Path.Combine(dir, "live-preview.png"));
        }
        finally { panel.Close(); }
    }
}
