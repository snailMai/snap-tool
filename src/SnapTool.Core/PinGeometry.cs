namespace SnapTool.Core;

public sealed class PinGeometry
{
    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public double Scale { get; private set; }
    public PixelRect Bounds { get; private set; }
    public PinGeometry(int width, int height, PixelPoint origin, PixelRect workArea)
    {
        if (width <= 0 || height <= 0 || workArea.IsEmpty) throw new ArgumentException("Invalid pin dimensions.");
        ImageWidth = width; ImageHeight = height;
        Scale = Math.Min(1, Math.Min(workArea.Width * 0.9 / width, workArea.Height * 0.9 / height));
        Place(origin, workArea);
    }
    public void Move(PixelPoint origin, PixelRect workArea) => Place(origin, workArea);
    public void Zoom(double factor, PixelRect workArea)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        Resize(Scale * factor, workArea);
    }
    public void Reset(PixelRect workArea) => Resize(1, workArea);
    private void Resize(double scale, PixelRect workArea)
    {
        var center = new PixelPoint(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);
        double minimum = Math.Min(0.1, Math.Min(workArea.Width * 0.9 / ImageWidth, workArea.Height * 0.9 / ImageHeight));
        Scale = Math.Clamp(scale, minimum, 4);
        Place(new(center.X - Width / 2, center.Y - Height / 2), workArea);
    }
    private int Width => Math.Max(1, (int)Math.Round(ImageWidth * Scale));
    private int Height => Math.Max(1, (int)Math.Round(ImageHeight * Scale));
    private void Place(PixelPoint origin, PixelRect workArea)
    {
        if (workArea.IsEmpty) throw new ArgumentException("Work area is empty.");
        // Large 100% images may extend offscreen, but retain a reachable portion.
        int x = Width <= workArea.Width ? Math.Clamp(origin.X, workArea.Left, workArea.Right - Width)
            : Math.Clamp(origin.X, workArea.Left - Width + 40, workArea.Right - 40);
        int y = Height <= workArea.Height ? Math.Clamp(origin.Y, workArea.Top, workArea.Bottom - Height)
            : Math.Clamp(origin.Y, workArea.Top - Height + 40, workArea.Bottom - 40);
        Bounds = new(x, y, x + Width, y + Height);
    }
}
