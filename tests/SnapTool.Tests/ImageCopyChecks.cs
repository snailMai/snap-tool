using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class ImageCopyChecks
{
    private static void Assert(bool value) { if (!value) throw new Exception("Image copy assertion failed."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
            if (child is DependencyObject node)
            {
                yield return node;
                foreach (var nested in Children(node)) yield return nested;
            }
    }

    // No system clipboard, user images, keys, or screenshot capture are accessed by these checks.
    private sealed class Clipboard
    {
        internal uint Sequence = 7;
        internal int Writes, Reads;
        internal bool Readable = true;
        internal string? Id;
        internal BitmapSource? Image;
        internal readonly List<ImageCopyTrace> Trace = [];
        internal readonly List<int> Waits = [];
        internal ClipboardImageMetadata Metadata() => new(Sequence, Image == null ? ClipboardImageFormats.None : ClipboardImageFormats.Bitmap);
        internal void Write(BitmapSource image, string id) { Writes++; Sequence++; Image = image; Id = id; }
        internal ClipboardImageReadback Read(string id)
        {
            Reads++;
            return new(id == Id, Readable, Image?.PixelWidth ?? 0, Image?.PixelHeight ?? 0, Id != null,
                nameof(Clipboard), Id?.GetType().FullName, Image?.GetType().FullName);
        }
        internal Task Delay(int ms, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Waits.Add(ms); return Task.CompletedTask; }
        internal Task<ImageCopyReceipt> Copy(BitmapSource image, CancellationToken token = default) =>
            VerifiedImageCopy.Run(image, token, Write, Metadata, Read, Trace.Add, Delay);
    }

    internal static void Run(Action<string, Action> test)
    {
        test("image data object reads only the marked bitmap and preserves source pixels", () =>
        {
            var image = OcrChecks.Sample(true, 64, 96); string id = Guid.NewGuid().ToString("N");
            var data = ImageOutput.ImageData(image, id);
            Assert(!ImageOutput.ReadImage(null, id).Owned && !ImageOutput.ReadImage(data, "another-copy").Owned);
            var read = ImageOutput.ReadImage(data, id);
            Assert(read.Owned && read.Readable && read.Width == 64 && read.Height == 96);
            Assert(read.MarkerPresent && read.MarkerType == typeof(string).FullName && read.ImageType == image.GetType().FullName);
            Assert(ReferenceEquals(image, data.GetData(DataFormats.Bitmap)));
            var missing = new DataObject(); missing.SetData(VerifiedImageCopy.MarkerFormat, id, false);
            var absentImage = ImageOutput.ReadImage(missing, id);
            Assert(absentImage.Owned && absentImage.MarkerPresent && !absentImage.Readable && absentImage.ImageType == null);
        });
        test("image diagnostics distinguish absent markers wrong marker types and unsupported bitmap types", () =>
        {
            string id = Guid.NewGuid().ToString("N"); var data = new DataObject();
            var missing = ImageOutput.ReadImage(data, id);
            Assert(!missing.MarkerPresent && !missing.Owned && missing.DataObjectType == typeof(DataObject).FullName);
            data.SetData(VerifiedImageCopy.MarkerFormat, new byte[] { 1, 2, 3 }, false);
            var wrongType = ImageOutput.ReadImage(data, id);
            Assert(wrongType.MarkerPresent && !wrongType.Owned && wrongType.MarkerType == typeof(byte[]).FullName && wrongType.ImageType == null);
            data.SetData(VerifiedImageCopy.MarkerFormat, id, false); data.SetData(DataFormats.Bitmap, new object(), false);
            var bitmap = ImageOutput.ReadImage(data, id);
            Assert(bitmap.Owned && !bitmap.Readable && bitmap.ImageType == typeof(object).FullName);
        });
        test("image readback trace preserves process attribution and types without content values", () => OcrChecks.Pump(async () =>
        {
            var clipboard = new Clipboard { Readable = false };
            await Throws<InvalidOperationException>(() => VerifiedImageCopy.Run(OcrChecks.Sample(true, 64, 96), default,
                clipboard.Write, () => new(clipboard.Sequence, ClipboardImageFormats.Bitmap, 101, "SyntheticOwner", 202, "SyntheticLocker"),
                clipboard.Read, clipboard.Trace.Add, clipboard.Delay));
            var entry = clipboard.Trace.First(t => t.Stage == "readback");
            Assert(entry.OwnerProcessId == 101 && entry.OwnerProcessName == "SyntheticOwner" && entry.OpenProcessId == 202 && entry.OpenProcessName == "SyntheticLocker");
            Assert(entry.MarkerPresent == true && entry.MarkerMatches == true && entry.MarkerType == typeof(string).FullName);
            Assert(entry.ImageType != null && entry.ReadWidth == 64 && entry.ReadHeight == 96 && entry.Readable == false);
        }));
        test("verified image copy accepts a readable own image and repeated identical images", () => OcrChecks.Pump(async () =>
        {
            var image = OcrChecks.Sample(true, 64, 96); var clipboard = new Clipboard();
            var first = await clipboard.Copy(image); var second = await clipboard.Copy(image);
            Assert(first.Id != second.Id && first.Written != second.Written && clipboard.Writes == 2);
            Assert(clipboard.Trace.Count(t => t.Stage == "verified") == 2 && clipboard.Waits.Count == 0);
        }));
        test("verified image copy rejects a silent no-op writer instead of reporting success", () => OcrChecks.Pump(async () =>
        {
            var clipboard = new Clipboard(); var image = OcrChecks.Sample(true, 64, 96);
            // The old write-only helper accepts this failure: the API returned without copying anything.
            await ImageOutput.CopyWithRetry(image, default, _ => clipboard.Writes++);
            Assert(clipboard.Writes == 1 && clipboard.Reads == 0);
            await Throws<InvalidOperationException>(() => VerifiedImageCopy.Run(image, default,
                (_, _) => clipboard.Writes++, clipboard.Metadata, clipboard.Read, clipboard.Trace.Add, clipboard.Delay));
            Assert(clipboard.Writes == 2 && clipboard.Reads == 0 && clipboard.Trace.All(t => t.Stage != "verified"));
        }));
        test("verified image copy rejects unreadable pixels and wrong dimensions without rewriting", () => OcrChecks.Pump(async () =>
        {
            var image = OcrChecks.Sample(true, 64, 96);
            foreach (bool readable in new[] { false, true })
            {
                var clipboard = new Clipboard { Readable = readable };
                await Throws<InvalidOperationException>(() => VerifiedImageCopy.Run(image, default, clipboard.Write,
                    clipboard.Metadata, id => readable ? new(true, true, 63, 96) : clipboard.Read(id), clipboard.Trace.Add, clipboard.Delay));
                Assert(clipboard.Writes == 1 && clipboard.Waits.Count == 3 && clipboard.Trace.Last().Stage == "failed");
            }
        }));
        test("verified image copy requires both an own marker and an exported bitmap format", () => OcrChecks.Pump(async () =>
        {
            foreach (bool owned in new[] { false, true })
            {
                var clipboard = new Clipboard(); var image = OcrChecks.Sample(true, 64, 96);
                await Throws<InvalidOperationException>(() => VerifiedImageCopy.Run(image, default, clipboard.Write,
                    () => new(clipboard.Sequence, ClipboardImageFormats.None), _ => new(owned, true, 64, 96), clipboard.Trace.Add, clipboard.Delay));
                Assert(clipboard.Writes == 1 && clipboard.Trace.All(t => t.Stage != "verified"));
            }
        }));
        test("verified image copy retries busy reads without another write", () => OcrChecks.Pump(async () =>
        {
            var clipboard = new Clipboard(); int reads = 0;
            await VerifiedImageCopy.Run(OcrChecks.Sample(true, 64, 96), default, clipboard.Write, clipboard.Metadata,
                id => ++reads < 3 ? throw new ExternalException("synthetic busy") : clipboard.Read(id), clipboard.Trace.Add, clipboard.Delay);
            Assert(clipboard.Writes == 1 && reads == 3 && clipboard.Waits.SequenceEqual(new[] { 80, 80 }));
        }));
        test("verified image copy bounds writer retries and stops after a partial write", () => OcrChecks.Pump(async () =>
        {
            var image = OcrChecks.Sample(true, 64, 96); var clipboard = new Clipboard(); int calls = 0;
            await VerifiedImageCopy.Run(image, default, (value, id) =>
            { if (++calls < 4) throw new ExternalException(); clipboard.Write(value, id); },
                clipboard.Metadata, clipboard.Read, clipboard.Trace.Add, clipboard.Delay);
            Assert(calls == 4 && clipboard.Waits.Count == 3);
            clipboard = new(); calls = 0;
            await Throws<ExternalException>(() => VerifiedImageCopy.Run(image, default, (_, _) =>
            { calls++; clipboard.Sequence++; throw new ExternalException(); }, clipboard.Metadata, clipboard.Read, clipboard.Trace.Add, clipboard.Delay));
            Assert(calls == 1 && clipboard.Waits.Count == 0);
            clipboard = new(); calls = 0;
            await Throws<ExternalException>(() => VerifiedImageCopy.Run(image, default, (_, _) =>
            { calls++; throw new ExternalException(); }, clipboard.Metadata, clipboard.Read, clipboard.Trace.Add, clipboard.Delay));
            Assert(calls == 4 && clipboard.Waits.Count == 3);
        }));
        test("verified image copy never overwrites a clipboard replacement during verification or retry", () => OcrChecks.Pump(async () =>
        {
            var image = OcrChecks.Sample(true, 64, 96); var clipboard = new Clipboard();
            await Throws<InvalidOperationException>(() => VerifiedImageCopy.Run(image, default, clipboard.Write,
                clipboard.Metadata, id => { var result = clipboard.Read(id); clipboard.Sequence++; return result; }, clipboard.Trace.Add, clipboard.Delay));
            Assert(clipboard.Writes == 1 && clipboard.Trace.All(t => t.Stage != "verified"));
            clipboard = new(); int calls = 0;
            await Throws<InvalidOperationException>(() => VerifiedImageCopy.Run(image, default, (_, _) =>
            { calls++; throw new ExternalException(); }, clipboard.Metadata, clipboard.Read, clipboard.Trace.Add,
                (_, _) => { clipboard.Sequence++; return Task.CompletedTask; }));
            Assert(calls == 1);
        }));
        test("verified image copy cancellation suppresses writes or late success", () => OcrChecks.Pump(async () =>
        {
            var clipboard = new Clipboard(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => clipboard.Copy(OcrChecks.Sample(true, 64, 96), cancel.Token));
            Assert(clipboard.Writes == 0 && clipboard.Reads == 0);
            using var during = new CancellationTokenSource(); clipboard.Readable = false;
            await Throws<OperationCanceledException>(() => VerifiedImageCopy.Run(OcrChecks.Sample(true, 64, 96), during.Token,
                clipboard.Write, clipboard.Metadata, clipboard.Read, clipboard.Trace.Add,
                (_, token) => { during.Cancel(); return Task.FromCanceled(token); }));
            Assert(clipboard.Writes == 1 && clipboard.Reads == 1 && clipboard.Trace.Last().Stage == "cancelled");
        }));
        test("image follow-up is bounded metadata-only and stops on replacement", () => OcrChecks.Pump(async () =>
        {
            var clipboard = new Clipboard(); var receipt = await clipboard.Copy(OcrChecks.Sample(true, 64, 96)); int reads = clipboard.Reads;
            await VerifiedImageCopy.Observe(receipt, clipboard.Metadata, clipboard.Trace.Add, clipboard.Delay);
            Assert(clipboard.Waits.SequenceEqual(new[] { 100, 400, 1500 }) && clipboard.Reads == reads);
            Assert(clipboard.Trace.Count(t => t.Stage == "follow-up") == 3);
            clipboard.Trace.Clear(); clipboard.Waits.Clear();
            await VerifiedImageCopy.Observe(receipt, clipboard.Metadata, clipboard.Trace.Add,
                (ms, token) => { clipboard.Sequence++; return clipboard.Delay(ms, token); });
            Assert(clipboard.Waits.Count == 1 && clipboard.Reads == reads && clipboard.Trace.Single().Stage == "sequence-changed-after-copy");
        }));
        test("diagnostic IO failures do not turn a verified copy into failure", () => OcrChecks.Pump(async () =>
        {
            var clipboard = new Clipboard();
            await VerifiedImageCopy.Run(OcrChecks.Sample(true, 64, 96), default, clipboard.Write,
                clipboard.Metadata, clipboard.Read, _ => throw new IOException("synthetic log unavailable"), clipboard.Delay);
            Assert(clipboard.Writes == 1 && clipboard.Reads == 1);
        }));
        test("ordinary capture retains selection on verification failure and closes only after a successful retry", () => OcrChecks.Pump(async () =>
        {
            var bounds = new PixelRect(0, 0, 128, 96); var image = OcrChecks.Sample(true, bounds.Width, bounds.Height);
            var clipboard = new Clipboard { Readable = false }; int finished = 0;
            var session = new CaptureSession(new(bounds, [bounds], image), () => finished++, (_, _) => { },
                (_, _) => Task.FromResult(""), copyImage: async (source, token) => { await clipboard.Copy(source, token); });
            session.Selection.Select(bounds); var owner = new OverlayWindow(session, bounds);
            try
            {
                await session.Copy(owner);
                Assert(finished == 0 && !session.Busy && session.Selection.Rectangle == bounds && session.Status.Contains("复制失败"));
                var error = Children(owner).OfType<TextBlock>().Single(text => text.Text.StartsWith("复制失败", StringComparison.Ordinal));
                Assert(error.Visibility == Visibility.Visible && error.Foreground == Brushes.Firebrick && error.FontWeight == FontWeights.SemiBold);
                Assert(Children(owner).OfType<Button>().Single(button => (button.Tag as string) == "复制").IsEnabled);
                clipboard.Readable = true; await session.Copy(owner);
                Assert(finished == 1 && clipboard.Writes == 2);
            }
            finally { session.Close(); owner.Close(); }
        }));
        test("long capture retains stitched image on verification failure and permits retry", () => OcrChecks.Pump(async () =>
        {
            var bounds = new PixelRect(0, 0, 128, 96); var image = OcrChecks.Sample(true, bounds.Width, bounds.Height);
            var clipboard = new Clipboard { Readable = false };
            var window = new ScrollCaptureWindow(bounds, image, async (source, _, token) => { await clipboard.Copy(source, token); }, () => image);
            try
            {
                await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control);
                Assert(window.ImageHeight == bounds.Height && window.LivePreview != null && window.Status.Contains("复制失败"));
                var error = Children(window).OfType<TextBlock>().Single(text => text.Text.StartsWith("复制失败", StringComparison.Ordinal));
                Assert(error.Foreground == Brushes.Firebrick && error.Background == Brushes.MistyRose);
                Assert(Children(window).OfType<Button>().Single(button => (button.Content as string) == "完成").IsEnabled);
                clipboard.Readable = true; await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control);
                Assert(window.LivePreview == null && window.ImageHeight == 0 && clipboard.Writes == 2);
            }
            finally { window.Close(); }
        }));
        test("ordinary capture ignores duplicate copy and cancels a pending readback without late success", () => OcrChecks.Pump(async () =>
        {
            foreach (bool cancel in new[] { false, true })
            {
                var bounds = new PixelRect(0, 0, 128, 96); var image = OcrChecks.Sample(true, bounds.Width, bounds.Height);
                var clipboard = new Clipboard { Readable = false }; int finished = 0;
                var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
                var session = new CaptureSession(new(bounds, [bounds], image), () => finished++, (_, _) => { },
                    (_, _) => Task.FromResult(""), copyImage: async (source, token) =>
                    {
                        await VerifiedImageCopy.Run(source, token, clipboard.Write, clipboard.Metadata, clipboard.Read, clipboard.Trace.Add,
                            async (_, waiting) => { entered.TrySetResult(); await release.Task.WaitAsync(waiting); });
                    });
                session.Selection.Select(bounds); var owner = new OverlayWindow(session, bounds);
                try
                {
                    var pending = session.Copy(owner); await entered.Task;
                    await session.Copy(owner); Assert(clipboard.Writes == 1 && session.Busy);
                    if (cancel) session.Close(); else clipboard.Readable = true;
                    release.SetResult(); await pending;
                    Assert(finished == 1 && clipboard.Writes == 1 && !session.Busy);
                    Assert(clipboard.Trace.Last().Stage == (cancel ? "cancelled" : "verified"));
                }
                finally { session.Close(); owner.Close(); }
            }
        }));
        test("image diagnostics contain only declared metadata and rotate a bounded local log", () => OcrChecks.Pump(async () =>
        {
            var clipboard = new Clipboard(); await clipboard.Copy(OcrChecks.Sample(true, 64, 96));
            string directory = Path.Combine(Program.FindRoot(), ".tmp", "image-copy", "diagnostic-checks"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jsonl");
            ImageCopyDiagnostics.Append(clipboard.Trace.Last(), path);
            using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var names = json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
                var expected = new[] { "TimeUtc", "CopyId", "Stage", "Attempt", "ElapsedMs", "Width", "Height", "SequenceBefore",
                    "SequenceWritten", "SequenceCurrent", "Formats", "Readable", "ErrorCode", "OwnerProcessId", "OwnerProcessName",
                    "OpenProcessId", "OpenProcessName", "MarkerPresent", "MarkerMatches", "DataObjectType", "MarkerType", "ImageType",
                    "ReadWidth", "ReadHeight" }.OrderBy(n => n).ToArray();
                Assert(names.SequenceEqual(expected));
            }
            File.WriteAllText(path, new string(' ', 1024 * 1024));
            ImageCopyDiagnostics.Append(clipboard.Trace.Last(), path);
            Assert(new FileInfo(path).Length < 2048 && new FileInfo(path + ".previous").Length == 1024 * 1024);
            ImageCopyDiagnostics.Append(clipboard.Trace.Last(), directory); // A directory cannot be appended; failure is ignored.
        }));
    }
}
