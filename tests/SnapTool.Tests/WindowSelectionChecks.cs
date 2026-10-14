using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class WindowSelectionChecks
{
    private static readonly PixelRect Bounds = new(-100, -50, 300, 250);
    private static readonly PixelRect Front = new(0, 0, 120, 100), Back = new(-80, -40, 240, 200);
    private static void Assert(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null)
    { if (!value) throw new Exception("Window selection: " + expression); }
    private static CaptureSession Session(PixelRect[] targets, Action? finished = null) =>
        new(new(Bounds, [new(-100, -50, 100, 250), new(100, -50, 300, 250)], OcrChecks.Sample(true, 400, 300)),
            finished ?? (() => { }), (_, _) => { }, (_, _) => Task.FromResult(""), windowTargets: targets);
    private static void Click(CaptureSession s, PixelPoint point) { s.Begin(point, 6); s.End(point); }

    internal static void Run(Action<string, Action> test)
    {
        test("window hover uses frozen frontmost bounds without committing selection", () =>
        {
            var targets = new[] { Front, Back }; var s = Session(targets); targets[0] = default;
            s.Move(new(20, 20)); Assert(s.WindowPreview == Front && s.Selection.Rectangle.IsEmpty && !s.Dragging);
            Assert(s.HitTest(new(0, 0), 6) == (true, SelectionHit.None));
            s.Move(new(160, 80)); Assert(s.DisplayRectangle == Back);
            s.Move(new(280, 220)); Assert(s.DisplayRectangle.IsEmpty && s.AutoSelecting); s.Close();
        });
        test("window candidates clip offscreen and span negative and multiple monitors", () =>
        {
            var s = Session([default, new(-999, -999, 999, 160)]);
            s.Move(new(-50, 20)); Assert(s.DisplayRectangle == new PixelRect(-100, -50, 300, 160));
            Click(s, new(200, 20)); Assert(s.Selection.Rectangle == new PixelRect(-100, -50, 300, 160)); s.Close();
        });
        test("click with small jitter fixes window and permits resize and annotations", () =>
        {
            var s = Session([Front, Back]); s.Move(new(20, 20)); s.Begin(new(20, 20), 6);
            s.Move(new(23, 22)); Assert(s.Dragging && s.AutoSelecting && s.DisplayRectangle == Front);
            s.End(new(24, 22)); Assert(!s.AutoSelecting && !s.Dragging && s.Selection.Rectangle == Front);
            s.Move(new(180, 150)); Assert(s.DisplayRectangle == Front);
            s.Begin(new(120, 100), 6); s.End(new(160, 140)); Assert(s.Selection.Rectangle == new PixelRect(0, 0, 160, 140));
            s.SetTool(AnnotationKind.Arrow); s.Begin(new(20, 20), 3); s.End(new(100, 90));
            Assert(s.Annotations.Items.Count == 1 && s.Selection.Rectangle == new PixelRect(0, 0, 160, 140)); s.Close();
        });
        test("drag bypasses hovered window and never switches gesture ownership", () =>
        {
            var s = Session([Front, Back]); s.Begin(new(20, 20), 6); s.Move(new(27, 20));
            Assert(!s.AutoSelecting && s.Selection.Dragging && s.Selection.Rectangle == new PixelRect(20, 20, 27, 20));
            s.Move(new(180, 150)); s.Move(new(-50, -20)); s.End(new(-50, -20));
            Assert(s.Selection.Rectangle == new PixelRect(-50, -20, 20, 20)); s.Close();
        });
        test("release without move event still distinguishes click from manual drag", () =>
        {
            var s = Session([Front]); s.Begin(new(20, 20), 6); s.End(new(60, 50));
            Assert(!s.AutoSelecting && s.Selection.Rectangle == new PixelRect(20, 20, 60, 50)); s.Close();
        });
        test("unrecognized area remains empty after click and supports manual selection", () =>
        {
            foreach (var targets in new[] { Array.Empty<PixelRect>(), new[] { Front } })
            {
                var s = Session(targets); Click(s, new(200, 180)); Assert(s.AutoSelecting && s.Selection.Rectangle.IsEmpty && !s.Dragging);
                s.Begin(new(200, 180), 6); s.End(new(250, 220));
                Assert(s.Selection.Rectangle == new PixelRect(200, 180, 250, 220)); s.Close();
            }
        });
        test("preview and pending click cannot output then confirmed window uses exact crop", () => OcrChecks.Pump(async () =>
        {
            foreach (bool copy in new[] { false, true })
            {
                int outputs = 0, ocr = 0, closed = 0;
                void Check(BitmapSource image) { Assert(image.PixelWidth == 120 && image.PixelHeight == 100); outputs++; }
                var s = new CaptureSession(new(Bounds, [Bounds], OcrChecks.Sample(true, 400, 300)), () => closed++, (_, _) => { },
                    (image, _) => { Assert(image.PixelWidth == 120 && image.PixelHeight == 100); ocr++; return Task.FromResult(""); },
                    showPin: (image, rect) => { Assert(rect == Front); Check(image); },
                    copyImage: (image, _) => { Check(image); return Task.CompletedTask; }, windowTargets: [Front]);
                var window = new OverlayWindow(s, Bounds);
                try
                {
                    s.Move(new(20, 20)); await s.Copy(window); s.Pin(); await s.RecognizeText();
                    s.Begin(new(20, 20), 6); await s.Copy(window); s.Pin(); await s.RecognizeText();
                    Assert(outputs == 0 && ocr == 0 && closed == 0);
                    s.End(new(20, 20)); await s.RecognizeText(); Assert(ocr == 1);
                    if (copy) await s.Copy(window); else s.Pin(); Assert(outputs == 1 && closed == 1);
                }
                finally { window.Close(); s.Close(); }
            }
        }));
        test("cancel pending window selection clears gesture and rejects late mouse input", () =>
        {
            int closed = 0; var s = Session([Front], () => closed++); s.Begin(new(20, 20), 6); s.Close();
            s.Move(new(90, 80)); s.End(new(90, 80)); s.Close();
            Assert(!s.Dragging && s.Selection.Rectangle.IsEmpty && closed == 1);
        });
    }

    internal static void CheckNativeWindows()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var back = new Window { Width = 220, Height = 160, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Background = Brushes.Navy };
        var front = new Window { Width = 150, Height = 100, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Background = Brushes.Teal };
        OcrChecks.Pump(async () =>
        {
            try
            {
                var screen = Native.Monitors()[0]; int x = screen.Left + 80, y = screen.Top + 100;
                back.Show(); front.Show();
                var backHandle = new WindowInteropHelper(back).Handle; var frontHandle = new WindowInteropHelper(front).Handle;
                Assert(Native.SetWindowPos(backHandle, new IntPtr(-1), x, y, 220, 160, 0x0010));
                Assert(Native.SetWindowPos(frontHandle, new IntPtr(-1), x + 30, y + 30, 150, 100, 0x0010));
                await Task.Delay(150);
                var targets = WindowTargets.Capture().ToArray();
                var a = targets.Single(t => t.Handle == frontHandle); var b = targets.Single(t => t.Handle == backHandle);
                Assert(a.Bounds == new PixelRect(x + 30, y + 30, x + 180, y + 130));
                Assert(b.Bounds == new PixelRect(x, y, x + 220, y + 160));
                Assert(Array.IndexOf(targets, a) < Array.IndexOf(targets, b));
                front.Hide(); await Task.Delay(50);
                Assert(!WindowTargets.Capture().Any(t => t.Handle == frontHandle));
                back.WindowState = WindowState.Minimized; await Task.Delay(50);
                Assert(!WindowTargets.Capture().Any(t => t.Handle == backHandle));
                Assert(targets.Contains(a) && targets.Contains(b));
                Console.WriteLine("PASS native window targets: physical frame bounds, overlap order, hidden/minimized filtering, frozen candidates");
            }
            finally { front.Close(); back.Close(); }
        });
        app.Shutdown();
    }

    internal static void RenderPreview()
    {
        var bounds = new PixelRect(0, 0, 1100, 650);
        var bitmap = new DrawingVisual();
        using (var dc = bitmap.RenderOpen())
        {
            dc.DrawRectangle(Brushes.SlateGray, null, new Rect(0, 0, 1100, 650));
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(60, 130, 630, 440));
            dc.DrawRectangle(Brushes.White, null, new Rect(390, 200, 600, 340));
            dc.DrawRectangle(Brushes.Teal, null, new Rect(390, 200, 600, 36));
        }
        var image = new RenderTargetBitmap(1100, 650, 96, 96, PixelFormats.Pbgra32); image.Render(bitmap); image.Freeze();
        var s = new CaptureSession(new(bounds, [bounds], image), () => { }, (_, _) => { }, (_, _) => Task.FromResult(""),
            windowTargets: [new(390, 200, 990, 540), new(60, 130, 690, 570)]);
        s.Move(new(500, 300)); var window = new OverlayWindow(s, bounds);
        try
        {
            var content = (FrameworkElement)window.Content; content.Measure(new Size(1100, 650)); content.Arrange(new Rect(0, 0, 1100, 650));
            var preview = new RenderTargetBitmap(1100, 650, 96, 96, PixelFormats.Pbgra32); preview.Render(content);
            string dir = Path.Combine(Program.FindRoot(), ".tmp", "window-selection"); Directory.CreateDirectory(dir);
            ImageOutput.SavePng(preview, Path.Combine(dir, "preview.png"));
        }
        finally { window.Close(); s.Close(); }
    }
}
