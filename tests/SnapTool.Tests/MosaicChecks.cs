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

internal static class MosaicChecks
{
    private static readonly PixelRect Bounds = new(-100, -50, 700, 450);
    private static void Assert(bool condition, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    { if (!condition) throw new Exception($"Mosaic assertion failed at line {line}."); }
    private static DesktopSnapshot Snapshot()
    {
        byte[] pixels = new byte[800 * 500 * 4];
        for (int y = 0; y < 500; y++) for (int x = 0; x < 800; x++)
        {
            int i = (y * 800 + x) * 4;
            pixels[i] = (byte)(x % 256); pixels[i + 1] = (byte)(y % 256);
            pixels[i + 2] = (byte)(((x + y) % 2) * 255); pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(800, 500, 96, 96, PixelFormats.Bgra32, null, pixels, 800 * 4); image.Freeze();
        return new(Bounds, [Bounds], image);
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var result = new byte[image.PixelWidth * image.PixelHeight * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(result, image.PixelWidth * 4, 0); return result;
    }
    private static void Add(Annotations marks, PixelPoint start, PixelPoint end)
    { marks.Begin(start, Bounds, 1, AnnotationKind.Mosaic); marks.End(end); }
    private static string DirectoryPath
    { get { string path = Path.Combine(Program.FindRoot(), ".tmp", "mosaic-validation"); Directory.CreateDirectory(path); return path; } }

    internal static void Run(Action<string, Action> test)
    {
        test("mosaic defaults to 30px with independent grain and rejects excessive sizes", () =>
        {
            var marks = new Annotations(); Add(marks, new(0, 0), new(80, 80));
            Assert(marks.Items.Single().Thickness == 30 && marks.Thickness == 8);
            marks.SetStyle(0xFF4545, 24); Assert(marks.MosaicSize == 24 && marks.Thickness == 8);
            bool rejected = false;
            try { marks.SetStyle(0xFF4545, 31); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected && marks.Items.Single().Thickness == 24);
            marks.Deselect(); marks.Begin(new(200, 100), Bounds, 1, AnnotationKind.Rectangle); marks.End(new(260, 160));
            Assert(marks.Items.Last().Thickness == 8); marks.SetStyle(0x409CFF, 5);
            marks.Deselect(); Add(marks, new(300, 100), new(360, 160));
            Assert(marks.Items.Last().Thickness == 24 && marks.Thickness == 5);
        });
        test("mosaic reverse drag clamps and zero-size gestures create no annotation", () =>
        {
            var marks = new Annotations(); Add(marks, new(0, 0), new(0, 0)); Add(marks, new(0, 0), new(1, 20));
            Assert(marks.Items.Count == 0 && !marks.CanUndo);
            Add(marks, new(50, 50), new(-200, -100));
            Assert(marks.Items.Single().Rectangle == new PixelRect(-100, -50, 50, 50));
            Assert(marks.Items.Single().Kind == AnnotationKind.Mosaic && Annotations.Handles(marks.Items.Single()).Count() == 8);
        });
        test("mosaic supports interior movement resizing style deletion and undo", () =>
        {
            var marks = new Annotations(); Add(marks, new(0, 0), new(80, 80)); var original = marks.Items.Single();
            Assert(marks.HitTest(new(30, 30), 1) == (0, SelectionHit.Move));
            marks.Begin(new(30, 30), Bounds, 1, AnnotationKind.Arrow); marks.End(new(60, 50));
            Assert(marks.Items.Single().Rectangle == new PixelRect(30, 20, 110, 100)); marks.Undo();
            Add(marks, new(20, 20), new(20, 20));
            marks.Begin(new(80, 80), Bounds, 1, AnnotationKind.Mosaic); marks.End(new(110, 120));
            Assert(marks.Items.Single().Rectangle == new PixelRect(0, 0, 110, 120)); marks.Undo();
            Add(marks, new(20, 20), new(20, 20)); marks.SetStyle(0x409CFF, 5); marks.Delete();
            Assert(marks.Items.Count == 0); marks.Undo(); Assert(marks.Items.Single().Thickness == 5);
            marks.Undo(); Assert(marks.Items.Single() == original);
        });
        test("mosaic blocks average original pixels including partial edge blocks", () =>
        {
            var snapshot = Snapshot(); var raw = Pixels(snapshot.Image);
            var region = new PixelRect(-91, -37, -72, -26);
            var mark = new Annotation(AnnotationKind.Mosaic, new(region.Left, region.Top), new(region.Right, region.Bottom), 0, 8);
            var actual = Pixels(AnnotationRenderer.Compose(snapshot, Bounds, [mark]));
            for (int y = 0; y < 500; y++) for (int x = 0; x < 800; x++)
            {
                int index = (y * 800 + x) * 4;
                if (!region.Contains(new(x - 100, y - 50)))
                { for (int c = 0; c < 4; c++) Assert(actual[index + c] == raw[index + c]); continue; }
                int bx = 9 + (x - 9) / 8 * 8, by = 13 + (y - 13) / 8 * 8;
                int right = Math.Min(bx + 8, 28), bottom = Math.Min(by + 8, 24), count = (right - bx) * (bottom - by);
                for (int c = 0; c < 4; c++)
                {
                    long sum = 0;
                    for (int yy = by; yy < bottom; yy++) for (int xx = bx; xx < right; xx++) sum += raw[(yy * 800 + xx) * 4 + c];
                    Assert(actual[index + c] == sum / count);
                }
            }
            Assert(Pixels(snapshot.Image).SequenceEqual(raw));
        });
        test("mosaic selection crop preserves block alignment and overlay order", () =>
        {
            var snapshot = Snapshot(); var mark = new Annotation(AnnotationKind.Mosaic, new(-91, -37), new(80, 70), 0, 8);
            var full = AnnotationRenderer.Compose(snapshot, Bounds, [mark]); var crop = new PixelRect(-87, -33, 40, 40);
            var expected = new CroppedBitmap(full, new Int32Rect(13, 17, crop.Width, crop.Height));
            Assert(Pixels(expected).SequenceEqual(Pixels(AnnotationRenderer.Compose(snapshot, crop, [mark]))));
            var rectangle = new Annotation(new PixelRect(-90, -36, 60, 50), 0xFFFFFF, 8);
            Assert(Pixels(AnnotationRenderer.Compose(snapshot, crop, [rectangle, mark])).SequenceEqual(Pixels(expected)));
            Assert(!Pixels(AnnotationRenderer.Compose(snapshot, crop, [mark, rectangle])).SequenceEqual(Pixels(expected)));
        });
        test("mosaic preview and export match across scales with nearest-neighbor blocks", () =>
        {
            var snapshot = Snapshot(); var mark = new Annotation(AnnotationKind.Mosaic, new(-91, -37), new(81, 71), 0, 8);
            var expected = Pixels(AnnotationRenderer.Compose(snapshot, Bounds, [mark]));
            foreach (double scale in new[] { 1.0, 0.8, 2.0 / 3.0 })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(scale, scale)); dc.DrawImage(snapshot.Image, new Rect(0, 0, 800, 500));
                    dc.PushTransform(new TranslateTransform(100, 50)); AnnotationRenderer.Draw(dc, [mark], new MosaicRenderer(snapshot)); dc.Pop(); dc.Pop();
                }
                var rendered = new RenderTargetBitmap(800, 500, 96 / scale, 96 / scale, PixelFormats.Pbgra32); rendered.Render(visual);
                Assert(Pixels(rendered).SequenceEqual(expected));
            }
        });
        test("mosaic copy pin PNG and OCR preserve their intended pixels", () => OcrChecks.Pump(async () =>
        {
            var snapshot = Snapshot(); var original = Pixels(snapshot.Image); BitmapSource? copied = null, pinned = null, recognized = null;
            CaptureSession Session() => new(snapshot, () => { }, (_, _) => { }, (image, _) => { recognized = image; return Task.FromResult("text"); },
                (_, _) => Task.CompletedTask, showPin: (image, _) => pinned = image, copyImage: (image, _) => { copied = image; return Task.CompletedTask; });
            var session = Session(); session.Selection.Select(Bounds); session.SetTool(AnnotationKind.Mosaic);
            session.Begin(new(0, 0), 1); session.End(new(150, 100)); var output = session.CreateOutput();
            string path = Path.Combine(DirectoryPath, "roundtrip.png"); ImageOutput.SavePng(output, path);
            using (var file = File.OpenRead(path)) Assert(Pixels(BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0]).SequenceEqual(Pixels(output)));
            await session.Copy(null!); Assert(copied != null && Pixels(copied).SequenceEqual(Pixels(output)));
            session = Session(); session.Selection.Select(Bounds); session.SetTool(AnnotationKind.Mosaic); session.Begin(new(0, 0), 1); session.End(new(150, 100)); session.Pin();
            Assert(pinned != null && Pixels(pinned).SequenceEqual(Pixels(output)));
            session = Session(); session.Selection.Select(Bounds); session.SetTool(AnnotationKind.Mosaic); session.Begin(new(0, 0), 1); session.End(new(150, 100)); await session.RecognizeText();
            Assert(recognized != null && Pixels(recognized).SequenceEqual(original) && Pixels(snapshot.Image).SequenceEqual(original));
        }));
        test("mosaic toolbar toggles and displays grain without color or a new hotkey", () =>
        {
            var snapshot = Snapshot(); var session = new CaptureSession(snapshot, () => { }, (_, _) => { }, (_, _) => Task.FromResult("")); session.Selection.Select(Bounds);
            var window = new OverlayWindow(session, Bounds);
            try
            {
                var panel = (StackPanel)((Border)((Canvas)((Grid)window.Content).Children[1]).Children[0]).Child;
                var button = ((StackPanel)panel.Children[1]).Children.OfType<Button>().Single(b => b.Content?.ToString() == "马赛克");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Assert(session.Tool == AnnotationKind.Mosaic);
                window.Refresh();
                var style = (StackPanel)panel.Children[2]; Assert(((TextBlock)style.Children[2]).Text == "粒度");
                Assert(style.Children[0].Visibility == Visibility.Collapsed && style.Children[1].Visibility == Visibility.Collapsed);
                Assert(((ComboBoxItem)((ComboBox)style.Children[3]).SelectedItem).Content.ToString() == "30 px");
                Assert(((ComboBox)style.Children[3]).Items.Cast<ComboBoxItem>().Select(i => (int)i.Tag).SequenceEqual(new[] { 8, 16, 24, 30 }));
                ((ComboBox)style.Children[3]).SelectedIndex = 1;
                Assert(session.Annotations.MosaicSize == 16 && session.Annotations.Thickness == 8);
                session.SetTool(AnnotationKind.Rectangle); window.Refresh();
                Assert(((ComboBox)style.Children[3]).Items.Cast<ComboBoxItem>().Select(i => (int)i.Tag).SequenceEqual(new[] { 2, 3, 5, 8 }));
                Assert(((ComboBoxItem)((ComboBox)style.Children[3]).SelectedItem).Content.ToString() == "8 px");
                session.SetTool(AnnotationKind.Mosaic); window.Refresh();
                Assert(((ComboBoxItem)((ComboBox)style.Children[3]).SelectedItem).Content.ToString() == "16 px");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Assert(!session.Annotating);
            }
            finally { window.Close(); session.Close(); }
        });
        test("mosaic cache drops superseded geometry deleted marks and closed sessions", () =>
        {
            var session = new CaptureSession(Snapshot(), () => { }, (_, _) => { }, (_, _) => Task.FromResult("")); session.Selection.Select(Bounds); session.SetTool(AnnotationKind.Mosaic);
            session.Begin(new(0, 0), 1); session.End(new(150, 100)); session.CreateOutput(); Assert(session.Mosaic.CachedRegions == 1);
            session.Begin(new(20, 20), 1); session.End(new(30, 30)); session.CreateOutput(); Assert(session.Mosaic.CachedRegions == 1);
            session.DeleteAnnotation(); Assert(session.Mosaic.CachedRegions == 0); session.UndoAnnotation(); session.CreateOutput(); Assert(session.Mosaic.CachedRegions == 1);
            session.Close(); Assert(session.Mosaic.CachedRegions == 0);
        });
    }

    internal static void RenderPreview()
    {
        var snapshot = Snapshot(); var session = new CaptureSession(snapshot, () => { }, (_, _) => { }, (_, _) => Task.FromResult(""));
        session.Selection.Select(new(-70, -20, 670, 310)); session.SetTool(AnnotationKind.Mosaic);
        session.Begin(new(40, 30), 1); session.End(new(300, 150));
        var window = new OverlayWindow(session, Bounds);
        try
        {
            window.ShowToolbar(); var content = (FrameworkElement)window.Content; content.Measure(new Size(800, 500)); content.Arrange(new Rect(0, 0, 800, 500)); content.UpdateLayout();
            var image = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32); image.Render(content); ImageOutput.SavePng(image, Path.Combine(DirectoryPath, "preview.png"));
        }
        finally { window.Close(); session.Close(); }
    }
}
