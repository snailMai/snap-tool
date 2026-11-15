using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SnapTool;

internal static class ImageOutput
{
    internal static Task CopyGif(string path, CancellationToken token) =>
        CopyGifWithRetry(path, token, data => Clipboard.SetDataObject(data, true));

    internal static async Task CopyGifWithRetry(string path, CancellationToken token, Action<DataObject> write)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("录屏临时文件不存在。", path);
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { Path.GetFullPath(path) });
        using var effect = new MemoryStream(BitConverter.GetBytes(1)); // DROPEFFECT_COPY, never move the cache.
        data.SetData("Preferred DropEffect", effect);
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { write(data); return; }
            catch (ExternalException) when (attempt < 3) { await Task.Delay(80, token); }
        }
    }

    internal static Task CopyText(string text, CancellationToken token) =>
        CopyTextWithRetry(text, token, value => Clipboard.SetText(value, TextDataFormat.UnicodeText));

    internal static async Task CopyTextWithRetry(string text, CancellationToken token, Action<string> write)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { write(text); return; }
            catch (ExternalException) when (attempt < 3) { await Task.Delay(80, token); }
        }
    }

    internal static async Task Copy(BitmapSource image, CancellationToken token = default)
    {
        var receipt = await VerifiedImageCopy.Run(image, token, (value, id) =>
            Clipboard.SetDataObject(ImageData(value, id), true), ImageMetadata,
            id => ReadImage(Clipboard.GetDataObject(), id), ImageCopyDiagnostics.Record);
        _ = VerifiedImageCopy.Observe(receipt, ImageMetadata, ImageCopyDiagnostics.Record);
    }

    internal static DataObject ImageData(BitmapSource image, string id)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData(VerifiedImageCopy.MarkerFormat, id, false);
        return data;
    }

    private static ClipboardImageMetadata ImageMetadata()
    {
        var formats = ClipboardImageFormats.None;
        if (Native.IsClipboardFormatAvailable(2)) formats |= ClipboardImageFormats.Bitmap;
        if (Native.IsClipboardFormatAvailable(8)) formats |= ClipboardImageFormats.Dib;
        if (Native.IsClipboardFormatAvailable(17)) formats |= ClipboardImageFormats.DibV5;
        var processes = Native.ClipboardProcesses();
        string? ownerName = ProcessName(processes.Owner);
        return new(Native.GetClipboardSequenceNumber(), formats, processes.Owner, ownerName,
            processes.Open, processes.Open == processes.Owner ? ownerName : ProcessName(processes.Open));
    }

    private static string? ProcessName(uint id)
    {
        if (id == 0 || id > int.MaxValue) return null;
        try { using var process = Process.GetProcessById((int)id); return process.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    internal static ClipboardImageReadback ReadImage(IDataObject? data, string id)
    {
        // Check ownership before requesting pixels, so an unrelated clipboard item is not inspected.
        object? marker = data?.GetData(VerifiedImageCopy.MarkerFormat, false);
        var result = new ClipboardImageReadback(false, false, MarkerPresent: marker != null,
            DataObjectType: data?.GetType().FullName, MarkerType: marker?.GetType().FullName);
        if (marker is not string value || value != id) return result;
        object? bitmap = data!.GetData(DataFormats.Bitmap, true);
        result = result with { Owned = true, ImageType = bitmap?.GetType().FullName };
        if (bitmap is not BitmapSource image) return result;
        int stride = Math.Max(1, (image.Format.BitsPerPixel + 7) / 8);
        var pixel = new byte[stride];
        image.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, stride, 0);
        image.CopyPixels(new Int32Rect(image.PixelWidth - 1, image.PixelHeight - 1, 1, 1), pixel, stride, 0);
        return result with { Readable = true, Width = image.PixelWidth, Height = image.PixelHeight };
    }

    internal static async Task CopyWithRetry(BitmapSource image, CancellationToken token, Action<BitmapSource> write)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { write(image); return; }
            catch (ExternalException) when (attempt < 3) { await Task.Delay(80, token); }
        }
    }

    internal static void SavePng(BitmapSource image, string path)
    {
        string fullPath = Path.GetFullPath(path);
        string temp = Path.Combine(Path.GetDirectoryName(fullPath)!, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
                stream.Flush(true);
            }
            File.Move(temp, fullPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
