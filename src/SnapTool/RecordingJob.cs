using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal sealed class RecordingJob(RecordingSize size, string? temporaryDirectory = null)
{
    private readonly Stopwatch clock = new();
    private volatile bool stop;
    private int frames;
    internal TimeSpan Elapsed => clock.Elapsed;
    internal int Frames => Volatile.Read(ref frames);
    internal string? PendingPath { get; private set; }
    internal void Stop() => stop = true;

    internal async Task Run(Func<CancellationToken, Task<SoftwareBitmap>> read, CancellationToken token)
    {
        string directory = temporaryDirectory ?? Path.Combine(Path.GetTempPath(), "SnapTool", "Recording");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "Recording-" + Guid.NewGuid().ToString("N") + ".gif");
        bool encoded = false;
        try
        {
            long previousTime = -1, nextDue = 0;
            await RecordingEncoder.Encode(temporary, size, async cancellation =>
            {
                if (stop) return null;
                if (frames > 0)
                {
                    var wait = TimeSpan.FromTicks(nextDue) - clock.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellation).ConfigureAwait(false);
                    if (stop) return null;
                }
                var image = await read(cancellation).ConfigureAwait(false);
                if (frames == 0) clock.Start();
                long time = frames == 0 ? 0 : Math.Max(previousTime + 1, clock.Elapsed.Ticks);
                nextDue = Math.Max(nextDue + TimeSpan.TicksPerSecond / 30, time);
                previousTime = time; Interlocked.Increment(ref frames);
                return new RecordingFrame(image, TimeSpan.FromTicks(time));
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Frames == 0) throw new InvalidOperationException("没有可保存的录屏画面。");
            encoded = true; PendingPath = temporary;
        }
        finally
        {
            clock.Stop();
            // Keep the completed GIF available for clipboard retries.
            if (!encoded && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // The clipboard references this file; do not delete it when the recording window closes.
    internal void ReleaseToClipboard() => PendingPath = null;

    internal void Discard()
    {
        if (PendingPath == null) return;
        File.Delete(PendingPath); PendingPath = null;
    }
}
