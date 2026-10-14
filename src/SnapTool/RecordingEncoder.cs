using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal readonly record struct RecordingFrame(SoftwareBitmap Image, TimeSpan Time);

internal static class RecordingEncoder
{
    internal static async Task Encode(string path, RecordingSize size,
        Func<CancellationToken, Task<RecordingFrame?>> next, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var output = stream.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, output).AsTask(token).ConfigureAwait(false);
        await encoder.BitmapContainerProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            ["/appext/Application"] = new(Encoding.ASCII.GetBytes("NETSCAPE2.0"), PropertyType.UInt8Array),
            ["/appext/Data"] = new(new byte[] { 3, 1, 0, 0, 0 }, PropertyType.UInt8Array)
        }).AsTask(token).ConfigureAwait(false);
        byte[] pixels = new byte[checked(size.Width * size.Height * 4)];
        RecordingFrame? current = null, following = null;
        try
        {
            current = await next(token).ConfigureAwait(false);
            if (current == null) throw new InvalidOperationException("没有可保存的录屏画面。");
            long writtenDelay = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                following = await next(token).ConfigureAwait(false);
                var end = following?.Time ?? current.Value.Time + TimeSpan.FromSeconds(1.0 / 30);
                // GIF delays use centiseconds. Round cumulative time to avoid 30 fps drift.
                long desiredEnd = (long)Math.Round(end.TotalMilliseconds / 10);
                ushort delay = (ushort)Math.Clamp(desiredEnd - writtenDelay, 1, ushort.MaxValue);
                current.Value.Image.CopyToBuffer(pixels.AsBuffer());
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    (uint)size.Width, (uint)size.Height, 96, 96, pixels);
                await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
                {
                    ["/grctlext/Delay"] = new(delay, PropertyType.UInt16),
                    ["/grctlext/Disposal"] = new((byte)1, PropertyType.UInt8)
                }).AsTask(token).ConfigureAwait(false);
                writtenDelay += delay;
                current.Value.Image.Dispose(); current = null;
                if (following == null) break;
                await encoder.GoToNextFrameAsync().AsTask(token).ConfigureAwait(false);
                current = following; following = null;
            }
            await encoder.FlushAsync().AsTask(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            stream.Flush(true);
        }
        finally { current?.Image.Dispose(); following?.Image.Dispose(); }
    }
}
