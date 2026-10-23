using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SkiaSharp;
using SnapTool.Core;

namespace SnapTool;

// One CPU engine per application. Serialize runs, including runs from cancelled sessions.
internal sealed class OcrService : IDisposable
{
    private readonly string directory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Timer idleTimer;
    private readonly TimeSpan idleTimeout;
    private long lastUsed;
    private RapidOcr? engine;
    private int disposed;

    internal OcrService(string? modelDirectory = null, TimeSpan? idleTimeout = null)
    {
        directory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "v5");
        this.idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        if (this.idleTimeout < TimeSpan.FromMilliseconds(1) || this.idleTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        idleTimer = new Timer(ReleaseIdleEngine, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal bool IsLoaded => Volatile.Read(ref engine) != null;

    internal async Task<string> Recognize(BitmapSource image, CancellationToken token) =>
        (await Detect(image, token, false)).StrRes.TrimEnd('\r', '\n');

    internal async Task<OcrTextLayout> RecognizeLayout(BitmapSource image, CancellationToken token) =>
        CreateLayout(await Detect(image, token, true), image.PixelWidth, image.PixelHeight);

    private Task<OcrResult> Detect(BitmapSource image, CancellationToken token, bool locate)
    {
        if (!image.IsFrozen) throw new ArgumentException("OCR 输入必须为冻结的截图。", nameof(image));
        return Task.Run(async () =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                token.ThrowIfCancellationRequested();
                idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                EnsureEngine();
                token.ThrowIfCancellationRequested();
                var converted = new FormatConvertedBitmap(image, locate ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, 0);
                using var bitmap = new SKBitmap(image.PixelWidth, image.PixelHeight, SKColorType.Bgra8888, locate ? SKAlphaType.Premul : SKAlphaType.Opaque);
                converted.CopyPixels(System.Windows.Int32Rect.Empty, bitmap.GetPixels(), checked(bitmap.RowBytes * bitmap.Height), bitmap.RowBytes);
                if (locate) { using var canvas = new SKCanvas(bitmap); canvas.DrawColor(SKColors.White, SKBlendMode.DstOver); }
                // Preserve screenshot detail up to 2048 px; recognition crops use the original image.
                var result = engine!.Detect(bitmap, RapidOcrOptions.Default with { ImgResize = 2048, ReturnWordBox = locate, ReturnSingleCharBox = locate }, token);
                token.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                if (Volatile.Read(ref disposed) == 0 && engine != null)
                {
                    lastUsed = Stopwatch.GetTimestamp();
                    idleTimer.Change(idleTimeout, Timeout.InfiniteTimeSpan);
                }
                gate.Release();
            }
        }, token);
    }

    internal static OcrTextLayout CreateLayout(OcrResult result, int width, int height)
    {
        var text = new StringBuilder(); var glyphs = new List<OcrGlyph>();
        foreach (var block in result.TextBlocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            if (text.Length > 0) text.Append('\n');
            int start = text.Length, cursor = 0; text.Append(block.Text);
            foreach (var word in block.WordResults ?? [])
            {
                if (string.IsNullOrEmpty(word.Text)) continue;
                int offset = block.Text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
                if (offset < 0) continue;
                cursor = offset + word.Text.Length;
                if (word.BoxPoints.Length != 4) continue;
                var corners = word.BoxPoints.Select(p => new PixelPoint(Math.Clamp(p.X, 0, width), Math.Clamp(p.Y, 0, height))).ToArray();
                var glyph = new OcrGlyph(start + offset, word.Text.Length, corners);
                if (!glyph.Bounds.IsEmpty) glyphs.Add(glyph);
            }
        }
        return new(text.ToString(), glyphs.ToArray());
    }

    private void ReleaseIdleEngine(object? state)
    {
        if (!gate.Wait(0)) return;
        try
        {
            if (Volatile.Read(ref disposed) != 0 || engine == null) return;
            var remaining = idleTimeout - Stopwatch.GetElapsedTime(lastUsed);
            if (remaining > TimeSpan.Zero)
            {
                idleTimer.Change(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds))), Timeout.InfiniteTimeSpan);
                return;
            }
            engine.Dispose(); engine = null;
        }
        finally { gate.Release(); }
    }

    private void EnsureEngine()
    {
        if (engine != null) return;
        string Model(string name)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException("OCR 模型缺失，请保留完整的 models 文件夹。", path);
            return path;
        }
        string det = Model("ch_PP-OCRv5_det_mobile.onnx"), cls = Model("ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            rec = Model("ch_PP-OCRv5_rec_mobile.onnx"), dict = Model("ppocrv5_dict.txt");
        var candidate = new RapidOcr();
        try
        {
            using var options = RapidOcr.GetDefaultSessionOptions(Math.Min(4, Environment.ProcessorCount));
            // Desktop-sized inputs vary; retaining the CPU arena keeps the largest run's buffers resident.
            options.EnableCpuMemArena = false;
            candidate.InitModels(det, cls, rec, dict, options);
            engine = candidate;
        }
        catch { candidate.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gate.Wait();
        try { idleTimer.Dispose(); engine?.Dispose(); engine = null; }
        finally { gate.Release(); }
    }
}
