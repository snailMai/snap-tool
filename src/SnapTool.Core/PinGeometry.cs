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
    public void Move(PixelPoint origin, PixelRect workArea, IReadOnlyList<PixelRect>? workAreas = null) => Place(origin, workArea, workAreas);
    public void Zoom(double factor, PixelRect workArea, IReadOnlyList<PixelRect>? workAreas = null)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        Resize(Scale * factor, workArea, workAreas);
    }
    public void Reset(PixelRect workArea, IReadOnlyList<PixelRect>? workAreas = null) => Resize(1, workArea, workAreas);
    private void Resize(double scale, PixelRect workArea, IReadOnlyList<PixelRect>? workAreas)
    {
        var center = new PixelPoint(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);
        double minimum = Math.Min(0.1, Math.Min(workArea.Width * 0.9 / ImageWidth, workArea.Height * 0.9 / ImageHeight));
        Scale = Math.Clamp(scale, minimum, 4);
        Place(new(center.X - Width / 2, center.Y - Height / 2), workArea, workAreas);
    }
    private int Width => Math.Max(1, (int)Math.Round(ImageWidth * Scale));
    private int Height => Math.Max(1, (int)Math.Round(ImageHeight * Scale));
    private void Place(PixelPoint origin, PixelRect workArea, IReadOnlyList<PixelRect>? workAreas = null)
    {
        var areas = workAreas ?? new[] { workArea };
        if (areas.Count == 0 || areas.Any(area => area.IsEmpty)) throw new ArgumentException("Work areas must be nonempty.");
        var proposed = Constrain(origin, PixelRect.Union(areas));
        // A desktop bounding rectangle can include gaps with no monitor. Keep an operable patch on a real display.
        if (areas.Any(area =>
        {
            var visible = proposed.Intersect(area);
            return visible.Width >= Math.Min(40, Math.Min(Width, area.Width)) &&
                visible.Height >= Math.Min(40, Math.Min(Height, area.Height));
        })) { Bounds = proposed; return; }

        double nearestDistance = double.PositiveInfinity;
        foreach (var area in areas)
        {
            var candidate = Constrain(new(proposed.Left, proposed.Top), area);
            double dx = (double)candidate.Left - proposed.Left, dy = (double)candidate.Top - proposed.Top;
            double distance = dx * dx + dy * dy;
            if (distance < nearestDistance) { Bounds = candidate; nearestDistance = distance; }
        }
    }
    private PixelRect Constrain(PixelPoint origin, PixelRect workArea)
    {
        if (workArea.IsEmpty) throw new ArgumentException("Work area is empty.");
        // Large 100% images may extend offscreen, but retain a reachable portion.
        int x = Width <= workArea.Width ? Math.Clamp(origin.X, workArea.Left, workArea.Right - Width)
            : Math.Clamp(origin.X, workArea.Left - Width + 40, workArea.Right - 40);
        int y = Height <= workArea.Height ? Math.Clamp(origin.Y, workArea.Top, workArea.Bottom - Height)
            : Math.Clamp(origin.Y, workArea.Top - Height + 40, workArea.Bottom - 40);
        return new(x, y, x + Width, y + Height);
    }
}
