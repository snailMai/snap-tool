namespace SnapTool.Core;

public enum AnnotationKind { Rectangle, Line, Arrow }

public sealed record Annotation(AnnotationKind Kind, PixelPoint Start, PixelPoint End, uint Rgb, int Thickness)
{
    public Annotation(PixelRect rectangle, uint rgb, int thickness)
        : this(AnnotationKind.Rectangle, new(rectangle.Left, rectangle.Top), new(rectangle.Right, rectangle.Bottom), rgb, thickness) { }
    public PixelRect Rectangle => PixelRect.Between(Start, End);
    public double Length => Math.Sqrt(Math.Pow(End.X - Start.X, 2) + Math.Pow(End.Y - Start.Y, 2));

    public (double X, double Y)[] ArrowOutline()
    {
        double length = Length;
        if (length == 0) return [];
        double size = Math.Min(Math.Max(12, Thickness * 5), length / 2);
        double ux = (End.X - Start.X) / length, uy = (End.Y - Start.Y) / length;
        (double X, double Y) Point(double back, double side) => (End.X - ux * back - uy * side, End.Y - uy * back + ux * side);
        // A pointed tail, widening shaft and inset neck form one filled outline at every angle.
        return [(Start.X, Start.Y), Point(size * 0.75, size * 0.2), Point(size, size * 0.5),
            (End.X, End.Y), Point(size, -size * 0.5), Point(size * 0.75, -size * 0.2)];
    }
}

// Coordinates remain anchored to the frozen desktop when the capture region changes.
public sealed class Annotations
{
    private readonly List<Annotation> items = [];
    private readonly Stack<Annotation[]> history = new();
    private Annotation[]? before;
    private Annotation originalMark = null!;
    private PixelPoint anchor;
    private PixelRect original, bounds;
    private SelectionHit operation;
    public IReadOnlyList<Annotation> Items => items;
    public int SelectedIndex { get; private set; } = -1;
    public Annotation? Selected => SelectedIndex >= 0 ? items[SelectedIndex] : null;
    public bool Dragging => before != null;
    public bool CanUndo => Dragging || history.Count > 0;
    public uint Rgb { get; private set; } = 0xFF4545;
    public int Thickness { get; private set; } = 3;

    public void Deselect() { if (!Dragging) SelectedIndex = -1; }

