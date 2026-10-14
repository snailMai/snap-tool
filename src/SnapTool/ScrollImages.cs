using System.Threading;
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

    internal static ScrollFrame Frame(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        var pixels = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
        converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return new(image.PixelWidth, image.PixelHeight, pixels);
    }

    internal static BitmapSource Compose(ScrollStitcher stitcher, CancellationToken token)
    {
        var pixels = stitcher.Compose(token);
        var image = BitmapSource.Create(stitcher.Width, stitcher.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stitcher.Width * 4);
        image.Freeze(); return image;
    }
}
