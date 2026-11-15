using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SnapTool;

[Flags]
internal enum ClipboardImageFormats { None = 0, Bitmap = 1, Dib = 2, DibV5 = 4 }
internal readonly record struct ClipboardImageMetadata(uint Sequence, ClipboardImageFormats Formats,
    uint OwnerProcessId = 0, string? OwnerProcessName = null, uint OpenProcessId = 0, string? OpenProcessName = null);
internal readonly record struct ClipboardImageReadback(bool Owned, bool Readable, int Width = 0, int Height = 0,
    bool MarkerPresent = false, string? DataObjectType = null, string? MarkerType = null, string? ImageType = null);
internal sealed record ImageCopyReceipt(string Id, int Width, int Height, long Started, uint Before, uint Written = 0, int Attempt = 0);
internal sealed record ImageCopyTrace(DateTimeOffset TimeUtc, string CopyId, string Stage, int Attempt,
    double ElapsedMs, int Width, int Height, uint SequenceBefore, uint SequenceWritten, uint SequenceCurrent,
    ClipboardImageFormats Formats, bool? Readable, int? ErrorCode,
    uint OwnerProcessId, string? OwnerProcessName, uint OpenProcessId, string? OpenProcessName,
    bool? MarkerPresent, bool? MarkerMatches, string? DataObjectType, string? MarkerType, string? ImageType,
    int? ReadWidth, int? ReadHeight);

internal static class VerifiedImageCopy
{
    internal const string MarkerFormat = "SnailMai.SnapTool.ImageCopyId";

    internal static async Task<ImageCopyReceipt> Run(BitmapSource image, CancellationToken token,
        Action<BitmapSource, string> write, Func<ClipboardImageMetadata> metadata,
        Func<string, ClipboardImageReadback> read, Action<ImageCopyTrace> trace,
        Func<int, CancellationToken, Task>? delay = null)
    {
        token.ThrowIfCancellationRequested();
        delay ??= Task.Delay;
        var receipt = new ImageCopyReceipt(Guid.NewGuid().ToString("N"), image.PixelWidth, image.PixelHeight,
            Stopwatch.GetTimestamp(), metadata().Sequence);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                receipt = receipt with { Attempt = attempt };
                var before = metadata();
                if (before.Sequence != receipt.Before)
                    throw new InvalidOperationException("剪贴板状态已变化，未再次写入，请重新复制。");
                Report(trace, receipt, "write-start", before);
                try { write(image, receipt.Id); break; }
                catch (ExternalException ex)
                {
                    var failed = metadata();
                    Report(trace, receipt, "write-error", failed, error: ex.HResult);
                    // A partial write or another copy must not be overwritten by an automatic retry.
                    if (attempt == 4 || failed.Sequence != receipt.Before) throw;
                    await delay(80, token);
                }
            }

            var written = metadata();
            receipt = receipt with { Written = written.Sequence };
            Report(trace, receipt, "write-returned", written);
            if (written.Sequence == 0 || written.Sequence == receipt.Before)
                throw new InvalidOperationException("未能确认剪贴板已更新，截图已保留，请重新复制。");

            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var before = metadata();
                if (before.Sequence != receipt.Written)
                    throw new InvalidOperationException("复制后剪贴板状态发生变化，截图已保留，请重新复制。");
                try
                {
                    var result = read(receipt.Id);
                    var after = metadata();
                    Report(trace, receipt, "readback", after, result.Readable, readback: result);
                    if (after.Sequence != receipt.Written || !result.Owned)
                        throw new InvalidOperationException("未能确认剪贴板中是本次图片，截图已保留，请重新复制。");
                    if (result.Readable && result.Width == image.PixelWidth && result.Height == image.PixelHeight &&
                        after.Formats != ClipboardImageFormats.None)
                    {
                        token.ThrowIfCancellationRequested();
                        Report(trace, receipt, "verified", after, true);
                        return receipt;
                    }
                    if (attempt == 3)
                        throw new InvalidOperationException("写入后未能读回完整尺寸的图片，截图已保留，请重新复制。");
                }
                catch (ExternalException ex)
                {
                    Report(trace, receipt, "read-error", metadata(), error: ex.HResult);
                    if (attempt == 3) throw;
                }
                await delay(80, token);
            }
        }
        catch (Exception ex)
        {
            Report(trace, receipt, ex is OperationCanceledException ? "cancelled" : "failed", metadata(), error: ex.HResult);
            throw;
        }
    }

    // Only metadata is observed after returning. No bitmap, text, or data object is retained.
    internal static async Task Observe(ImageCopyReceipt receipt, Func<ClipboardImageMetadata> metadata,
        Action<ImageCopyTrace> trace, Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        try
        {
            foreach (int milliseconds in new[] { 100, 400, 1500 })
            {
                await delay(milliseconds, CancellationToken.None);
                var current = metadata();
                bool changed = current.Sequence != receipt.Written;
                Report(trace, receipt, changed ? "sequence-changed-after-copy" : "follow-up", current);
                if (changed) return;
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or OperationCanceledException)
        { Report(trace, receipt, "follow-up-unavailable", default, error: ex.HResult); }
    }

    private static void Report(Action<ImageCopyTrace> trace, ImageCopyReceipt receipt, string stage,
        ClipboardImageMetadata current, bool? readable = null, int? error = null, ClipboardImageReadback? readback = null)
    {
        try
        {
            trace(new(DateTimeOffset.UtcNow, receipt.Id, stage, receipt.Attempt,
                Math.Round(Stopwatch.GetElapsedTime(receipt.Started).TotalMilliseconds, 2), receipt.Width, receipt.Height,
                receipt.Before, receipt.Written, current.Sequence, current.Formats, readable, error,
                current.OwnerProcessId, current.OwnerProcessName, current.OpenProcessId, current.OpenProcessName,
                readback?.MarkerPresent, readback?.Owned, readback?.DataObjectType, readback?.MarkerType, readback?.ImageType,
                readback?.Width, readback?.Height));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}

internal static class ImageCopyDiagnostics
{
    private static readonly object gate = new();
    internal static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapTool", "logs", "image-copy.jsonl");
    internal static void Record(ImageCopyTrace entry) => Append(entry, Path);
    internal static void Append(ImageCopyTrace entry, string path)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
