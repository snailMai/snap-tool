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

internal static class NumberAnnotationChecks
{
    private static readonly PixelRect Bounds = new(-100, -50, 700, 450);
    private static void Assert(bool value) { if (!value) throw new Exception("Number annotation assertion failed"); }
    private static void Add(Annotations marks, int x, int y = 80)
    { marks.Begin(new(x, y), Bounds, 2, AnnotationKind.Number); marks.End(new(x, y)); }
    private static DesktopSnapshot Snapshot() => new(Bounds, [Bounds], OcrChecks.Sample(true, 800, 500));
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }

    internal static void Run(Action<string, Action> test)
    {
        test("number clicks increment across tool switches and new sessions start at one", () =>
        {
            var marks = new Annotations(); Add(marks, 0); Add(marks, 80);
            marks.Begin(new(200, 200), Bounds, 2); marks.End(new(260, 260)); Add(marks, 160);
            Assert(marks.Items.Where(m => m.Kind == AnnotationKind.Number).Select(m => m.Number).SequenceEqual(new[] { 1, 2, 3 }));
            Assert(marks.NextNumber == 4 && new Annotations().NextNumber == 1);
        });
        test("number delete does not renumber others and undo restores numbering", () =>
        {
            var marks = new Annotations(); Add(marks, 0); Add(marks, 80); Add(marks, 160);
            Add(marks, 80); Assert(marks.Items.Count == 3); marks.Delete();
            Assert(marks.Items.Select(m => m.Number).SequenceEqual(new[] { 1, 3 }) && marks.NextNumber == 4);
            marks.Undo(); Assert(marks.Items.Count == 3); marks.Undo();
            Assert(marks.Items.Count == 2 && marks.NextNumber == 3); Add(marks, 240); Assert(marks.Items.Last().Number == 3);
        });
        test("number drag moves selected mark without resizing or consuming sequence", () =>
        {
            var marks = new Annotations(); Add(marks, 0); var original = marks.Items.Single();
            marks.Begin(new(0, 80), Bounds, 2, AnnotationKind.Arrow); marks.End(new(999, 999), true);
            var moved = marks.Items.Single(); Assert(moved.Number == 1 && moved.NumberRadius == original.NumberRadius && moved.Rectangle.Intersect(Bounds) == moved.Rectangle);
            Assert(marks.NextNumber == 2); marks.Undo(); Assert(marks.Items.Single() == original);
        });
        test("number invalid clicks and cancelled gestures do not consume sequence", () =>
        {
            var marks = new Annotations(); marks.Begin(new(-200, 0), Bounds, 2, AnnotationKind.Number); marks.End(new(0, 0));
            marks.Begin(new(0, 0), default, 2, AnnotationKind.Number); Assert(marks.Items.Count == 0);
            marks.Begin(new(0, 80), Bounds, 2, AnnotationKind.Number); marks.Undo();
            Assert(marks.NextNumber == 1 && marks.Items.Count == 0); Add(marks, -99, -49);
            Assert(marks.Items.Single().Number == 1 && marks.Items.Single().Start == new PixelPoint(-99, -49));
        });
        test("number style edits and mixed undo preserve value and drawing order", () =>
        {
            var marks = new Annotations(); Add(marks, 0); marks.SetStyle(0xFFFFFF, 3);
            Assert(marks.Selected!.Number == 1 && marks.Selected.NumberRadius == 14); marks.Undo();
            Assert(marks.Items.Single().Thickness == 8); Add(marks, 80);
            marks.Begin(new(200, 100), Bounds, 2, AnnotationKind.Arrow); marks.End(new(300, 150));
            marks.Undo(); Assert(marks.Items.Count == 2 && marks.NextNumber == 3);
        });
        test("number toolbar toggles and capture borders retain selection priority", () =>
        {
            var session = new CaptureSession(Snapshot(), () => { }, (_, _) => { }, (_, _) => Task.FromResult(""));
            session.Selection.Select(Bounds); var window = new OverlayWindow(session, Bounds);
            try
            {
                var button = RefreshChecks.Descendants<Button>((DependencyObject)window.Content).Single(b => Equals(b.Tag, "序号"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(session.Tool == AnnotationKind.Number);
                session.Begin(new(0, 80), 2); session.End(new(0, 80)); Assert(session.Annotations.Items.Single().Number == 1);
                session.Begin(new(-100, 100), 2); session.End(new(-80, 100));
                Assert(session.Selection.Rectangle.Left == -80 && session.Annotations.Items.Count == 1);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(!session.Annotating);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(session.Annotations.NextNumber == 2);
                window.Refresh(); Assert(RefreshChecks.Descendants<ComboBoxItem>((DependencyObject)window.Content).Any(i => Equals(i.Content, "48 px")) ||
                    RefreshChecks.Descendants<ComboBox>((DependencyObject)window.Content).Any(c => c.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Content, "48 px"))));
            }
            finally { window.Close(); session.Close(); }
        });
        test("number original resolution output includes marks while OCR reads original", () => OcrChecks.Pump(async () =>
        {
            var snapshot = Snapshot(); var original = Pixels(snapshot.Image); BitmapSource? copied = null, pinned = null; int ocr = 0;
            var session = new CaptureSession(snapshot, () => { }, (_, _) => { }, (image, _) =>
                { Assert(Pixels(image).SequenceEqual(original)); ocr++; return Task.FromResult(""); },
                showPin: (image, _) => pinned = image, copyImage: (image, _) => { copied = image; return Task.CompletedTask; });
            session.Selection.Select(Bounds); session.SetTool(AnnotationKind.Number); session.Begin(new(0, 80), 2); session.End(new(0, 80));
            var output = session.CreateOutput(); Assert(!Pixels(output).SequenceEqual(original)); await session.RecognizeText(); Assert(ocr == 1);
            var window = new OverlayWindow(session, Bounds);
            try { await session.Copy(window); Assert(copied != null && Pixels(copied).SequenceEqual(Pixels(output))); }
            finally { window.Close(); session.Close(); }
            var pinSession = new CaptureSession(snapshot, () => { }, (_, _) => { }, (_, _) => Task.FromResult(""), showPin: (image, _) => pinned = image);
            pinSession.Selection.Select(Bounds); pinSession.SetTool(AnnotationKind.Number); pinSession.Begin(new(0, 80), 2); pinSession.End(new(0, 80)); pinSession.Pin();
            Assert(pinned != null && Pixels(pinned).SequenceEqual(Pixels(output)) && Pixels(snapshot.Image).SequenceEqual(original));
        }));
        test("number rendering supports multiple digits crop and PNG roundtrip", () =>
        {
            var snapshot = Snapshot(); var mark = new Annotation(AnnotationKind.Number, new(-95, 0), new(-95, 0), 0xFF4545, 8) { Number = 100 };
            var image = AnnotationRenderer.Compose(snapshot, Bounds, [mark]); Assert(image.PixelWidth == 800 && image.PixelHeight == 500);
            string dir = Path.Combine(Program.FindRoot(), ".tmp", "number-annotation"); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "roundtrip.png"); ImageOutput.SavePng(image, path);
            using var stream = File.OpenRead(path); var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert(Pixels(decoded).SequenceEqual(Pixels(image)));
        });
    }

    internal static void RenderPreview()
    {
        var snapshot = Snapshot(); var session = new CaptureSession(snapshot, () => { }, (_, _) => { }, (_, _) => Task.FromResult(""));
        session.Selection.Select(new(-70, -20, 670, 310)); session.SetTool(AnnotationKind.Number);
        foreach (int x in new[] { 30, 120, 210 }) { session.Begin(new(x, 200), 2); session.End(new(x, 200)); }
        var window = new OverlayWindow(session, Bounds);
        try
        {
            window.ShowToolbar(); var content = (FrameworkElement)window.Content;
            content.Measure(new Size(800, 500)); content.Arrange(new Rect(0, 0, 800, 500)); content.UpdateLayout();
            var image = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            string dir = Path.Combine(Program.FindRoot(), ".tmp", "number-annotation"); Directory.CreateDirectory(dir);
            ImageOutput.SavePng(image, Path.Combine(dir, "preview.png"));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 480, 100));
                AnnotationRenderer.Draw(dc, new[] { 1, 9, 10, 99, 100, 1000 }.Select((n, i) =>
                    new Annotation(AnnotationKind.Number, new(40 + i * 80, 50), new(40 + i * 80, 50), i == 4 ? 0xFFFFFFu : 0xFF4545u, 8) { Number = n }));
            }
            var digits = new RenderTargetBitmap(480, 100, 96, 96, PixelFormats.Pbgra32); digits.Render(visual);
            ImageOutput.SavePng(digits, Path.Combine(dir, "digits.png"));
        }
        finally { window.Close(); session.Close(); }
    }
}
