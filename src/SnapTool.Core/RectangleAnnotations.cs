namespace SnapTool.Core;

public sealed record RectangleAnnotation(PixelRect Rectangle, uint Rgb, int Thickness);

// Coordinates remain anchored to the frozen desktop when the capture region changes.
public sealed class RectangleAnnotations
{
    private readonly List<RectangleAnnotation> items = [];
    private readonly Stack<RectangleAnnotation[]> history = new();
    private RectangleAnnotation[]? before;
    private PixelPoint anchor;
    private PixelRect original, bounds;
    private SelectionHit operation;
    public IReadOnlyList<RectangleAnnotation> Items => items;
    public int SelectedIndex { get; private set; } = -1;
    public RectangleAnnotation? Selected => SelectedIndex >= 0 ? items[SelectedIndex] : null;
    public bool Dragging => before != null;
    public bool CanUndo => Dragging || history.Count > 0;
    public uint Rgb { get; private set; } = 0xFF4545;
    public int Thickness { get; private set; } = 3;

    public void Deselect() { if (!Dragging) SelectedIndex = -1; }

    public static IEnumerable<(PixelPoint Point, SelectionHit Hit)> Handles(PixelRect r)
    {
        yield return (new(r.Left, r.Top), SelectionHit.Left | SelectionHit.Top);
        yield return (new((r.Left + r.Right) / 2, r.Top), SelectionHit.Top);
        yield return (new(r.Right, r.Top), SelectionHit.Right | SelectionHit.Top);
        yield return (new(r.Left, (r.Top + r.Bottom) / 2), SelectionHit.Left);
        yield return (new(r.Right, (r.Top + r.Bottom) / 2), SelectionHit.Right);
        yield return (new(r.Left, r.Bottom), SelectionHit.Left | SelectionHit.Bottom);
        yield return (new((r.Left + r.Right) / 2, r.Bottom), SelectionHit.Bottom);
        yield return (new(r.Right, r.Bottom), SelectionHit.Right | SelectionHit.Bottom);
    }

    public (int Index, SelectionHit Hit) HitTest(PixelPoint p, int tolerance)
    {
        if (Selected is { } selected)
            foreach (var handle in Handles(selected.Rectangle))
                if (Math.Abs(p.X - handle.Point.X) <= tolerance && Math.Abs(p.Y - handle.Point.Y) <= tolerance)
                    return (SelectedIndex, handle.Hit);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var r = items[i].Rectangle;
            int hitWidth = Math.Max(tolerance, items[i].Thickness);
            if (p.X < r.Left - hitWidth || p.X > r.Right + hitWidth || p.Y < r.Top - hitWidth || p.Y > r.Bottom + hitWidth) continue;
            if (Math.Abs(p.X - r.Left) <= hitWidth || Math.Abs(p.X - r.Right) <= hitWidth ||
                Math.Abs(p.Y - r.Top) <= hitWidth || Math.Abs(p.Y - r.Bottom) <= hitWidth)
                return (i, SelectionHit.Move);
        }
        return (-1, SelectionHit.None);
    }

    public void Begin(PixelPoint p, PixelRect region, int tolerance)
    {
        if (Dragging || region.IsEmpty || !region.Contains(p)) return;
        before = items.ToArray(); anchor = p; bounds = region;
        (SelectedIndex, operation) = HitTest(p, tolerance);
        if (SelectedIndex < 0)
        {
            SelectedIndex = items.Count;
            items.Add(new(PixelRect.Between(p, p), Rgb, Thickness));
        }
        original = items[SelectedIndex].Rectangle;
        Rgb = items[SelectedIndex].Rgb; Thickness = items[SelectedIndex].Thickness;
    }

    public void Update(PixelPoint p, bool square = false)
    {
        if (!Dragging) return;
        p = bounds.Clamp(p);
        PixelRect r;
        if (operation == SelectionHit.None)
        {
            if (square)
            {
                int dx = p.X - anchor.X, dy = p.Y - anchor.Y;
                int length = Math.Min(Math.Abs(dx), Math.Abs(dy));
                p = new(anchor.X + Math.Sign(dx) * length, anchor.Y + Math.Sign(dy) * length);
            }
            r = PixelRect.Between(anchor, p);
        }
        else if (operation == SelectionHit.Move)
        {
            // Partially cropped shapes retain their geometry until explicitly moved.
            int dx = Math.Clamp(p.X - anchor.X, Math.Min(0, bounds.Left - original.Left), Math.Max(0, bounds.Right - original.Right));
            int dy = Math.Clamp(p.Y - anchor.Y, Math.Min(0, bounds.Top - original.Top), Math.Max(0, bounds.Bottom - original.Bottom));
            r = new(original.Left + dx, original.Top + dy, original.Right + dx, original.Bottom + dy);
        }
        else
        {
            r = PixelRect.Between(new(operation.HasFlag(SelectionHit.Left) ? p.X : original.Left,
                    operation.HasFlag(SelectionHit.Top) ? p.Y : original.Top),
                new(operation.HasFlag(SelectionHit.Right) ? p.X : original.Right,
                    operation.HasFlag(SelectionHit.Bottom) ? p.Y : original.Bottom));
        }
        items[SelectedIndex] = items[SelectedIndex] with { Rectangle = r };
    }

    public void End(PixelPoint p, bool square = false)
    {
        if (before == null) return;
        Update(p, square);
        var saved = before; before = null;
        if (Selected!.Rectangle.Width < 2 || Selected.Rectangle.Height < 2)
        { Restore(saved); return; }
        if (!items.SequenceEqual(saved)) history.Push(saved);
    }

    public void SetStyle(uint rgb, int thickness)
    {
        if (rgb > 0xFFFFFF || thickness < 1 || thickness > 12) throw new ArgumentOutOfRangeException(nameof(thickness));
        if (Dragging) return;
        Rgb = rgb; Thickness = thickness;
        if (Selected is not { } selected || (selected.Rgb == rgb && selected.Thickness == thickness)) return;
        history.Push(items.ToArray()); items[SelectedIndex] = selected with { Rgb = rgb, Thickness = thickness };
    }

    public void Delete()
    {
        if (Dragging || SelectedIndex < 0) return;
        history.Push(items.ToArray()); items.RemoveAt(SelectedIndex); SelectedIndex = -1;
    }

    public void Undo()
    {
        if (before != null) { var saved = before; before = null; Restore(saved); }
        else if (history.TryPop(out var saved)) Restore(saved);
    }

    private void Restore(RectangleAnnotation[] saved)
    { items.Clear(); items.AddRange(saved); SelectedIndex = -1; }
}
