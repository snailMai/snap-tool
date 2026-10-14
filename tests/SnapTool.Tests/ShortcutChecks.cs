using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

internal static class ShortcutChecks
{
    private static readonly PixelRect Bounds = new(0, 0, 400, 300);
    private static void Assert(bool value) { if (!value) throw new Exception("Shortcut assertion failed"); }
    private static CaptureSession Session(Func<BitmapSource, Task>? copy = null, Action<BitmapSource, PixelRect>? pin = null,
        Action? finished = null, Action? ocr = null, Func<BitmapSource, CancellationToken, Task>? cancellableCopy = null)
    {
        var s = new CaptureSession(new(Bounds, [Bounds], OcrChecks.Sample(true, 400, 300)), finished ?? (() => { }), (_, _) => { },
            (_, _) => { ocr?.Invoke(); return Task.FromResult(""); }, showPin: pin,
            copyImage: cancellableCopy ?? (copy == null ? null : (image, _) => copy(image)));
        s.Selection.Begin(new(20, 20), 0); s.Selection.End(new(300, 200));
        s.SetTool(AnnotationKind.Arrow); s.Begin(new(50, 50), 2); s.End(new(200, 150)); return s;
    }
    private static KeyEventArgs KeyEvent(OverlayWindow window, Key key) =>
        new(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle()), Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
    internal static Task Press(OverlayWindow window, Key key, ModifierKeys modifiers) => window.HandleKey(KeyEvent(window, key), modifiers);
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    internal static void Run(Action<string, Action> test)
    {
        test("closing capture cancels clipboard retry and permits a new capture", () => OcrChecks.Pump(async () =>
        {
            int attempts = 0, writes = 0, closed = 0;
            var s = Session(finished: () => closed++, cancellableCopy: (image, token) =>
                ImageOutput.CopyWithRetry(image, token, _ =>
                {
                    attempts++;
                    if (attempts == 1) throw new ExternalException("Synthetic clipboard busy");
                    writes++;
                }));
            var window = new OverlayWindow(s, Bounds);
            try
            {
                var task = s.Copy(window); Assert(s.Busy && attempts == 1);
                s.Close(); s.Close();
                int nextCopies = 0;
                var next = Session(_ => { nextCopies++; return Task.CompletedTask; });
                var nextWindow = new OverlayWindow(next, Bounds);
                try { await next.Copy(nextWindow); Assert(nextCopies == 1); }
                finally { nextWindow.Close(); next.Close(); }
                await task;
                Assert(!s.Busy && attempts == 1 && writes == 0 && closed == 1);
            }
            finally { window.Close(); s.Close(); }
        }));
        test("image clipboard retry succeeds or propagates final failure", () => OcrChecks.Pump(async () =>
        {
            int attempts = 0, writes = 0;
            await ImageOutput.CopyWithRetry(OcrChecks.Sample(), CancellationToken.None, _ =>
            { if (++attempts < 3) throw new ExternalException(); writes++; });
            Assert(attempts == 3 && writes == 1);
            attempts = 0;
            try
            {
                await ImageOutput.CopyWithRetry(OcrChecks.Sample(), CancellationToken.None, _ => { attempts++; throw new ExternalException(); });
                throw new Exception("Expected retry exhaustion");
            }
            catch (ExternalException) { Assert(attempts == 4); }
        }));
        test("Enter respects toolbar controls descendants and modifiers", () => OcrChecks.Pump(async () =>
        {
            int copies = 0;
            var s = Session(_ => { copies++; return Task.CompletedTask; });
            var window = new OverlayWindow(s, Bounds);
            try
            {
                var controls = RefreshChecks.Descendants<Control>((DependencyObject)window.Content).ToArray();
                foreach (var control in controls.Where(c => c is Button || c is ComboBox))
                {
                    var e = KeyEvent(window, Key.Enter); e.Source = control;
                    await window.HandleKey(e, ModifierKeys.None); Assert(!e.Handled && copies == 0);
                }
                var item = (ComboBoxItem)controls.OfType<ComboBox>().First().Items[0];
                foreach (var source in new object[] { item, new TextBlock { Text = "8 px" } })
                {
                    var e = KeyEvent(window, Key.Enter); e.Source = source;
                    await window.HandleKey(e, ModifierKeys.None); Assert(!e.Handled && copies == 0);
                }
                foreach (var modifiers in new[] { ModifierKeys.Control, ModifierKeys.Shift, ModifierKeys.Alt, ModifierKeys.Control | ModifierKeys.Shift })
                {
                    var e = KeyEvent(window, Key.Enter); await window.HandleKey(e, modifiers);
                    Assert(!e.Handled && copies == 0);
                }
                var surface = RefreshChecks.Descendants<SelectionSurface>((DependencyObject)window.Content).Single();
                var enter = KeyEvent(window, Key.Enter); enter.Source = surface;
                await window.HandleKey(enter, ModifierKeys.None); Assert(enter.Handled && copies == 1);
            }
            finally { window.Close(); s.Close(); }
        }));
        test("Ctrl+C and Enter copy annotated selection and close once", () => OcrChecks.Pump(async () =>
        {
            foreach (var (key, modifiers) in new[] { (Key.C, ModifierKeys.Control), (Key.Enter, ModifierKeys.None) })
            {
                int copies = 0, closed = 0; BitmapSource? copied = null;
                var s = Session(image => { copies++; copied = image; return Task.CompletedTask; }, finished: () => closed++);
                var window = new OverlayWindow(s, Bounds); var expected = Pixels(s.CreateOutput());
                try
                {
                    var e = KeyEvent(window, key); await window.HandleKey(e, modifiers);
                    await Press(window, key, modifiers);
                    Assert(e.Handled && copies == 1 && closed == 1 && copied != null && Pixels(copied).SequenceEqual(expected));
                }
                finally { window.Close(); }
            }
        }));
        test("Ctrl+T and F3 pin annotated selection without copying or disabling tool", () => OcrChecks.Pump(async () =>
        {
            foreach (var (key, modifiers) in new[] { (Key.T, ModifierKeys.Control), (Key.F3, ModifierKeys.None) })
            {
                int copies = 0, pins = 0, closed = 0; BitmapSource? pinned = null;
                var s = Session(_ => { copies++; return Task.CompletedTask; }, (image, rect) =>
                    { pins++; pinned = image; Assert(rect == new PixelRect(20, 20, 300, 200)); }, () => closed++);
                var window = new OverlayWindow(s, Bounds); var expected = Pixels(s.CreateOutput());
                try
                {
                    var e = KeyEvent(window, key); await window.HandleKey(e, modifiers);
                    Assert(e.Handled && copies == 0 && pins == 1 && closed == 1 && s.Tool == AnnotationKind.Arrow);
                    Assert(pinned != null && Pixels(pinned).SequenceEqual(expected));
                }
                finally { window.Close(); }
            }
        }));
        test("Shift+C Shift+T and extra modifiers stay distinct from copy and pin", () => OcrChecks.Pump(async () =>
        {
            int copies = 0, pins = 0, ocr = 0;
            var s = Session(_ => { copies++; return Task.CompletedTask; }, (_, _) => pins++, ocr: () => ocr++);
            var window = new OverlayWindow(s, Bounds);
            try
            {
                await Press(window, Key.C, ModifierKeys.Shift); Assert(ocr == 1);
                await Press(window, Key.T, ModifierKeys.Shift); Assert(ocr == 2);
                s.SetTool(AnnotationKind.Arrow);
                var oldPinKey = KeyEvent(window, Key.V); await window.HandleKey(oldPinKey, ModifierKeys.Control);
                Assert(!oldPinKey.Handled && s.Tool == AnnotationKind.Arrow);
                foreach (var key in new[] { Key.C, Key.T, Key.V })
                {
                    var e = KeyEvent(window, key); await window.HandleKey(e, ModifierKeys.Control | ModifierKeys.Shift); Assert(!e.Handled);
                    await Press(window, key, ModifierKeys.Alt);
                }
                Assert(copies == 0 && pins == 0);
            }
            finally { window.Close(); }
        }));
        test("removed R L A V shortcuts leave every annotation mode unchanged", () => OcrChecks.Pump(async () =>
        {
            var s = Session(); var window = new OverlayWindow(s, Bounds);
            try
            {
                foreach (var kind in new AnnotationKind?[] { null, AnnotationKind.Rectangle, AnnotationKind.Arrow })
                {
                    s.SetTool(kind);
                    foreach (var key in new[] { Key.R, Key.L, Key.A, Key.V })
                    {
                        var e = KeyEvent(window, key); await window.HandleKey(e, ModifierKeys.None);
                        Assert(!e.Handled && s.Tool == kind);
                    }
                }
            }
            finally { window.Close(); s.Close(); }
        }));
        test("Ctrl+C and Ctrl+T ignore dragging and empty selection", () => OcrChecks.Pump(async () =>
        {
            int outputs = 0; var s = Session(_ => { outputs++; return Task.CompletedTask; }, (_, _) => outputs++);
            var window = new OverlayWindow(s, Bounds);
            try
            {
                s.Begin(new(300, 200), 2); s.Move(new(200, 150));
                await Press(window, Key.C, ModifierKeys.Control); await Press(window, Key.T, ModifierKeys.Control); Assert(outputs == 0);
                s.End(new(20, 20)); Assert(s.Selection.Rectangle.IsEmpty);
                await Press(window, Key.C, ModifierKeys.Control); await Press(window, Key.T, ModifierKeys.Control); Assert(outputs == 0);
            }
            finally { window.Close(); }
        }));
        test("copy shortcut consumes key before await and blocks repeated outputs", () => OcrChecks.Pump(async () =>
        {
            var pending = new TaskCompletionSource(); int copies = 0, pins = 0;
            var s = Session(_ => { copies++; return pending.Task; }, (_, _) => pins++); var window = new OverlayWindow(s, Bounds);
            try
            {
                var e = KeyEvent(window, Key.C); var task = window.HandleKey(e, ModifierKeys.Control);
                Assert(e.Handled && s.Busy);
                await Press(window, Key.C, ModifierKeys.Control); await Press(window, Key.T, ModifierKeys.Control);
                Assert(copies == 1 && pins == 0); pending.SetResult(); await task;
            }
            finally { pending.TrySetResult(); window.Close(); }
        }));
    }
}
