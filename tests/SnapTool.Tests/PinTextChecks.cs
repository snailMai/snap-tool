using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SkiaSharp;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class PinTextChecks
{
    private static void Assert(bool value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? expression = null)
    { if (!value) throw new Exception("Pin text assertion: " + expression); }
    private static PixelPoint[] Corners(int x, int y) => [new(x, y), new(x + 10, y), new(x + 10, y + 20), new(x, y + 20)];
    internal static OcrTextLayout Layout() => new("你好\nHi OCR", [new(0, 1, Corners(10, 10)), new(1, 1, Corners(24, 10)),
        new(3, 1, Corners(10, 50)), new(4, 1, Corners(24, 50)), new(6, 1, Corners(52, 50)), new(7, 1, Corners(66, 50)), new(8, 1, Corners(80, 50))]);
    private static PinWindow Window(Func<BitmapSource, CancellationToken, Task<OcrTextLayout>>? recognize = null,
        Func<string, CancellationToken, Task>? copyText = null, Func<BitmapSource, CancellationToken, Task>? copyImage = null,
        Func<Window, string?>? save = null) => new(OcrChecks.Sample(true, 120, 100), new(100, 100, 220, 200),
            copyImage ?? ((_, _) => Task.CompletedTask), save ?? (_ => null), recognize: recognize ?? ((_, _) => Task.FromResult(Layout())),
            copyText: copyText ?? ((_, _) => Task.CompletedTask));
    private static void Select(PinWindow window) { Assert(window.BeginTextSelection(new(12, 15))); window.UpdateTextSelection(new(28, 55)); window.EndPointerDrag(); }
    private static byte[] Pixels(BitmapSource image)
    { var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes; }

    internal static void Run(Action<string, Action> test)
    {
        test("OCR polygon selection preserves CJK English spaces lines and reverse drags", () =>
        {
            var layout = Layout(); var selection = new OcrTextSelection(layout);
            Assert(selection.Begin(12, 15)); selection.Update(28, 55); Assert(selection.Text == "你好\nHi" && selection.Glyphs.Count() == 4);
            Assert(selection.Begin(82, 55)); selection.Update(25, 15); Assert(selection.Text == "好\nHi OCR");
            Assert(selection.Begin(12, 55)); selection.Update(1000, 55); Assert(selection.Text == "Hi OCR");
            Assert(!selection.Begin(110, 90) && selection.Text == "");
            Assert(layout.Hit(double.NaN, 0) == -1 && layout.Hit(double.PositiveInfinity, 0, true) == -1);
            var tilted = new OcrGlyph(0, 1, [new(10, 0), new(20, 10), new(10, 20), new(0, 10)]);
            Assert(tilted.Contains(10, 10) && !tilted.Contains(1, 1));
        });
        test("OCR layout uses returned glyph positions and preserves UTF16 repeated words", () =>
        {
            WordBox Word(string text, int x) => new() { Text = text, Score = 1, BoxPoints = Corners(x, 10).Select(p => new SKPointI(p.X, p.Y)).ToArray() };
            var result = new OcrResult { StrRes = "", TextBlocks = [new() { Text = "中 😀 a a", BoxPoints = [], Chars = null, CharScores = null,
                WordResults = [Word("中", 10), Word("😀", 30), Word("a", 50), Word("a", 70), Word("missing", 90)] }] };
            var layout = OcrService.CreateLayout(result, 120, 100);
            Assert(layout.Text == "中 😀 a a" && layout.Glyphs.Select(g => g.Start).SequenceEqual(new[] { 0, 2, 5, 7 }));
            var selection = new OcrTextSelection(layout); Assert(selection.Begin(32, 15) && selection.Text == "😀");
            selection.Update(72, 15); Assert(selection.Text == "😀 a a");
            var clipped = OcrService.CreateLayout(result, 1, 1); Assert(clipped.Glyphs.Count == 0);
        });
        test("pin mouse entry recognizes once from only its independent bitmap", () => OcrChecks.Pump(async () =>
        {
            int calls = 0; BitmapSource? input = null;
            var window = Window((image, _) => { calls++; input = image; return Task.FromResult(Layout()); });
            try
            {
                window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                await window.RecognizeText(); Assert(calls == 1 && ReferenceEquals(input, window.Original) && window.TextLayout != null && window.Status == "");
                Assert(window.BeginTextSelection(new(12, 15))); window.EndPointerDrag(); Assert(window.SelectedText == "你");
            }
            finally { window.Close(); }
            Assert(window.Original == null && window.TextLayout == null && window.SelectedText == "");
        }));
        test("pin selected copy and explicit image copy preserve pixel output and menu actions", () => OcrChecks.Pump(async () =>
        {
            string? copied = null; int images = 0; BitmapSource? bitmap = null;
            string directory = Path.Combine(Program.FindRoot(), ".tmp", "pin-text-checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "pin.png");
            var window = Window(copyText: (text, _) => { copied = text; return Task.CompletedTask; },
                copyImage: (image, _) => { images++; bitmap = image; return Task.CompletedTask; }, save: _ => path);
            try
            {
                byte[] before = Pixels(window.Original!); await window.RecognizeText(); Select(window);
                Assert(window.SelectedText == "你好\nHi");
                await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); Assert(copied == window.SelectedText && images == 0);
                copied = null; ((MenuItem)window.ContextMenu.Items[6]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert(copied == "你好\nHi");
                await window.CopyImage(); Assert(images == 1 && ReferenceEquals(bitmap, window.Original));
                window.SaveImage(); using var stream = File.OpenRead(path);
                var saved = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Assert(Pixels(saved).SequenceEqual(before) && Pixels(window.Original!).SequenceEqual(before));
                Assert(!window.BeginTextSelection(new(115, 95))); await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); Assert(images == 2);
            }
            finally { window.Close(); }
        }));
        test("pin text copy failure retains selection and close cancels retries without late output", () => OcrChecks.Pump(async () =>
        {
            int copies = 0; var pending = new TaskCompletionSource(); CancellationToken captured = default;
            var window = Window(copyText: (_, token) => { captured = token; if (++copies == 1) throw new IOException(); return pending.Task; });
            await window.RecognizeText(); Select(window); await window.CopySelectedText(); Assert(window.Status.Contains("失败") && window.SelectedText == "你好\nHi");
            var operation = window.CopySelectedText(); await window.CopySelectedText(); Assert(copies == 2 && window.Busy);
            window.Close(); Assert(captured.IsCancellationRequested && window.SelectedText == "");
            pending.SetResult(); await operation; Assert(!window.Status.Contains("已复制"));
        }));
        test("selected pin text respects configured copy gesture and suppresses output during drag", () => OcrChecks.Pump(async () =>
        {
            int copies = 0; var values = ShortcutProfile.Default.Export(); values[ShortcutAction.Copy] = new(Key.D, ModifierKeys.Control);
            var profile = new ShortcutProfile(values);
            var window = new PinWindow(OcrChecks.Sample(true, 120, 100), new(100, 100, 220, 200),
                copy: (_, _) => throw new Exception("Unexpected image copy"), getShortcuts: () => profile,
                recognize: (_, _) => Task.FromResult(Layout()), copyText: (_, _) => { copies++; return Task.CompletedTask; });
            try
            {
                await window.RecognizeText(); Assert(window.BeginTextSelection(new(12, 15)));
                await ShortcutChecks.Press(window, Key.D, ModifierKeys.Control); Assert(copies == 0);
                window.EndPointerDrag(); await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); Assert(copies == 0);
                await ShortcutChecks.Press(window, Key.D, ModifierKeys.Control); Assert(copies == 1);
            }
            finally { window.Close(); }
        }));
        test("pin late OCR and queued OCR cancellation cannot repopulate a closed window", () => OcrChecks.Pump(async () =>
        {
            var pending = new TaskCompletionSource<OcrTextLayout>(); int calls = 0; CancellationToken captured = default;
            var window = Window((_, token) => { calls++; captured = token; return pending.Task; });
            var operation = window.RecognizeText(); await window.RecognizeText(); Assert(calls == 1 && window.Recognizing);
            window.Close(); Assert(captured.IsCancellationRequested); pending.SetResult(Layout()); await operation;
            Assert(window.TextLayout == null && window.SelectedText == "" && !window.Recognizing); await window.RecognizeText(true); Assert(calls == 1);
        }));
        test("pin failed and blank OCR keep picture usable and explicit retry can recover", () => OcrChecks.Pump(async () =>
        {
            int calls = 0, copies = 0;
            var window = Window((_, _) => ++calls == 1 ? Task.FromException<OcrTextLayout>(new IOException()) :
                Task.FromResult(calls == 2 ? new OcrTextLayout("", []) : Layout()), copyImage: (_, _) => { copies++; return Task.CompletedTask; });
            try
            {
                await window.RecognizeText(); Assert(window.Status.Contains("失败") && window.Original != null);
                await window.RecognizeText(); Assert(calls == 1); await window.CopyImage(); Assert(copies == 1);
                await window.RecognizeText(true); Assert(window.Status.Contains("未识别") && !window.BeginTextSelection(new(12, 15)));
                await window.RecognizeText(true); Select(window); Assert(calls == 3 && window.SelectedText == "你好\nHi");
            }
            finally { window.Close(); }
        }));
        test("OCR result arriving during selection does not change the current drag", () => OcrChecks.Pump(async () =>
        {
            int calls = 0; var pending = new TaskCompletionSource<OcrTextLayout>();
            var window = Window((_, _) => ++calls == 1 ? Task.FromResult(Layout()) : pending.Task);
            try
            {
                await window.RecognizeText(); Assert(window.BeginTextSelection(new(12, 15)));
                var operation = window.RecognizeText(true); pending.SetResult(new("", [])); await operation;
                window.UpdateTextSelection(new(28, 55)); Assert(window.SelectedText == "你好\nHi");
                window.EndPointerDrag(); Assert(window.SelectedText == "" && window.TextLayout!.Glyphs.Count == 0);
            }
            finally { window.Close(); }
        }));
        test("real OCR character polygons map large and transparent images to source pixels", () => OcrChecks.Pump(async () =>
        {
            using var ocr = new OcrService();
            var layout = await ocr.RecognizeLayout(OcrChecks.Sample(false, 3840, 2160), default);
            Assert(layout.Text.Contains("截图文字识别") && layout.Text.Contains("Hello"));
            var first = layout.Glyphs.First(g => layout.Text.Substring(g.Start, g.Length) == "截");
            Assert(first.Bounds.Left >= 10 && first.Bounds.Right < 120 && first.Bounds.Top >= 10 && first.Bounds.Bottom < 90);
            Assert(layout.Glyphs.Count > 20 && layout.Glyphs.All(g => g.Start >= 0 && g.End <= layout.Text.Length));
            var visual = new DrawingVisual(); using (var context = visual.RenderOpen())
                context.DrawText(new FormattedText("Hello OCR", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Arial"), 32, Brushes.Black, 1), new Point(24, 20));
            var transparent = new RenderTargetBitmap(900, 100, 96, 96, PixelFormats.Pbgra32); transparent.Render(visual); transparent.Freeze();
            Assert((await ocr.RecognizeLayout(transparent, default)).Text.Contains("Hello"));
            var blank = new RenderTargetBitmap(900, 220, 96, 96, PixelFormats.Pbgra32); blank.Freeze();
            Assert((await ocr.RecognizeLayout(blank, default)).Glyphs.Count == 0);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool canceled = false;
            try { await ocr.RecognizeLayout(blank, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
            Assert(canceled);
        }));
    }

    internal static async Task CheckWindows()
    {
        string? copied = null; var window = Window(copyText: (text, _) => { copied = text; return Task.CompletedTask; });
        try
        {
            window.Show(); await window.RecognizeText();
            foreach (double factor in new[] { 0.5, 2.0, 1.5 })
            {
                window.Zoom(factor); window.UpdateLayout();
                var picture = RefreshChecks.Descendants<Image>((DependencyObject)window.Content).Single();
                var point = window.ImagePoint(new(picture.ActualWidth * 12 / 120, picture.ActualHeight * 15 / 100));
                Assert(Math.Abs(point.X - 12) < 0.001 && Math.Abs(point.Y - 15) < 0.001);
                Assert(window.BeginTextSelection(point)); window.UpdateTextSelection(new(28, 55)); window.EndPointerDrag();
                await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); Assert(copied == "你好\nHi");
            }
            window.HandleDoubleClick(new(12, 15)); Assert(window.IsVisible);
            Select(window); Assert(window.Status == "");
            var layer = RefreshChecks.Descendants<PinTextLayer>((DependencyObject)window.Content).Single();
            var overlay = new RenderTargetBitmap(120, 100, 96, 96, PixelFormats.Pbgra32);
            var overlayVisual = new DrawingVisual(); using (var context = overlayVisual.RenderOpen())
                context.DrawRectangle(new VisualBrush(layer) { ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, layer.ActualWidth, layer.ActualHeight), Stretch = Stretch.Fill }, null, new Rect(0, 0, 120, 100));
            overlay.Render(overlayVisual); byte[] highlight = Pixels(overlay);
            int selectedPixel = (15 * 120 + 12) * 4, blankPixel = (95 * 120 + 115) * 4;
            Assert(highlight[selectedPixel + 3] > 0 && highlight[selectedPixel + 3] < 255 && highlight[selectedPixel] > highlight[selectedPixel + 2]);
            Assert(highlight[blankPixel + 3] == 0);
            var dpi = VisualTreeHelper.GetDpi(window); var content = (FrameworkElement)window.Content;
            var preview = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32); preview.Render(content);
            string directory = Path.Combine(Program.FindRoot(), ".tmp", "pin-text"); Directory.CreateDirectory(directory);
            ImageOutput.SavePng(preview, Path.Combine(directory, "selection.png"));
            window.HandleDoubleClick(new(115, 95)); Assert(!window.IsVisible && window.Original == null && window.TextLayout == null);
            Console.WriteLine("PASS native pin text selection: zoom mapping, text copy, protected text double-click, blank double-click and cache release.");
        }
        finally { window.Close(); }
    }
}
