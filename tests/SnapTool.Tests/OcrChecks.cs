using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class OcrChecks
{
    internal static BitmapSource Sample(bool blank = false, int width = 900, int height = 220)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (!blank)
            {
                int y = 20;
                foreach (string line in new[] { "截图文字识别", "Hello OCR 12345", "ABC-2026: 98.50" })
                {
                    var text = new FormattedText(line, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                        new Typeface("Microsoft YaHei"), 32, Brushes.Black, 1);
                    dc.DrawText(text, new Point(24, y)); y += 60;
                }
            }
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    internal static void Run(Action<string, Action> test)
    {
        void Check(string name, Func<Task> action) => test(name, () => Pump(action));
        var image = Sample();
        var snapshot = new DesktopSnapshot(new(0, 0, 900, 220), [new(0, 0, 900, 220)], image);
        CaptureSession Session(Func<BitmapSource, CancellationToken, Task<string>> recognize,
            Func<string, CancellationToken, Task> copy, Action? finished = null, Action<string, string>? notify = null)
        {
            var s = new CaptureSession(snapshot, finished ?? (() => { }), notify ?? ((_, _) => { }), recognize, copy);
            s.Selection.Begin(new(0, 0), 0); s.Selection.End(new(900, 220)); return s;
        }

        Check("OCR copies exact text and closes once", async () =>
        {
            int closed = 0, notified = 0; string clipboard = "before";
            var session = Session((_, _) => Task.FromResult("中文\r\nHello 123!"),
                (text, _) => { clipboard = text; return Task.CompletedTask; }, () => closed++, (_, _) => notified++);
            await session.RecognizeText();
            Assert(clipboard == "中文\r\nHello 123!" && closed == 1 && notified == 1 && !session.Busy);
        });
        Check("OCR uses final resized selection", async () =>
        {
            int width = 0, height = 0;
            var s = Session((crop, _) => { width = crop.PixelWidth; height = crop.PixelHeight; return Task.FromResult(""); }, (_, _) => Task.CompletedTask);
            s.Selection.Begin(new(900, 220), 3); s.Selection.End(new(600, 120));
            await s.RecognizeText(); Assert(width == 600 && height == 120);
        });
        Check("OCR ignores empty and dragging selections", async () =>
        {
            int calls = 0;
            var s = new CaptureSession(snapshot, () => { }, (_, _) => { }, (_, _) => { calls++; return Task.FromResult("x"); });
            await s.RecognizeText(); s.Selection.Begin(new(0, 0), 0); s.Selection.Update(new(200, 100));
            await s.RecognizeText(); Assert(calls == 0); s.Close();
        });
        Check("OCR blank preserves clipboard and selection", async () =>
        {
            int copies = 0, closed = 0;
            var s = Session((_, _) => Task.FromResult(" \n"), (_, _) => { copies++; return Task.CompletedTask; }, () => closed++);
            var before = s.Selection.Rectangle; await s.RecognizeText();
            Assert(copies == 0 && closed == 0 && s.Selection.Rectangle == before && s.Status.Contains("未识别"));
        });
        Check("OCR duplicate request and edits are ignored while busy", async () =>
        {
            int calls = 0;
            var pending = new TaskCompletionSource<string>();
            var s = Session((_, _) => { calls++; return pending.Task; }, (_, _) => Task.CompletedTask);
            var first = s.RecognizeText(); var before = s.Selection.Rectangle;
            s.Begin(new(30, 40), 3); s.Move(new(90, 100)); s.End(new(90, 100));
            await s.RecognizeText(); Assert(calls == 1 && s.Busy && s.Recognizing && s.Selection.Rectangle == before);
            pending.SetResult(""); await first; Assert(!s.Busy);
        });
        Check("OCR cancel discards late result and forbids restart", async () =>
        {
            int copies = 0, closes = 0, calls = 0; CancellationToken received = default;
            var pending = new TaskCompletionSource<string>();
            var s = Session((_, token) => { calls++; received = token; return pending.Task; },
                (_, _) => { copies++; return Task.CompletedTask; }, () => closes++);
            var work = s.RecognizeText(); s.Close(); s.Close(); Assert(received.IsCancellationRequested);
            pending.SetResult("late result"); await work; await s.RecognizeText();
            Assert(copies == 0 && closes == 1 && calls == 1);
        });
        Check("OCR failure preserves selection and allows retry", async () =>
        {
            int calls = 0, copies = 0;
            var s = Session((_, _) => ++calls == 1 ? Task.FromException<string>(new IOException("test")) : Task.FromResult("ok"),
                (_, _) => { copies++; return Task.CompletedTask; });
            var before = s.Selection.Rectangle; await s.RecognizeText();
            Assert(!s.Busy && s.Selection.Rectangle == before && copies == 0 && s.Status.Contains("失败"));
            await s.RecognizeText(); Assert(copies == 1);
        });
        Check("clipboard transient lock retries exact text", async () =>
        {
            int attempts = 0; string result = "";
            await ImageOutput.CopyTextWithRetry("中文\nHello!", default, text =>
            { if (++attempts < 3) throw new ExternalException(); result = text; });
            Assert(attempts == 3 && result == "中文\nHello!");
        });
        Check("clipboard persistent lock stops after four attempts", async () =>
        {
            int attempts = 0;
            await Throws<ExternalException>(() => ImageOutput.CopyTextWithRetry("text", default, _ => { attempts++; throw new ExternalException(); }));
            Assert(attempts == 4);
        });
        Check("cancel during clipboard retry prevents later write", async () =>
        {
            int attempts = 0; using var cancel = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => ImageOutput.CopyTextWithRetry("text", cancel.Token, _ =>
            { attempts++; cancel.Cancel(); throw new ExternalException(); }));
            Assert(attempts == 1);
        });
        Check("OCR cancel during output retry closes without success notification", async () =>
        {
            int attempts = 0, notified = 0; CaptureSession? s = null;
            s = Session((_, _) => Task.FromResult("text"), (text, token) =>
                ImageOutput.CopyTextWithRetry(text, token, _ => { attempts++; s!.Close(); throw new ExternalException(); }),
                notify: (_, _) => notified++);
            await s.RecognizeText(); Assert(attempts == 1 && notified == 0);
        });
        Check("blank clipboard output never calls writer", async () =>
        { await ImageOutput.CopyTextWithRetry(" \r\n", default, _ => throw new Exception("unexpected write")); });
        Check("missing OCR model gives recoverable error", async () =>
        {
            using var missing = new OcrService(Path.Combine(AppContext.BaseDirectory, "missing-models"));
            await Throws<FileNotFoundException>(() => missing.Recognize(image, default));
        });
        using var ocr = new OcrService();
        Check("OCR cancelled before loading", async () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => ocr.Recognize(image, cancel.Token));
        });
        Check("real offline OCR Chinese English numbers punctuation and line breaks", async () =>
        {
            var sw = Stopwatch.StartNew(); string text = await ocr.Recognize(image, default);
            Console.WriteLine($"OCR cold: {sw.ElapsedMilliseconds} ms; synthetic result: {text.Replace(Environment.NewLine, " | ")}");
            Assert(text.Replace("\r", "") == "截图文字识别\nHello OCR 12345\nABC-2026: 98.50");
            sw.Restart(); string warm = await ocr.Recognize(image, default);
            Console.WriteLine($"OCR warm: {sw.ElapsedMilliseconds} ms; process working set: {Environment.WorkingSet / 1024 / 1024} MiB");
            Assert(warm == text);
        });
        Check("real OCR blank image returns no text", async () => Assert(string.IsNullOrWhiteSpace(await ocr.Recognize(Sample(true), default))));
        Check("real OCR single pixel is safe", async () => Assert(string.IsNullOrWhiteSpace(await ocr.Recognize(Sample(true, 1, 1), default))));
        Check("real OCR 4K selection retains Chinese text", async () =>
        { string text = await ocr.Recognize(Sample(false, 3840, 2160), default); Assert(text.Contains("截图文字识别") && text.Contains("12345")); });
        Check("real OCR active cancellation allows next run", async () =>
        {
            using var cancel = new CancellationTokenSource();
            var work = ocr.Recognize(Sample(false, 3840, 2160), cancel.Token); cancel.CancelAfter(10);
            await Throws<OperationCanceledException>(() => work);
            Assert((await ocr.Recognize(image, default)).Contains("截图文字识别"));
        });
        Check("disposed OCR service rejects new work", async () =>
        { using var disposed = new OcrService(); disposed.Dispose(); await Throws<ObjectDisposedException>(() => disposed.Recognize(image, default)); });
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("OCR assertion failed"); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    // Exercise continuations on a real STA dispatcher, as in the application.
    internal static void Pump(Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var frame = new DispatcherFrame(); Exception? failure = null;
        async void Run()
        {
            try { await action(); }
            catch (Exception ex) { failure = ex; }
            finally { frame.Continue = false; }
        }
        try { Run(); if (frame.Continue) Dispatcher.PushFrame(frame); if (failure != null) throw failure; }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
