using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class TextAnnotationChecks
{
    private static readonly PixelRect Bounds = new(-100, -50, 700, 450);
    private static void Assert(bool value, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    { if (!value) throw new Exception("Text annotation assertion failed at line " + line); }
    private static DesktopSnapshot Snapshot() => new(Bounds, [Bounds], OcrChecks.Sample(true, 800, 500));
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    private static void Text(Annotations marks, string value, PixelPoint? point = null)
    {
        marks.BeginText(point ?? new(20, 40));
        marks.UpdateText(value, TextAnnotationRenderer.Measure(value, marks.TextStyle)); marks.EndText();
    }
    private static CaptureSession Session(DesktopSnapshot snapshot, Action<BitmapSource, PixelRect>? pin = null, Func<BitmapSource, System.Threading.CancellationToken, Task>? copy = null) =>
        new(snapshot, () => { }, (_, _) => { }, (_, _) => Task.FromResult(""), showPin: pin, copyImage: copy);

    internal static void Run(Action<string, Action> test)
    {
        test("text draft commits as one undo action and blank or canceled drafts leave no marks", () =>
        {
            var marks = new Annotations(); marks.BeginText(new(10, 20));
            marks.UpdateText("输入", new(70, 40)); marks.UpdateText("输入内容\n第二行", new(150, 80)); marks.EndText();
            Assert(marks.Items.Single().Text.Contains("第二行")); marks.Undo(); Assert(marks.Items.Count == 0 && !marks.CanUndo);
            marks.BeginText(new(10, 20)); marks.UpdateText("取消", new(70, 40)); marks.EndText(true);
            marks.BeginText(new(10, 20)); marks.UpdateText(" \n", new(20, 80)); marks.EndText();
            Assert(marks.Items.Count == 0 && !marks.CanUndo && !marks.EditingText);
        });
        test("editing text can cancel restore delete blank and undo without losing other marks", () =>
        {
            var marks = new Annotations(); Text(marks, "原文"); var original = marks.Items.Single();
            marks.Begin(new(300, 200), Bounds, 2); marks.End(new(400, 260));
            marks.BeginText(original.Start, 0); marks.UpdateText("改文", new(80, 40)); marks.EndText(true);
            Assert(marks.Items[0] == original && marks.Items.Count == 2);
            marks.BeginText(original.Start, 0); marks.UpdateText("改文", new(80, 40)); marks.EndText(); marks.Undo();
            Assert(marks.Items[0] == original);
            marks.BeginText(original.Start, 0); marks.UpdateText("", new(0, 0)); marks.EndText();
            Assert(marks.Items.Count == 1 && marks.Items[0].Kind == AnnotationKind.Rectangle);
            marks.Undo(); Assert(marks.Items[0] == original && marks.Items.Count == 2);
        });
        test("text moves in other tools and shares deletion and undo order", () =>
        {
            var marks = new Annotations(); Text(marks, "move me"); var original = marks.Items.Single();
            marks.Begin(new(25, 45), Bounds, 2, AnnotationKind.Arrow); marks.End(new(90, 100));
            var moved = marks.Items.Single(); Assert(moved.Text == original.Text && moved.Rectangle.Width == original.Rectangle.Width && moved.Start != original.Start);
            marks.Delete(); Assert(marks.Items.Count == 0); marks.Undo(); Assert(marks.Items.Single() == moved);
            marks.Undo(); Assert(marks.Items.Single() == original);
        });
        test("text styles recompute bounds preserve content and undo atomically", () =>
        {
            var marks = new Annotations(); Text(marks, "中文 Italic"); var original = marks.Items.Single();
            var style = marks.TextStyle with { FontFamily = "Segoe UI", FontSize = 64, Bold = false, Italic = true, Outline = false, Background = true };
            marks.SetTextStyle(style, TextAnnotationRenderer.Measure(original.Text, style));
            Assert(marks.Selected!.Text == original.Text && marks.Selected.TextStyle == style && marks.Selected.Rectangle.Width > original.Rectangle.Width);
            marks.Undo(); Assert(marks.Items.Single() == original);
            bool rejected = false; try { marks.SetTextStyle(style with { FontSize = 201 }, new(20, 20)); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected && marks.Items.Single() == original);
        });
        test("text editor keeps Enter and text clipboard shortcuts local and Escape cancels only edit", () => OcrChecks.Pump(async () =>
        {
            int copies = 0;
            var session = Session(Snapshot(), copy: (_, _) => { copies++; return Task.CompletedTask; }); session.Selection.Select(Bounds);
            var window = new OverlayWindow(session, Bounds);
            try
            {
                session.SetTool(AnnotationKind.Text); Assert(session.TryEditText(window, new(20, 40), 2));
                var input = window.TextEditorBox!; input.Text = "输入内容\nsecond line";
                foreach (var (key, modifier) in new[] { (Key.C, ModifierKeys.Control), (Key.Z, ModifierKeys.Control), (Key.Delete, ModifierKeys.None), (Key.Enter, ModifierKeys.None), (Key.C, ModifierKeys.Shift) })
                {
                    var e = new KeyEventArgs(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle()), Environment.TickCount, key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = input };
                    await window.HandleKey(e, modifier); Assert(!e.Handled && copies == 0 && session.Annotations.EditingText);
                }
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle()), Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = input };
                await window.HandleKey(escape, ModifierKeys.None);
                Assert(escape.Handled && session.Annotations.Items.Count == 0 && window.TextEditorBox == null && session.Selection.Rectangle == Bounds);
                Assert(session.TryEditText(window, new(20, 40), 2)); window.TextEditorBox!.Text = "确认文字";
                var confirm = new KeyEventArgs(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle()), Environment.TickCount, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = window.TextEditorBox };
                await window.HandleKey(confirm, ModifierKeys.Control);
                Assert(confirm.Handled && !session.Annotations.EditingText && session.Annotations.Items.Single().Text == "确认文字");
            }
            finally { window.Close(); session.Close(); }
        }));
        test("text toolbar updates font style and capture edge resize retains desktop text position", () =>
        {
            var session = Session(Snapshot()); session.Selection.Select(Bounds); var window = new OverlayWindow(session, Bounds);
            try
            {
                RefreshChecks.Descendants<Button>((DependencyObject)window.Content).Single(b => Equals(b.Tag, "文字")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(session.Tool == AnnotationKind.Text && session.TryEditText(window, new(20, 40), 2)); window.TextEditorBox!.Text = "样式";
                session.SetTextStyle(session.Annotations.TextStyle with { FontSize = 48, Bold = false, Italic = true, Background = true });
                window.Refresh();
                double editorScale = (window.ActualHeight > 0 ? window.ActualHeight : window.Height) / Bounds.Height;
                if (Math.Abs(window.TextEditorBox!.FontSize / editorScale - 48) >= 0.01 || window.TextEditorBox.FontStyle != FontStyles.Italic)
                    throw new Exception($"Editor font={window.TextEditorBox.FontSize}, scale={editorScale}, style={window.TextEditorBox.FontStyle}, model={session.Annotations.Selected!.TextStyle.FontSize}/{session.Annotations.Selected.TextStyle.Italic}");
                session.FinishTextEditing(); var mark = session.Annotations.Items.Single();
                session.Begin(new(-100, 100), 2); session.End(new(-80, 100));
                Assert(session.Selection.Rectangle.Left == -80 && session.Annotations.Items.Single() == mark);
                Assert(session.TryEditText(window, new(mark.Start.X + 3, mark.Start.Y + 3), 2, true));
                session.FinishTextEditing(true); Assert(session.Annotations.Items.Single() == mark);
            }
            finally { window.Close(); session.Close(); }
        });
        test("text preview and original resolution output match across scales and crops", () =>
        {
            var snapshot = Snapshot(); var marks = new Annotations(); Text(marks, "输入内容\nItalic text");
            var style = marks.TextStyle with { FontSize = 48, Italic = true }; marks.SetTextStyle(style, TextAnnotationRenderer.Measure(marks.Selected!.Text, style));
            var output = AnnotationRenderer.Compose(snapshot, Bounds, marks.Items);
            foreach (double scale in new[] { 0.5, 0.8, 1.0, 1.25, 2.0 })
            {
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    dc.DrawImage(snapshot.Image, new Rect(0, 0, 800, 500)); dc.PushTransform(new TranslateTransform(100, 50));
                    AnnotationRenderer.Draw(dc, marks.Items); dc.Pop(); dc.Pop();
                }
                var image = new RenderTargetBitmap(800, 500, 96 / scale, 96 / scale, PixelFormats.Pbgra32); image.Render(visual);
                Assert(Pixels(image).SequenceEqual(Pixels(output)));
            }
            var crop = new PixelRect(40, 55, 180, 120);
            var cropped = new CroppedBitmap(output, new Int32Rect(crop.Left + 100, crop.Top + 50, crop.Width, crop.Height));
            Assert(Pixels(cropped).SequenceEqual(Pixels(AnnotationRenderer.Compose(snapshot, crop, marks.Items))));
        });
        test("text color white outline and optional background render independently", () =>
        {
            int[] whites = new int[3];
            for (int mode = 0; mode < 3; mode++)
            {
                var style = new TextAnnotationStyle { Outline = mode > 0, Background = mode == 2 };
                var size = TextAnnotationRenderer.Measure("描边 Text", style);
                var mark = new Annotation(AnnotationKind.Text, new(20, 20), new(20 + size.X, 20 + size.Y), 0xFF4545, 8)
                    { Text = "描边 Text", TextStyle = style };
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
                { dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 400, 160)); AnnotationRenderer.Draw(dc, [mark]); }
                var image = new RenderTargetBitmap(400, 160, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                var pixels = Pixels(image); int reds = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] == 255 && pixels[i + 1] == 255 && pixels[i + 2] == 255) whites[mode]++;
                    if (pixels[i] == 0x45 && pixels[i + 1] == 0x45 && pixels[i + 2] == 255) reds++;
                }
                Assert(reds > 10);
            }
            Assert(whites[0] == 0 && whites[1] > 0 && whites[2] > whites[1]);
        });
        test("text copy pin PNG and original OCR stay consistent and editor commits before output", () => OcrChecks.Pump(async () =>
        {
            foreach (bool pin in new[] { false, true })
            {
                var snapshot = Snapshot(); var raw = Pixels(snapshot.Image); BitmapSource? result = null; int recognitions = 0;
                var session = new CaptureSession(snapshot, () => { }, (_, _) => { }, (image, _) =>
                    { Assert(Pixels(image).SequenceEqual(raw)); recognitions++; return Task.FromResult(""); },
                    showPin: (image, _) => result = image, copyImage: (image, _) => { result = image; return Task.CompletedTask; });
                session.Selection.Select(Bounds); session.SetTool(AnnotationKind.Text); var window = new OverlayWindow(session, Bounds);
                try
                {
                    Assert(session.TryEditText(window, new(20, 40), 2)); window.TextEditorBox!.Text = "文字输出";
                    var expected = session.CreateOutput(); Assert(window.TextEditorBox == null && !Pixels(expected).SequenceEqual(raw));
                    await session.RecognizeText(); Assert(recognitions == 1);
                    if (pin) session.Pin(); else await session.Copy(window);
                    Assert(result != null && Pixels(result).SequenceEqual(Pixels(expected)));
                    string dir = Path.Combine(Program.FindRoot(), ".tmp", "text-annotation"); Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "roundtrip.png"); ImageOutput.SavePng(expected, path);
                    using var stream = File.OpenRead(path); var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    Assert(Pixels(decoded.Frames[0]).SequenceEqual(Pixels(expected)) && Pixels(snapshot.Image).SequenceEqual(raw));
                }
                finally { window.Close(); session.Close(); }
            }
        }));
    }

    internal static void CheckWindow()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var monitor = Native.Monitors()[0]; var bounds = new PixelRect(monitor.Left + 80, monitor.Top + 80, monitor.Left + 980, monitor.Top + 680);
        int copies = 0;
        var session = Session(new(bounds, [bounds], OcrChecks.Sample(true, 900, 600)), copy: (_, _) => { copies++; return Task.CompletedTask; });
        session.Selection.Select(new(bounds.Left + 20, bounds.Top + 20, bounds.Right - 20, bounds.Bottom - 100));
        session.SetTool(AnnotationKind.Text);
        try
        {
            OcrChecks.Pump(async () =>
            {
                session.Show(); await Task.Delay(150);
                var window = app.Windows.OfType<OverlayWindow>().Single();
                var point = new PixelPoint(bounds.Left + 100, bounds.Top + 100);
                Assert(session.TryEditText(window, point, 2)); await Task.Delay(50);
                var input = window.TextEditorBox!; Assert(input.IsKeyboardFocusWithin);
                input.Text = "中文输入\nEnglish text";
                session.SetTextStyle(session.Annotations.TextStyle with { FontSize = 48, Italic = true });
                double scale = window.ActualHeight / bounds.Height;
                Assert(Math.Abs(input.FontSize / scale - 48) < 0.01 && input.FontStyle == FontStyles.Italic);
                await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control);
                Assert(copies == 0 && session.Annotations.EditingText);
                await ShortcutChecks.Press(window, Key.Enter, ModifierKeys.Control);
                Assert(window.IsVisible && window.TextEditorBox == null && session.Annotations.Items.Single().Text == input.Text);
                var original = session.Annotations.Items.Single();
                Assert(session.TryEditText(window, new(point.X + 10, point.Y + 10), 2, true));
                window.TextEditorBox!.Text = "放弃修改";
                await ShortcutChecks.Press(window, Key.Escape, ModifierKeys.None);
                Assert(window.IsVisible && session.Annotations.Items.Single() == original && copies == 0);
                Assert(session.TryEditText(window, new(point.X + 10, point.Y + 10), 2, true));
                window.TextEditorBox!.Text = "点击其他位置确认";
                var copyButton = RefreshChecks.Descendants<Button>((DependencyObject)window.Content).Single(b => Equals(b.Tag, "复制"));
                copyButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Assert(window.TextEditorBox == null && session.Annotations.Items.Single().Text == "点击其他位置确认");
                Assert(copies == 0);
                Console.WriteLine("PASS native text editor: focus, physical font size, local text shortcuts, confirm/cancel, outside click commit; synthetic text and clipboard writer only.");
            });
        }
        finally { session.Close(); app.Shutdown(); }
    }

    internal static void RenderPreview()
    {
        var fixture = new DrawingVisual(); using (var dc = fixture.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(237, 242, 232)), null, new Rect(0, 0, 800, 500));
        var source = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32); source.Render(fixture); source.Freeze();
        var session = Session(new(Bounds, [Bounds], source)); session.Selection.Select(new(-80, -30, 650, 320)); session.SetTool(AnnotationKind.Text);
        var window = new OverlayWindow(session, Bounds);
        try
        {
            var style = session.Annotations.TextStyle with { FontSize = 64 }; session.SetTextStyle(style);
            Text(session.Annotations, "输入内容", new(90, 80)); window.ShowToolbar();
            var content = (FrameworkElement)window.Content; content.Measure(new Size(800, 500)); content.Arrange(new Rect(0, 0, 800, 500)); content.UpdateLayout();
            var toolbar = ((Canvas)((Grid)window.Content).Children[1]).Children[0];
            Canvas.SetLeft(toolbar, 20); Canvas.SetTop(toolbar, 390); content.UpdateLayout();
            var image = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            string dir = Path.Combine(Program.FindRoot(), ".tmp", "text-annotation"); Directory.CreateDirectory(dir);
            ImageOutput.SavePng(image, Path.Combine(dir, "preview.png"));
            ImageOutput.SavePng(session.CreateOutput(), Path.Combine(dir, "export.png"));
        }
        finally { window.Close(); session.Close(); }
    }
}
