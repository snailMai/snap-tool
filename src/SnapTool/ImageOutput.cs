using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SnapTool;

internal static class ImageOutput
{
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

    internal static Task Copy(BitmapSource image, CancellationToken token = default) =>
        CopyWithRetry(image, token, value =>
        {
            var data = new DataObject();
            data.SetImage(value);
            Clipboard.SetDataObject(data, true);
        });

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
