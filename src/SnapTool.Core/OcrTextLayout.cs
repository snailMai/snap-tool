namespace SnapTool.Core;

internal sealed record OcrGlyph(int Start, int Length, PixelPoint[] Corners)
{
    internal int End => Start + Length;
    internal PixelRect Bounds => new(Corners.Min(p => p.X), Corners.Min(p => p.Y), Corners.Max(p => p.X), Corners.Max(p => p.Y));
    internal bool Contains(double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = Corners.Length - 1; i < Corners.Length; j = i++)
        {
            var a = Corners[i]; var b = Corners[j];
            if ((a.Y > y) != (b.Y > y) && x < (double)(b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}

internal sealed class OcrTextLayout(string text, IReadOnlyList<OcrGlyph> glyphs)
{
    internal string Text { get; } = text;
    internal IReadOnlyList<OcrGlyph> Glyphs { get; } = glyphs;
    internal int Hit(double x, double y, bool nearest = false)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return -1;
        int best = -1; double bestVertical = double.PositiveInfinity, bestDistance = double.PositiveInfinity;
        for (int i = 0; i < Glyphs.Count; i++)
        {
            var glyph = Glyphs[i];
            if (glyph.Contains(x, y)) return i;
            if (!nearest) continue;
            var r = glyph.Bounds;
            double dx = Math.Max(0, Math.Max(r.Left - x, x - r.Right));
            double dy = Math.Max(0, Math.Max(r.Top - y, y - r.Bottom));
            double distance = dx * dx + dy * dy;
            if (dy < bestVertical || (dy == bestVertical && distance < bestDistance))
            { best = i; bestVertical = dy; bestDistance = distance; }
        }
        return best;
    }
}

internal sealed class OcrTextSelection(OcrTextLayout layout)
{
    private int anchor = -1, focus = -1;
    internal string Text => anchor < 0 ? "" : layout.Text[layout.Glyphs[Math.Min(anchor, focus)].Start..layout.Glyphs[Math.Max(anchor, focus)].End];
    internal IEnumerable<OcrGlyph> Glyphs => anchor < 0 ? [] : layout.Glyphs.Skip(Math.Min(anchor, focus)).Take(Math.Abs(anchor - focus) + 1);
    internal bool Begin(double x, double y) { anchor = focus = layout.Hit(x, y); return anchor >= 0; }
    internal void Update(double x, double y) { if (anchor >= 0) { int hit = layout.Hit(x, y, true); if (hit >= 0) focus = hit; } }
    internal void Clear() => anchor = focus = -1;
}
