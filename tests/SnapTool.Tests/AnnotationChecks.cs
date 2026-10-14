using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class AnnotationChecks
{
    private static readonly PixelRect Bounds = new(-100, -50, 300, 250);
    private static Annotations Marked()
    {
        var m = new Annotations();
        m.Begin(new(0, 0), Bounds, 2); m.End(new(100, 100)); return m;
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Annotation assertion failed"); }
    internal static void Run(Action<string, Action> test)
    {
        test("rectangle reverse drag clamps at negative desktop boundary", () =>
        {
            var m = new Annotations(); m.Begin(new(80, 80), Bounds, 2); m.End(new(-200, -100));
            Assert(m.Items.Single().Rectangle == new PixelRect(-100, -50, 80, 80));
        });
        test("rectangle ignores outside and empty region", () =>
        {
            var m = new Annotations(); m.Begin(new(300, 0), Bounds, 2); m.End(new(50, 50));
            m.Begin(new(0, 0), default, 2); Assert(m.Items.Count == 0 && !m.Dragging && !m.CanUndo);
        });
        test("rectangle click and one-pixel gestures create no history", () =>
        {
            var m = new Annotations(); m.Begin(new(0, 0), Bounds, 2); m.End(new(0, 0));
            m.Begin(new(0, 0), Bounds, 2); m.End(new(1, 10)); Assert(m.Items.Count == 0 && !m.CanUndo);
        });
        test("rectangle shift constrains new shape in all quadrants", () =>
        {
            foreach (var p in new[] { new PixelPoint(90, 30), new(-90, 30), new(90, -30), new(-90, -30) })
            {
                var m = new Annotations(); m.Begin(new(0, 0), Bounds, 2); m.End(p, true);
                Assert(m.Items.Single().Rectangle.Width == 30 && m.Items.Single().Rectangle.Height == 30);
            }
        });
        test("rectangle nested shapes and topmost border hit", () =>
        {
            var m = Marked(); m.Begin(new(20, 20), Bounds, 2); m.End(new(80, 80));
            Assert(m.Items.Count == 2 && m.HitTest(new(20, 40), 2).Index == 1);
            m.Deselect(); m.Begin(new(0, 20), Bounds, 2); m.End(new(0, 20));
            Assert(m.SelectedIndex == 0 && m.Items.Count == 2);
        });
        test("rectangle border movement preserves size and clamps", () =>
        {
            var m = Marked(); m.Begin(new(0, 25), Bounds, 2); m.End(new(-500, -500));
            Assert(m.Items.Single().Rectangle == new PixelRect(-100, -50, 0, 50));
            m.Undo(); Assert(m.Items.Single().Rectangle == new PixelRect(0, 0, 100, 100));
        });
        test("rectangle handles resize across opposite corner", () =>
        {
            var m = Marked(); m.Begin(new(0, 0), Bounds, 2); m.End(new(150, 130));
            Assert(m.Items.Single().Rectangle == new PixelRect(100, 100, 150, 130));
        });
        test("rectangle edge handle changes one axis", () =>
        {
            var m = Marked(); m.Begin(new(50, 0), Bounds, 2); m.End(new(80, -40));
            Assert(m.Items.Single().Rectangle == new PixelRect(0, -40, 100, 100));
        });
        test("rectangle collapsed resize restores prior shape", () =>
        {
            var m = Marked(); m.Begin(new(0, 0), Bounds, 2); m.End(new(100, 100));
            Assert(m.Items.Single().Rectangle == new PixelRect(0, 0, 100, 100));
            m.Undo(); Assert(m.Items.Count == 0);
        });
        test("rectangle style delete and undo retain independent shapes", () =>
        {
            var m = Marked(); m.SetStyle(0x409CFF, 5); m.Deselect();
            m.Begin(new(130, 0), Bounds, 2); m.End(new(220, 80)); m.SetStyle(0xFFFFFF, 8);
            Assert(m.Items[0].Rgb == 0x409CFF && m.Items[1].Thickness == 8);
            m.Delete(); Assert(m.Items.Count == 1); m.Undo(); Assert(m.Items.Count == 2);
            m.Undo(); Assert(m.Items[1].Rgb == 0x409CFF && m.Items[1].Thickness == 5);
        });
        test("rectangle unfinished drag undo and duplicate begin", () =>
        {
            var m = Marked(); m.Begin(new(150, 150), Bounds, 2); m.Update(new(200, 200));
            m.Begin(new(0, 0), Bounds, 2); m.Undo(); Assert(m.Items.Count == 1 && !m.Dragging);
            m.Update(new(250, 240)); Assert(m.Items.Single().Rectangle == new PixelRect(0, 0, 100, 100));
        });
        test("rectangle invalid style rejected", () =>
        {
            var m = Marked(); int rejected = 0;
            foreach (var (color, width) in new[] { (0x1000000u, 3), (0u, 0), (0u, 13) })
                try { m.SetStyle(color, width); } catch (ArgumentOutOfRangeException) { rejected++; }
            Assert(rejected == 3 && m.Items.Single().Thickness == 3);
        });
        test("annotation export uses negative origin and retains original pixels", () =>
        {
            var snapshot = Snapshot(); var output = AnnotationRenderer.Compose(snapshot, Bounds, Marked().Items);
            Assert(output.PixelWidth == 400 && output.PixelHeight == 300 && output.IsFrozen);
            Assert(Pixel(output, 100, 50) == 0xFF4545 && Pixel(output, 104, 54) == 0xFFFFFF);
            Assert(Pixel(snapshot.Image, 100, 50) == 0xFFFFFF);
        });
        test("annotation crop clips original edges without inventing new border", () =>
        {
            var output = AnnotationRenderer.Compose(Snapshot(), new(20, 20, 120, 120), Marked().Items);
            Assert(Pixel(output, 0, 0) == 0xFFFFFF && Pixel(output, 79, 30) == 0xFF4545);
            Assert(Pixel(output, 80, 30) == 0xFFFFFF && Pixel(output, 30, 79) == 0xFF4545);
        });
        test("annotation minimum size and flush crop borders stay visible", () =>
        {
            var output = AnnotationRenderer.Compose(Snapshot(), new(0, 0, 2, 2), [new(new(0, 0, 2, 2), 0x409CFF, 8)]);
            Assert(Pixel(output, 0, 0) == 0x409CFF && Pixel(output, 1, 1) == 0x409CFF);
        });
        test("annotation preview transform matches export across monitor scales", () =>
        {
            var marks = Marked().Items; var output = AnnotationRenderer.Compose(Snapshot(), Bounds, marks);
            foreach (double scale in new[] { 1.0, 0.8, 2.0 / 3.0 })
            {
                var v = new DrawingVisual();
                using (var dc = v.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 400, 300));
                    dc.PushTransform(new TranslateTransform(100, 50)); AnnotationRenderer.Draw(dc, marks);
                    dc.Pop(); dc.Pop();
                }
                var preview = new RenderTargetBitmap(400, 300, 96 / scale, 96 / scale, PixelFormats.Pbgra32);
                preview.Render(v);
                Assert(Pixel(preview, 100, 50) == Pixel(output, 100, 50) && Pixel(preview, 104, 54) == 0xFFFFFF);
            }
        });
        test("annotation PNG export roundtrip retains colored border", () =>
        {
            string dir = Path.Combine(Program.FindRoot(), ".tmp", "annotation-checks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "marked.png"); ImageOutput.SavePng(AnnotationRenderer.Compose(Snapshot(), Bounds, Marked().Items), path);
                using var file = File.OpenRead(path);
                var decoded = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Assert(Pixel(decoded.Frames[0], 100, 50) == 0xFF4545);
            }
            finally { Directory.Delete(dir, true); }
        });
        test("rectangle tool freezes capture region and blocks OCR during gesture", () => OcrChecks.Pump(async () =>
        {
            int calls = 0;
            var s = Session(image => { calls++; Assert(Pixel(image, 100, 50) == 0xFFFFFF); });
            s.SetTool(AnnotationKind.Rectangle); s.Begin(new(0, 0), 2); s.Move(new(100, 100));
            s.SetTool(null); await s.RecognizeText();
            Assert(s.Annotating && calls == 0 && s.Selection.Rectangle == Bounds);
            s.End(new(100, 100)); Assert(Pixel(s.CreateOutput(), 100, 50) == 0xFF4545);
            await s.RecognizeText(); Assert(calls == 1); s.Close();
        }));
        test("selection adjustment preserves annotation desktop position", () =>
        {
            var s = Session(); s.SetTool(AnnotationKind.Rectangle); s.Begin(new(0, 0), 2); s.End(new(100, 100));
            s.SetTool(null); s.Begin(new(300, 250), 2); s.End(new(60, 60));
            Assert(s.Selection.Rectangle == new PixelRect(-100, -50, 60, 60));
            Assert(s.Annotations.Items.Single().Rectangle == new PixelRect(0, 0, 100, 100));
            Assert(Pixel(s.CreateOutput(), 100, 50) == 0xFF4545); s.Close();
        });
        test("all annotation tools allow direct capture edge and corner resize", () =>
        {
            foreach (var kind in new[] { AnnotationKind.Rectangle, AnnotationKind.Arrow })
                foreach (var p in new[] { new PixelPoint(-100, -50), new(100, -50), new(300, -50), new(-100, 100),
                    new(300, 100), new(-100, 250), new(100, 250), new(300, 250) })
                {
                    var s = Session(); s.SetTool(kind); Assert(s.HitTest(p, 2).Selection);
                    s.Begin(p, 2); Assert(s.Selection.Dragging && !s.Annotations.Dragging);
                    s.End(new(p.X + (p.X == 300 ? -20 : 20), p.Y + (p.Y == 250 ? -20 : 20)));
                    Assert(s.Selection.Rectangle != Bounds && s.Tool == kind && s.Annotations.Items.Count == 0); s.Close();
                }
        });
        test("capture border takes priority over coincident annotation handles", () =>
        {
            var s = Session(); s.SetTool(AnnotationKind.Rectangle);
            s.Annotations.Begin(new(-100, -50), Bounds, 0); s.Annotations.End(new(100, 100));
            var mark = s.Annotations.Items.Single();
            Assert(s.HitTest(new(-100, -50), 2) == (true, SelectionHit.Left | SelectionHit.Top));
            s.Begin(new(-100, -50), 2); s.End(new(-80, -30));
            Assert(s.Selection.Rectangle == new PixelRect(-80, -30, 300, 250) && s.Annotations.Items.Single() == mark); s.Close();
        });
        test("annotation gesture retains ownership when cursor crosses capture border", () =>
        {
            var s = Session(); s.SetTool(AnnotationKind.Arrow); s.Begin(new(0, 0), 2);
            s.Move(new(300, 250)); s.Begin(new(300, 250), 2); s.End(new(500, 500));
            Assert(s.Selection.Rectangle == Bounds && !s.Dragging && s.Annotations.Items.Single().End == new PixelPoint(300, 250)); s.Close();
        });
        test("interior annotation movement and handle editing retain capture bounds", () =>
        {
            foreach (var kind in new[] { AnnotationKind.Rectangle, AnnotationKind.Arrow })
            {
                var s = Session(); s.SetTool(kind); s.Begin(new(0, 0), 2); s.End(new(100, 100));
                var body = kind == AnnotationKind.Rectangle ? new PixelPoint(0, 40) : new PixelPoint(40, 40);
                Assert(s.HitTest(body, 2) == (false, SelectionHit.Move));
                s.Begin(body, 2); s.End(new(body.X + 20, body.Y + 10));
                Assert(s.Annotations.Selected!.Start == new PixelPoint(20, 10));
                s.Begin(new(20, 10), 2); s.End(new(30, 20));
                Assert(s.Annotations.Selected!.Start == new PixelPoint(30, 20) && s.Selection.Rectangle == Bounds); s.Close();
            }
        });
        test("resize gesture blocks tool changes and preserves existing annotations", () =>
        {
            var s = Session(); s.SetTool(AnnotationKind.Arrow); s.Begin(new(0, 0), 2); s.End(new(100, 0));
            var mark = s.Annotations.Items.Single(); s.Begin(new(300, 250), 2);
            s.ToggleTool(AnnotationKind.Arrow); s.SetTool(AnnotationKind.Rectangle); s.SetAnnotationStyle(0, 8);
            s.Move(new(50, 50)); s.End(new(60, 60));
            Assert(s.Tool == AnnotationKind.Arrow && s.Annotations.Items.Single() == mark && s.Selection.Rectangle == new PixelRect(-100, -50, 60, 60)); s.Close();
        });
        test("toggle each drawing tool restores interior capture movement", () =>
        {
            foreach (var kind in new[] { AnnotationKind.Rectangle, AnnotationKind.Arrow })
            {
                var s = Session(); s.Begin(new(300, 250), 2); s.End(new(200, 150));
                s.ToggleTool(kind); Assert(s.Tool == kind); s.ToggleTool(kind); Assert(!s.Annotating);
                s.Begin(new(0, 0), 2); s.End(new(20, 10));
                Assert(s.Selection.Rectangle == new PixelRect(-80, -40, 220, 160) && s.Annotations.Items.Count == 0); s.Close();
            }
        });
        test("collapsed capture can be reselected while annotation tool stays active", () =>
        {
            var s = Session(); s.SetTool(AnnotationKind.Arrow); s.Begin(new(-100, 100), 2); s.End(new(300, 100));
            Assert(s.Selection.Rectangle.IsEmpty && s.Tool == AnnotationKind.Arrow);
            s.Begin(new(0, 0), 2); s.End(new(60, 60));
            Assert(s.Selection.Rectangle == new PixelRect(0, 0, 60, 60) && s.Annotations.Items.Count == 0); s.Close();
        });
        test("direct resize controls OCR and pin crop and prevents output during drag", () => OcrChecks.Pump(async () =>
        {
            int ocr = 0, pins = 0; PixelRect pinned = default;
            var s = new CaptureSession(Snapshot(), () => { }, (_, _) => { }, (image, _) =>
                { ocr++; Assert(image.PixelWidth == 160 && image.PixelHeight == 110); return Task.FromResult(""); },
                showPin: (image, rectangle) => { pins++; pinned = rectangle; Assert(image.PixelWidth == 160 && image.PixelHeight == 110); });
            s.Selection.Begin(new(-100, -50), 0); s.Selection.End(new(300, 250)); s.SetTool(AnnotationKind.Arrow);
            s.Begin(new(300, 250), 2); s.Move(new(60, 60)); await s.RecognizeText(); s.Pin(); Assert(ocr == 0 && pins == 0);
            s.End(new(60, 60)); Assert(s.CreateOutput().PixelWidth == 160);
            await s.RecognizeText(); s.Pin(); Assert(ocr == 1 && pins == 1 && pinned == new PixelRect(-100, -50, 60, 60));
        }));
        test("annotation mode buttons style undo and deletion use session state", () =>
        {
            var s = Session(); var window = new OverlayWindow(s, Bounds);
            try
            {
                var toolbar = Toolbar(window);
                Button Find(string label) => Descendants(toolbar).OfType<Button>().Single(b => Equals(b.Content, label));
                Find("矩形").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(s.Annotating);
                s.Begin(new(0, 0), 2); s.End(new(100, 100));
                var combos = Descendants(toolbar).OfType<ComboBox>().ToArray(); combos[0].SelectedIndex = 3;
                Assert(s.Annotations.Items.Single().Rgb == 0x409CFF);
                Find("删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(s.Annotations.Items.Count == 0);
                Find("撤销").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(s.Annotations.Items.Count == 1);
                Assert(!Descendants(toolbar).OfType<Button>().Any(b => Equals(b.Content, "调整选区")));
                Find("矩形").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(!s.Annotating);
            }
            finally { window.Close(); s.Close(); }
        });
    }

    private static DesktopSnapshot Snapshot() => new(Bounds, [Bounds], OcrChecks.Sample(true, 400, 300));
    private static CaptureSession Session(Action<BitmapSource>? inspect = null)
    {
        var s = new CaptureSession(Snapshot(), () => { }, (_, _) => { }, (image, _) => { inspect?.Invoke(image); return Task.FromResult(""); });
        s.Selection.Begin(new(Bounds.Left, Bounds.Top), 0); s.Selection.End(new(Bounds.Right, Bounds.Bottom)); return s;
    }
    private static uint Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] p = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), p, 4, 0); return (uint)(p[2] << 16 | p[1] << 8 | p[0]);
    }
    private static Border Toolbar(OverlayWindow window) => (Border)((Canvas)((Grid)window.Content).Children[1]).Children[0];
    private static System.Collections.Generic.IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is FrameworkElement element) { yield return element; foreach (var nested in Descendants(element)) yield return nested; }
    }

    internal static void RenderPreview()
    {
        var screen = new PixelRect(0, 0, 1100, 650);
        var snapshot = new DesktopSnapshot(screen, [screen], OcrChecks.Sample(false, 1100, 650));
        var s = new CaptureSession(snapshot, () => { }, (_, _) => { }, (_, _) => Task.FromResult(""));
        s.Selection.Begin(new(10, 5), 0); s.Selection.End(new(1010, 440));
        s.SetTool(AnnotationKind.Rectangle); s.Begin(new(18, 80), 3); s.End(new(480, 130));
        s.Annotations.Deselect(); s.SetAnnotationStyle(0x409CFF, 5); s.Begin(new(18, 145), 3); s.End(new(450, 205));
        s.SetTool(AnnotationKind.Arrow); s.SetAnnotationStyle(0x38D996, 3); s.Begin(new(40, 320), 3); s.End(new(950, 320));
        s.SetTool(AnnotationKind.Arrow); s.SetAnnotationStyle(0xFFD447, 5); s.Begin(new(850, 270), 3); s.End(new(650, 140));
        var window = new OverlayWindow(s, screen);
        string dir = Path.Combine(Program.FindRoot(), ".tmp", "rectangle-preview"); Directory.CreateDirectory(dir);
        try
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1100, 650)); content.Arrange(new Rect(0, 0, 1100, 650));
            var toolbar = Toolbar(window); toolbar.Visibility = Visibility.Visible; window.Refresh();
            // The content is rendered offscreen; place the real toolbar without creating a desktop overlay.
            Canvas.SetLeft(toolbar, 440); Canvas.SetTop(toolbar, 455); content.UpdateLayout();
            var image = new RenderTargetBitmap(1100, 650, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            ImageOutput.SavePng(image, Path.Combine(dir, "preview.png"));
            ImageOutput.SavePng(s.CreateOutput(), Path.Combine(dir, "export.png"));
        }
        finally { window.Close(); s.Close(); }
    }
}
