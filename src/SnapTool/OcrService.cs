using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SkiaSharp;

namespace SnapTool;

// One CPU engine per application. Serialize runs, including runs from cancelled sessions.
internal sealed class OcrService(string? modelDirectory = null) : IDisposable
{
    private readonly string directory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "v5");
    private readonly SemaphoreSlim gate = new(1, 1);
    private RapidOcr? engine;
    private int disposed;

    internal Task<string> Recognize(BitmapSource image, CancellationToken token)
    {
        if (!image.IsFrozen) throw new ArgumentException("OCR 输入必须为冻结的截图。", nameof(image));
        return Task.Run(async () =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                token.ThrowIfCancellationRequested();
                EnsureEngine();
                token.ThrowIfCancellationRequested();
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
                using var bitmap = new SKBitmap(image.PixelWidth, image.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
                converted.CopyPixels(System.Windows.Int32Rect.Empty, bitmap.GetPixels(), checked(bitmap.RowBytes * bitmap.Height), bitmap.RowBytes);
                // Preserve screenshot detail up to 2048 px; recognition crops use the original image.
                var result = engine!.Detect(bitmap, RapidOcrOptions.Default with { ImgResize = 2048 }, token);
                token.ThrowIfCancellationRequested();
                return result.StrRes.TrimEnd('\r', '\n');
            }
            finally { gate.Release(); }
        }, token);
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
            candidate.InitModels(det, cls, rec, dict, numThread: Math.Min(4, Environment.ProcessorCount));
            engine = candidate;
        }
        catch { candidate.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gate.Wait();
        try { engine?.Dispose(); engine = null; }
        finally { gate.Release(); }
    }
}
