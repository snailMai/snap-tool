using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal static class ScrollImages
{
    internal static BitmapSource Preview(ScrollStitcher stitcher, CancellationToken token = default)
    {
        var preview = stitcher.Preview(360, 720, token);
        var image = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgr32, null, preview.Pixels, preview.Width * 4);
        image.Freeze(); return image;
    }

    internal static ScrollFrame Frame(BitmapSource image, byte[]? reusable = null)
    {
        BitmapSource converted = image.Format == PixelFormats.Bgr32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        int length = checked(image.PixelWidth * image.PixelHeight * 4);
        var pixels = reusable?.Length == length ? reusable : new byte[length];
        converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return ScrollFrame.FromOwnedPixels(image.PixelWidth, image.PixelHeight, pixels);
    }

    internal static BitmapSource Compose(ScrollStitcher stitcher, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int stride = checked(stitcher.Width * 4);
        using var section = MemoryMappedFile.CreateNew(null, checked((long)stride * stitcher.Height));
        using (var view = section.CreateViewAccessor())
        {
            stitcher.CopyChunks((row, pixels) => view.WriteArray((long)row * stride, pixels, 0, pixels.Length), token);
        }
        token.ThrowIfCancellationRequested();
        // WIC owns its read-only mapped view. The section handle can close after creation;
        // no writer survives, no image file is created, and there is no double bitmap buffer.
        var image = Imaging.CreateBitmapSourceFromMemorySection(section.SafeMemoryMappedFileHandle.DangerousGetHandle(),
            stitcher.Width, stitcher.Height, PixelFormats.Bgr32, stride, 0);
        image.Freeze(); return image;
    }
}