    public static IEnumerable<(PixelPoint Point, SelectionHit Hit)> Handles(Annotation mark) =>
        mark.Kind == AnnotationKind.Rectangle ? Handles(mark.Rectangle) :
        [(mark.Start, SelectionHit.Left), (mark.End, SelectionHit.Right)];

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
            foreach (var handle in Handles(selected))
                if (Math.Abs(p.X - handle.Point.X) <= tolerance && Math.Abs(p.Y - handle.Point.Y) <= tolerance)
                    return (SelectedIndex, handle.Hit);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var r = items[i].Rectangle;
            int hitWidth = Math.Max(tolerance, items[i].Thickness);
            if (items[i].Kind != AnnotationKind.Rectangle)
            {
                var mark = items[i];
                if (mark.Kind == AnnotationKind.Arrow)
                {
                    if (HitOutline(p, mark.ArrowOutline(), tolerance)) return (i, SelectionHit.Move);
                }
                else if (Distance(p, (mark.Start.X, mark.Start.Y), (mark.End.X, mark.End.Y)) <= hitWidth) return (i, SelectionHit.Move);
                continue;
            }
            if (p.X < r.Left - hitWidth || p.X > r.Right + hitWidth || p.Y < r.Top - hitWidth || p.Y > r.Bottom + hitWidth) continue;
            if (Math.Abs(p.X - r.Left) <= hitWidth || Math.Abs(p.X - r.Right) <= hitWidth ||
                Math.Abs(p.Y - r.Top) <= hitWidth || Math.Abs(p.Y - r.Bottom) <= hitWidth)
                return (i, SelectionHit.Move);
        }
        return (-1, SelectionHit.None);
    }

    private static bool HitOutline(PixelPoint p, (double X, double Y)[] outline, int tolerance)
    {
        bool inside = false;
        for (int i = 0; i < outline.Length; i++)
        {
            var a = outline[i]; var b = outline[(i + 1) % outline.Length];
            if (Distance(p, a, b) <= tolerance) return true;
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static double Distance(PixelPoint p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, squared = dx * dx + dy * dy;
        double t = squared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / squared, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
    }

    public void Begin(PixelPoint p, PixelRect region, int tolerance, AnnotationKind kind = AnnotationKind.Rectangle)
    {
        if (Dragging || region.IsEmpty || !region.Contains(p)) return;
        before = items.ToArray(); anchor = p; bounds = region;
        (SelectedIndex, operation) = HitTest(p, tolerance);
        if (SelectedIndex < 0)
        {
            SelectedIndex = items.Count;
            items.Add(new(kind, p, p, Rgb, Thickness));
        }
        originalMark = items[SelectedIndex];
        original = items[SelectedIndex].Rectangle;
        Rgb = items[SelectedIndex].Rgb; Thickness = items[SelectedIndex].Thickness;
    }

    public void Update(PixelPoint p, bool square = false)
    {
        if (!Dragging) return;
        p = bounds.Clamp(p);
        if (originalMark.Kind != AnnotationKind.Rectangle)
        {
            PixelPoint start = originalMark.Start, end = originalMark.End;
            if (operation == SelectionHit.Move)
            {
                var delta = MoveDelta(p);
                start = new(start.X + delta.X, start.Y + delta.Y); end = new(end.X + delta.X, end.Y + delta.Y);
            }
            else if (operation == SelectionHit.Left) start = square ? Constrain(end, p) : p;
            else end = square ? Constrain(start, p) : p;
            items[SelectedIndex] = originalMark with { Start = start, End = end };
            return;
        }
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
            var (dx, dy) = MoveDelta(p);
            r = new(original.Left + dx, original.Top + dy, original.Right + dx, original.Bottom + dy);
        }
        else
        {
            r = PixelRect.Between(new(operation.HasFlag(SelectionHit.Left) ? p.X : original.Left,
                    operation.HasFlag(SelectionHit.Top) ? p.Y : original.Top),
                new(operation.HasFlag(SelectionHit.Right) ? p.X : original.Right,
                    operation.HasFlag(SelectionHit.Bottom) ? p.Y : original.Bottom));
        }
        items[SelectedIndex] = items[SelectedIndex] with { Start = new(r.Left, r.Top), End = new(r.Right, r.Bottom) };
    }

    private PixelPoint MoveDelta(PixelPoint p) => new(
        Math.Clamp(p.X - anchor.X, Math.Min(0, bounds.Left - original.Left), Math.Max(0, bounds.Right - original.Right)),
        Math.Clamp(p.Y - anchor.Y, Math.Min(0, bounds.Top - original.Top), Math.Max(0, bounds.Bottom - original.Bottom)));

    private PixelPoint Constrain(PixelPoint fixedPoint, PixelPoint p)
    {
        PixelPoint best = p; double bestDistance = double.PositiveInfinity;
        // Choose the closest feasible point on the eight rays. The fixed end may be outside a changed crop.
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) })
        {
            double lower = 0, upper = double.MaxValue;
            void Limit(int origin, int direction, int min, int max)
            {
                if (direction == 0) { if (origin < min || origin > max) upper = -1; return; }
                double a = (min - origin) / (double)direction, b = (max - origin) / (double)direction;
                lower = Math.Max(lower, Math.Min(a, b)); upper = Math.Min(upper, Math.Max(a, b));
            }
            Limit(fixedPoint.X, dx, bounds.Left, bounds.Right); Limit(fixedPoint.Y, dy, bounds.Top, bounds.Bottom);
            if (lower > upper) continue;
            double projected = ((p.X - fixedPoint.X) * dx + (p.Y - fixedPoint.Y) * dy) / (double)(dx * dx + dy * dy);
            int length = (int)Math.Clamp(Math.Round(projected), lower, upper);
            var candidate = new PixelPoint(fixedPoint.X + dx * length, fixedPoint.Y + dy * length);
            double distance = Math.Pow(candidate.X - p.X, 2) + Math.Pow(candidate.Y - p.Y, 2);
            if (distance < bestDistance) { best = candidate; bestDistance = distance; }
        }
        return best;
    }

    public void End(PixelPoint p, bool square = false)
    {
        if (before == null) return;
        Update(p, square);
        var saved = before; before = null;
        if (Selected!.Kind == AnnotationKind.Rectangle
            ? Selected.Rectangle.Width < 2 || Selected.Rectangle.Height < 2 : Selected.Length < 2)
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

    private void Restore(Annotation[] saved)
    { items.Clear(); items.AddRange(saved); SelectedIndex = -1; }
}
