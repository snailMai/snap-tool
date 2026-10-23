using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SnapTool;
using SnapTool.Core;
using Windows.Graphics.Imaging;

namespace SnapTool.Tests;

internal static class RecordingCleanupChecks
{
    private static void Assert(bool condition) { if (!condition) throw new Exception("Recording cleanup assertion failed."); }
    private static string NewDirectory()
    {
        string path = Path.Combine(Program.FindRoot(), ".tmp", "recording-cleanup-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static SoftwareBitmap Frame() => SoftwareBitmap.CreateCopyFromBuffer(new byte[32 * 32 * 4].AsBuffer(),
        BitmapPixelFormat.Bgra8, 32, 32, BitmapAlphaMode.Ignore);
    private static async Task<RecordingJob> CompletedJob(string directory)
    {
        var job = new RecordingJob(new RecordingSize(32, 32), directory);
        await job.Run(_ => { job.Stop(); return Task.FromResult(Frame()); }, default); return job;
    }
    internal static void Run(Action<string, Action> test)
    {
        test("recording discard tolerates a locked GIF and records cleanup for later", () => OcrChecks.Pump(async () =>
        {
            string directory = NewDirectory(); var job = await CompletedJob(directory); string path = job.PendingPath!;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                job.Discard();
                job.Discard();
                Assert(job.PendingPath == null && job.CleanupPath == path && job.CleanupRecorded && File.Exists(path) &&
                    File.ReadAllText(path + ".cleanup").Contains(Path.GetFileName(path)));
            }
            job.Discard(); Assert(job.CleanupPath == null && !job.CleanupRecorded && !File.Exists(path) && !File.Exists(path + ".cleanup"));
        }));
        test("read-only recording cleanup keeps unrelated files and retries after permission recovery", () => OcrChecks.Pump(async () =>
        {
            string directory = NewDirectory(); var job = await CompletedJob(directory); string path = job.PendingPath!;
            string sentinel = Path.Combine(directory, "existing.gif"); File.WriteAllText(sentinel, "keep");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try { job.Discard(); Assert(job.CleanupPath == path && job.CleanupRecorded && File.Exists(path)); }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
            job.Discard(); Assert(job.CleanupPath == null && !File.Exists(path) && File.ReadAllText(sentinel) == "keep");
        }));
        test("cleanup record write failure never prevents abandoning a locked recording", () => OcrChecks.Pump(async () =>
        {
            string directory = NewDirectory(); var job = await CompletedJob(directory); string path = job.PendingPath!;
            // A directory at the marker location deterministically denies creating a record file.
            Directory.CreateDirectory(path + ".cleanup");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                job.Discard(); Assert(job.PendingPath == null && job.CleanupPath == path && !job.CleanupRecorded && File.Exists(path));
            }
            Directory.Delete(path + ".cleanup");
            job.Discard(); Assert(job.CleanupPath == null && !File.Exists(path));
        }));
        test("partial recording cleanup preserves original capture failure and cancellation", () => OcrChecks.Pump(async () =>
        {
            foreach (bool cancel in new[] { false, true })
            {
                string directory = NewDirectory(); var job = new RecordingJob(new(32, 32), directory);
                using var cancellation = new CancellationTokenSource();
                var original = new IOException("synthetic capture failure"); Exception? failure = null; int reads = 0; string? path = null;
                try
                {
                    await job.Run(token =>
                    {
                        if (++reads == 2)
                        {
                            path = Directory.GetFiles(directory, "Recording-*.gif").Single(); File.SetAttributes(path, FileAttributes.ReadOnly);
                            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                            throw original;
                        }
                        return Task.FromResult(Frame());
                    }, cancellation.Token);
                }
                catch (Exception ex) { failure = ex; }
                try
                {
                    Assert(path != null && job.PendingPath == null && job.CleanupPath == path && job.CleanupRecorded);
                    Assert(cancel ? failure is OperationCanceledException : ReferenceEquals(failure, original));
                }
                finally { if (path != null) File.SetAttributes(path, FileAttributes.Normal); job.Discard(); }
                Assert(job.CleanupPath == null);
            }
        }));
        test("successfully copied GIF remains available and creates no cleanup record", () => OcrChecks.Pump(async () =>
        {
            string directory = NewDirectory(); var job = await CompletedJob(directory); string path = job.PendingPath!;
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            job.ReleaseToClipboard(); job.Discard();
            Assert(job.CleanupPath == null && !job.CleanupRecorded && File.Exists(path) && !File.Exists(path + ".cleanup"));
        }));
    }

    internal static async Task CheckWindows()
    {
        foreach (string action in new[] { "cancel", "close", "system" })
        {
            string directory = NewDirectory(); var screen = Native.Monitors()[0]; string? path = null;
            int cleanupWarnings = 0, copies = 0;
            var window = new RecordingWindow(new(screen.Left + 20, screen.Top + 20, screen.Left + 52, screen.Top + 52),
                (title, _) => { Assert(title == "临时录屏文件待清理"); cleanupWarnings++; }, _ => Task.FromResult(Frame()),
                (file, _) => { path = file; copies++; throw new IOException("synthetic clipboard busy"); }, directory);
            try
            {
                window.Start(); var clock = System.Diagnostics.Stopwatch.StartNew();
                while (window.Frames < 2 && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
                Assert(window.Frames >= 2); await window.Stop(); Assert(path != null && window.IsVisible && copies == 1);
                using (var locked = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (action == "close") window.Close();
                    else if (action == "system") await window.EndForSystemChange();
                    else await Task.WhenAll(window.Cancel(), window.Cancel());
                    clock.Restart(); while (window.IsVisible && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
                    await window.Cancel(); await window.Completion;
                    Assert(!window.IsVisible && cleanupWarnings == 1 && copies == 1 && File.Exists(path + ".cleanup"));
                    Assert(!Application.Current.Windows.OfType<RecordingBorder>().Any());
                }
            }
            finally
            {
                await window.Cancel();
                if (path != null) { File.Delete(path); File.Delete(path + ".cleanup"); }
            }
        }
        Console.WriteLine("PASS locked GIF cleanup: repeated cancel, window close and system exit close controls without copying or retaining borders.");
    }
}
