using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class LineAnnotationChecks
{
    private static readonly PixelRect Bounds = new(-100, -50, 300, 250);
    private static void Assert(bool value) { if (!value) throw new Exception("Line/arrow assertion failed"); }
    private static Annotations Mark(AnnotationKind kind, PixelPoint start, PixelPoint end, bool shift = false)
    {
        var m = new Annotations(); m.Begin(start, Bounds, 2, kind); m.End(end, shift); return m;
    }
    private static DesktopSnapshot Snapshot() => new(Bounds, [Bounds], OcrChecks.Sample(true, 400, 300));
    private static CaptureSession Session(Action<BitmapSource>? inspect = null, Action<BitmapSource, PixelRect>? pin = null)
    {
        var s = new CaptureSession(Snapshot(), () => { }, (_, _) => { },
            (image, _) => { inspect?.Invoke(image); return Task.FromResult(""); }, showPin: pin);
        s.Selection.Begin(new(-100, -50), 0); s.Selection.End(new(300, 250)); return s;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    private static uint Pixel(BitmapSource image, int x, int y)
    {
        var pixels = Pixels(image); int i = (y * image.PixelWidth + x) * 4;
        return (uint)(pixels[i + 2] << 16 | pixels[i + 1] << 8 | pixels[i]);
    }
    internal static void Run(Action<string, Action> test)
    {
        test("line and arrow retain horizontal vertical diagonal and reversed endpoints", () =>
        {
            foreach (var kind in new[] { AnnotationKind.Line, AnnotationKind.Arrow })
                foreach (var end in new[] { new PixelPoint(100, 0), new(0, 100), new(-80, -40), new(50, -30) })
                {
                    var mark = Mark(kind, new(0, 0), end).Items.Single();
                    Assert(mark.Kind == kind && mark.Start == new PixelPoint(0, 0) && mark.End == end);
                }
        });
        test("line zero length and sub-two-pixel gestures do not create history", () =>
        {
            foreach (var kind in new[] { AnnotationKind.Line, AnnotationKind.Arrow })
                foreach (var end in new[] { new PixelPoint(0, 0), new(1, 0), new(1, 1) })
                { var m = Mark(kind, new(0, 0), end); Assert(m.Items.Count == 0 && !m.CanUndo); }
            Assert(Mark(AnnotationKind.Line, new(0, 0), new(2, 0)).Items.Count == 1);
        });
        test("line outside and empty regions ignored and overshoot clipped", () =>
        {
            var m = new Annotations(); m.Begin(new(301, 0), Bounds, 2, AnnotationKind.Line); m.End(new(0, 0));
            m.Begin(new(0, 0), default, 2, AnnotationKind.Arrow); m.End(new(10, 10)); Assert(m.Items.Count == 0);
            m.Begin(new(0, 0), Bounds, 2, AnnotationKind.Arrow); m.End(new(-999, 999));
            Assert(m.Items.Single().End == new PixelPoint(-100, 250));
        });
        test("shift snaps line and arrow to all eight directions and stays in bounds", () =>
        {
            foreach (var kind in new[] { AnnotationKind.Line, AnnotationKind.Arrow })
                foreach (var (p, expected) in new[] {
                    (new PixelPoint(90, 10), new PixelPoint(90, 0)), (new PixelPoint(-90, 10), new PixelPoint(-90, 0)),
                    (new PixelPoint(10, 90), new PixelPoint(0, 90)), (new PixelPoint(10, -40), new PixelPoint(0, -40)),
                    (new PixelPoint(50, 40), new PixelPoint(45, 45)), (new PixelPoint(-50, 40), new PixelPoint(-45, 45)),
                    (new PixelPoint(50, -40), new PixelPoint(45, -45)), (new PixelPoint(-50, -40), new PixelPoint(-45, -45)) })
                    Assert(Mark(kind, new(0, 0), p, true).Items.Single().End == expected);
            Assert(Mark(AnnotationKind.Arrow, new(280, 220), new(999, 999), true).Items.Single().End == new PixelPoint(300, 240));
        });
        test("line endpoints edit independently and arrow direction survives crossing", () =>
        {
            var m = Mark(AnnotationKind.Arrow, new(0, 0), new(100, 0));
            Assert(Annotations.Handles(m.Selected!).Count() == 2);
            m.Begin(new(0, 0), Bounds, 2); m.End(new(150, 30));
            Assert(m.Selected!.Start == new PixelPoint(150, 30) && m.Selected.End == new PixelPoint(100, 0));
            m.Begin(new(100, 0), Bounds, 2); m.End(new(220, 40), true);
            Assert(m.Selected.Start == new PixelPoint(150, 30) && m.Selected.End == new PixelPoint(220, 30));
        });
        test("line collapsing endpoint restores previous geometry without extra undo", () =>
        {
            var m = Mark(AnnotationKind.Line, new(0, 0), new(100, 0)); var saved = m.Items.Single();
            m.Begin(new(100, 0), Bounds, 2); m.End(new(0, 0)); Assert(m.Items.Single() == saved);
            m.Undo(); Assert(m.Items.Count == 0);
        });
        test("line body move clamps both endpoints and preserves arrow direction", () =>
        {
            var m = Mark(AnnotationKind.Arrow, new(100, 0), new(0, 0));
            m.Begin(new(50, 0), Bounds, 2); m.End(new(-999, -999));
            Assert(m.Selected!.Start == new PixelPoint(0, -50) && m.Selected.End == new PixelPoint(-100, -50));
            m.Undo(); Assert(m.Items.Single().Start == new PixelPoint(100, 0));
        });
        test("diagonal hit testing excludes bounding box blank and includes arrow head", () =>
        {
            var m = Mark(AnnotationKind.Line, new(0, 0), new(100, 100)); m.Deselect();
            Assert(m.HitTest(new(50, 50), 2).Index == 0 && m.HitTest(new(0, 100), 2).Index == -1);
            m = Mark(AnnotationKind.Arrow, new(0, 0), new(100, 0)); m.SetStyle(0, 8); m.Deselect();
            Assert(m.HitTest(new(77, 11), 1).Index == 0 && m.HitTest(new(50, 20), 1).Index == -1);
        });
        test("mixed shapes choose latest overlap and share chronological undo", () =>
        {
            var m = new Annotations(); m.Begin(new(0, 0), Bounds, 1); m.End(new(100, 100)); m.Deselect();
            m.Begin(new(-50, 50), Bounds, 1, AnnotationKind.Line); m.End(new(150, 50)); m.Deselect();
            m.Begin(new(50, -30), Bounds, 1, AnnotationKind.Arrow); m.End(new(50, 150)); m.Deselect();
            Assert(m.HitTest(new(50, 50), 1).Index == 2);
            m.Begin(new(50, 50), Bounds, 1, AnnotationKind.Rectangle); m.End(new(50, 50));
            m.SetStyle(0x409CFF, 5); m.Delete(); Assert(m.Items.Count == 2);
            m.Undo(); Assert(m.Items[2].Rgb == 0x409CFF); m.Undo(); Assert(m.Items[2].Rgb == 0xFF4545);
            m.Undo(); Assert(m.Items.Count == 2); m.Undo(); Assert(m.Items.Single().Kind == AnnotationKind.Rectangle);
            m.Undo(); Assert(m.Items.Count == 0);
        });
        test("unfinished line undo restores shapes and rejects duplicate begin", () =>
        {
            var m = Mark(AnnotationKind.Arrow, new(0, 0), new(100, 0)); var saved = m.Items.Single();
            m.Begin(new(0, 100), Bounds, 2, AnnotationKind.Line); m.Update(new(100, 100));
            m.Begin(new(200, 200), Bounds, 2); m.Undo(); m.End(new(200, 200));
            Assert(!m.Dragging && m.Items.Single() == saved);
        });
        test("short and reversed arrow heads have finite proportional geometry", () =>
        {
            foreach (int width in new[] { 2, 3, 5, 8 })
                foreach (var end in new[] { new PixelPoint(2, 0), new(-2, 0), new(0, 2), new(0, -2), new(100, 100) })
                {
                    var mark = new Annotation(AnnotationKind.Arrow, new(0, 0), end, 0, width); var head = mark.ArrowHead();
                    double centerX = (head.Left.X + head.Right.X) / 2, centerY = (head.Left.Y + head.Right.Y) / 2;
                    double projection = (centerX * end.X + centerY * end.Y) / (mark.Length * mark.Length);
                    Assert(double.IsFinite(head.Left.X) && projection >= 0.5 && projection < 1);
                    Assert(AnnotationRenderer.Compose(Snapshot(), Bounds, [mark]).IsFrozen);
                }
        });
        test("line and arrow export preserves original pixels and draw order", () =>
        {
            var snapshot = Snapshot(); var before = Pixels(snapshot.Image);
            Annotation[] marks = [new(AnnotationKind.Line, new(-50, 0), new(100, 0), 0xFF4545, 3),
                new(AnnotationKind.Arrow, new(0, -30), new(0, 150), 0x409CFF, 5)];
            var output = AnnotationRenderer.Compose(snapshot, Bounds, marks);
            Assert(Pixel(output, 100, 50) == 0x409CFF && Pixel(output, 80, 50) == 0xFF4545);
            Assert(Pixel(output, 102, 190) == 0x409CFF && Pixel(output, 110, 190) == 0xFFFFFF);
            Assert(before.SequenceEqual(Pixels(snapshot.Image)));
        });
        test("cropping arrow head does not create a new head on crop boundary", () =>
        {
            var marks = Mark(AnnotationKind.Arrow, new(-50, 30), new(150, 30)).Items;
            var output = AnnotationRenderer.Compose(Snapshot(), new(0, 0, 100, 100), marks);
            Assert(Pixel(output, 98, 30) == 0xFF4545 && Pixel(output, 98, 26) == 0xFFFFFF);
            Assert(marks.Single().End == new PixelPoint(150, 30));
        });
        test("line and arrow preview matches export at three display scales", () =>
        {
            Annotation[] marks = [new(AnnotationKind.Line, new(-60, 0), new(200, 0), 0x38D996, 3),
                new(AnnotationKind.Arrow, new(150, 150), new(20, 20), 0x409CFF, 5)];
            var expected = Pixels(AnnotationRenderer.Compose(Snapshot(), Bounds, marks));
            foreach (double scale in new[] { 1.0, 0.8, 2.0 / 3 })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(scale, scale)); dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 400, 300));
                    dc.PushTransform(new TranslateTransform(100, 50)); AnnotationRenderer.Draw(dc, marks); dc.Pop(); dc.Pop();
                }
                var preview = new RenderTargetBitmap(400, 300, 96 / scale, 96 / scale, PixelFormats.Pbgra32); preview.Render(visual);
                var actual = Pixels(preview); Assert(actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max() <= 2);
            }
        });
        test("line arrow PNG roundtrip retains full rendered pixels", () =>
        {
            var output = AnnotationRenderer.Compose(Snapshot(), Bounds, Mark(AnnotationKind.Arrow, new(100, 100), new(-50, 0)).Items);
            using var stream = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output)); encoder.Save(stream);
            stream.Position = 0; var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert(Pixels(output).SequenceEqual(Pixels(decoder.Frames[0])));
        });
        test("line gesture blocks mode changes OCR and pin and OCR later reads original", () => OcrChecks.Pump(async () =>
        {
            int calls = 0, pins = 0; var s = Session(image => { calls++; Assert(Pixels(image).All(p => p == 255)); }, (_, _) => pins++);
            s.SetTool(AnnotationKind.Line); s.Begin(new(0, 0), 2); s.Move(new(100, 0));
            s.SetTool(AnnotationKind.Arrow); await s.RecognizeText(); s.Pin();
            Assert(s.Tool == AnnotationKind.Line && calls == 0 && pins == 0 && s.Selection.Rectangle == Bounds);
            s.End(new(100, 0)); await s.RecognizeText(); Assert(calls == 1); s.Close();
        }));
        test("line arrow pin uses annotated output with original resolution", () =>
        {
            BitmapSource? pinned = null; var s = Session(pin: (image, _) => pinned = image);
            s.SetTool(AnnotationKind.Line); s.Begin(new(-50, 0), 2); s.End(new(150, 0));
            s.SetTool(AnnotationKind.Arrow); s.Begin(new(100, 100), 2); s.End(new(0, 100));
            var expected = Pixels(s.CreateOutput()); s.Pin();
            Assert(pinned != null && pinned.PixelWidth == 400 && Pixels(pinned).SequenceEqual(expected));
        });
        test("crop changes retain line geometry and can restore hidden arrow", () =>
        {
            var s = Session(); s.SetTool(AnnotationKind.Arrow); s.Begin(new(0, 0), 2); s.End(new(200, 100));
            var mark = s.Annotations.Items.Single(); var expected = Pixels(s.CreateOutput()); s.SetTool(null);
            s.Begin(new(300, 250), 2); s.End(new(60, 60)); Assert(s.Annotations.Items.Single() == mark);
            s.Begin(new(60, 60), 2); s.End(new(300, 250)); Assert(Pixels(s.CreateOutput()).SequenceEqual(expected)); s.Close();
        });
        test("shift endpoint after crop stays inside region with remote fixed endpoint", () =>
        {
            var m = Mark(AnnotationKind.Arrow, new(-80, -40), new(50, 20));
            var crop = new PixelRect(0, 0, 100, 100);
            m.Begin(new(50, 20), crop, 2); m.End(new(90, 0), true);
            Assert(crop.Clamp(m.Selected!.End) == m.Selected.End && m.Selected.Start == new PixelPoint(-80, -40));
        });
        test("short thick arrow does not render a shaft wider than its head", () =>
        {
            var mark = new Annotation(AnnotationKind.Arrow, new(0, 0), new(4, 0), 0, 8);
            var output = AnnotationRenderer.Compose(Snapshot(), Bounds, [mark]);
            Assert(Pixel(output, 100, 47) == 0xFFFFFF && Pixel(output, 100, 50) != 0xFFFFFF);
        });
        test("line arrow toolbar and keyboard switch modes and share deletion", () =>
        {
            var s = Session(); var window = new OverlayWindow(s, Bounds);
            try
            {
                var source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
                var toolbar = (Border)((Canvas)((Grid)window.Content).Children[1]).Children[0];
                var tools = (StackPanel)((StackPanel)toolbar.Child).Children[1];
                Button Find(string text) => tools.Children.OfType<Button>().Single(b => Equals(b.Content, text));
                Find("直线").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(s.Tool == AnnotationKind.Line);
                s.Begin(new(0, 0), 2); s.End(new(100, 0));
                Find("箭头").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(s.Tool == AnnotationKind.Arrow);
                s.Begin(new(0, 100), 2); s.End(new(100, 100));
                Find("删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(s.Annotations.Items.Count == 1);
                Find("撤销").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(s.Annotations.Items.Count == 2);
                foreach (var (key, kind) in new[] { (Key.R, (AnnotationKind?)AnnotationKind.Rectangle), (Key.L, AnnotationKind.Line), (Key.A, AnnotationKind.Arrow), (Key.V, null) })
                {
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Assert(s.Tool == kind);
                }
            }
            finally { window.Close(); s.Close(); }
        });
    }
}
