namespace SnapTool.Core;

public readonly record struct PixelPoint(int X, int Y);

// Right and Bottom are exclusive, matching bitmap crop coordinates.
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsEmpty => Width == 0 || Height == 0;
    public bool Contains(PixelPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    public PixelPoint Clamp(PixelPoint p) => new(Math.Clamp(p.X, Left, Right), Math.Clamp(p.Y, Top, Bottom));
    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(Left, other.Left), top = Math.Max(Top, other.Top);
        return new(left, top, Math.Max(left, Math.Min(Right, other.Right)), Math.Max(top, Math.Min(Bottom, other.Bottom)));
    }
    public static PixelRect Between(PixelPoint a, PixelPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    public static PixelRect Union(IEnumerable<PixelRect> rectangles)
    {
        var items = rectangles.Where(r => !r.IsEmpty).ToArray();
        if (items.Length == 0) throw new ArgumentException("At least one nonempty rectangle is required.");
        return new(items.Min(r => r.Left), items.Min(r => r.Top), items.Max(r => r.Right), items.Max(r => r.Bottom));
    }
}

[Flags]
public enum SelectionHit { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

public sealed class SelectionModel(PixelRect bounds)
{
    public PixelRect Bounds { get; } = !bounds.IsEmpty ? bounds : throw new ArgumentException("Bounds must be nonempty.");
    public PixelRect Rectangle { get; private set; }
    public bool Dragging { get; private set; }
    private PixelPoint anchor;
    private PixelRect original;
    private SelectionHit mode;

    public SelectionHit HitTest(PixelPoint p, int tolerance)
    {
        if (Rectangle.IsEmpty) return SelectionHit.None;
        var r = Rectangle;
        if (p.X < r.Left - tolerance || p.X > r.Right + tolerance || p.Y < r.Top - tolerance || p.Y > r.Bottom + tolerance)
            return SelectionHit.None;
        var hit = SelectionHit.None;
        if (Math.Abs(p.X - r.Left) <= tolerance) hit |= SelectionHit.Left;
        else if (Math.Abs(p.X - r.Right) <= tolerance) hit |= SelectionHit.Right;
        if (Math.Abs(p.Y - r.Top) <= tolerance) hit |= SelectionHit.Top;
        else if (Math.Abs(p.Y - r.Bottom) <= tolerance) hit |= SelectionHit.Bottom;
        return hit != SelectionHit.None ? hit : r.Contains(p) ? SelectionHit.Move : SelectionHit.None;
    }

    public void Begin(PixelPoint p, int tolerance)
    {
        anchor = Bounds.Clamp(p);
        original = Rectangle;
        mode = HitTest(anchor, tolerance);
        Dragging = true;
        if (mode == SelectionHit.None) Rectangle = PixelRect.Between(anchor, anchor);
    }

    public void Update(PixelPoint p)
    {
        if (!Dragging) return;
        p = Bounds.Clamp(p);
        if (mode == SelectionHit.None) Rectangle = PixelRect.Between(anchor, p);
        else if (mode == SelectionHit.Move)
        {
            int dx = Math.Clamp(p.X - anchor.X, Bounds.Left - original.Left, Bounds.Right - original.Right);
            int dy = Math.Clamp(p.Y - anchor.Y, Bounds.Top - original.Top, Bounds.Bottom - original.Bottom);
            Rectangle = new(original.Left + dx, original.Top + dy, original.Right + dx, original.Bottom + dy);
        }
        else
        {
            int left = mode.HasFlag(SelectionHit.Left) ? p.X : original.Left;
            int right = mode.HasFlag(SelectionHit.Right) ? p.X : original.Right;
            int top = mode.HasFlag(SelectionHit.Top) ? p.Y : original.Top;
            int bottom = mode.HasFlag(SelectionHit.Bottom) ? p.Y : original.Bottom;
            Rectangle = PixelRect.Between(new(left, top), new(right, bottom));
        }
    }

    public void End(PixelPoint p) { Update(p); Dragging = false; }
    public void StopDragging() => Dragging = false;
}
