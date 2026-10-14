using System;
using System.Linq;
using System.Threading.Tasks;
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
        Action? finished = null, Action? ocr = null)
    {
        var s = new CaptureSession(new(Bounds, [Bounds], OcrChecks.Sample(true, 400, 300)), finished ?? (() => { }), (_, _) => { },
            (_, _) => { ocr?.Invoke(); return Task.FromResult(""); }, showPin: pin, copyImage: copy);
        s.Selection.Begin(new(20, 20), 0); s.Selection.End(new(300, 200));
        s.SetTool(AnnotationKind.Arrow); s.Begin(new(50, 50), 2); s.End(new(200, 150)); return s;
    }
    private static KeyEventArgs KeyEvent(OverlayWindow window, Key key) =>
        new(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle()), Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
    private static Task Press(OverlayWindow window, Key key, ModifierKeys modifiers) => window.HandleKey(KeyEvent(window, key), modifiers);
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    internal static void Run(Action<string, Action> test)
    {
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
        test("Ctrl+V and F3 pin annotated selection without copying or disabling tool", () => OcrChecks.Pump(async () =>
        {
            foreach (var (key, modifiers) in new[] { (Key.V, ModifierKeys.Control), (Key.F3, ModifierKeys.None) })
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
        test("V Shift+C and extra modifiers stay distinct from copy and pin", () => OcrChecks.Pump(async () =>
        {
            int copies = 0, pins = 0, ocr = 0;
            var s = Session(_ => { copies++; return Task.CompletedTask; }, (_, _) => pins++, ocr: () => ocr++);
            var window = new OverlayWindow(s, Bounds);
            try
            {
                await Press(window, Key.V, ModifierKeys.None); Assert(!s.Annotating);
                await Press(window, Key.C, ModifierKeys.Shift); Assert(ocr == 1);
                foreach (var key in new[] { Key.C, Key.V })
                {
                    var e = KeyEvent(window, key); await window.HandleKey(e, ModifierKeys.Control | ModifierKeys.Shift); Assert(!e.Handled);
                    await Press(window, key, ModifierKeys.Alt);
                }
                Assert(copies == 0 && pins == 0);
            }
            finally { window.Close(); }
        }));
        test("Ctrl+C and Ctrl+V ignore dragging and empty selection", () => OcrChecks.Pump(async () =>
        {
            int outputs = 0; var s = Session(_ => { outputs++; return Task.CompletedTask; }, (_, _) => outputs++);
            var window = new OverlayWindow(s, Bounds);
            try
            {
                s.Begin(new(300, 200), 2); s.Move(new(200, 150));
                await Press(window, Key.C, ModifierKeys.Control); await Press(window, Key.V, ModifierKeys.Control); Assert(outputs == 0);
                s.End(new(20, 20)); Assert(s.Selection.Rectangle.IsEmpty);
                await Press(window, Key.C, ModifierKeys.Control); await Press(window, Key.V, ModifierKeys.Control); Assert(outputs == 0);
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
                await Press(window, Key.C, ModifierKeys.Control); await Press(window, Key.V, ModifierKeys.Control);
                Assert(copies == 1 && pins == 0); pending.SetResult(); await task;
            }
            finally { pending.TrySetResult(); window.Close(); }
        }));
    }
}
