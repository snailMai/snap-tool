namespace SnapTool.Core;

public readonly record struct RecordingSize(int Width, int Height)
{
    public static RecordingSize Fit(PixelRect region)
    {
        if (region.Width < 16 || region.Height < 16) throw new ArgumentException("录屏区域至少需要 16 × 16 px。");
        double scale = Math.Min(1, Math.Min(1920.0 / region.Width, 1080.0 / region.Height));
        return new(Math.Max(1, (int)(region.Width * scale)), Math.Max(1, (int)(region.Height * scale)));
    }
}
