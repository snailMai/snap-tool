namespace SnapTool.Core;

public enum ScrollAppendResult { Added, Unchanged, NoMatch, LimitReached }

// BGR32 pixels, independent of WPF and native capture. Frames own their pixel buffers.
public sealed class ScrollFrame
{
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Pixels => data;
    internal readonly byte[] data;
    internal readonly ulong[] rows;

    public ScrollFrame(int width, int height, byte[] pixels)
    {
        if (width < 32 || height < 64 || (long)width * height > ScrollStitcher.MaxPixels ||
            pixels.LongLength != (long)width * height * 4)
            throw new ArgumentException("Invalid scroll frame dimensions.");
        Width = width; Height = height; data = (byte[])pixels.Clone(); rows = new ulong[height];
        for (int y = 0; y < height; y++)
        {
            ulong hash = 14695981039346656037;
            for (int x = 0; x < width; x += Math.Max(1, width / 128))
            {
                int offset = (y * width + x) * 4;
                for (int c = 0; c < 3; c++) hash = unchecked((hash ^ data[offset + c]) * 1099511628211);
            }
            rows[y] = hash;
        }
    }

    public bool SimilarTo(ScrollFrame other, CancellationToken token = default) =>
        Width == other.Width && Height == other.Height && Matches(other, 0, Height, 0.001, token);

    internal bool Matches(ScrollFrame other, int offsetRows, int height, double tolerance, CancellationToken token, int colorTolerance = 3)
    {
        long allowed = (long)(Width * (long)height * tolerance), changed = 0;
        for (int y = 0; y < height; y++)
        {
            if ((y & 15) == 0) token.ThrowIfCancellationRequested();
            int a = (y + offsetRows) * Width * 4, b = y * Width * 4;
            for (int x = 0; x < Width; x++, a += 4, b += 4)
                if ((Math.Abs(data[a] - other.data[b]) > colorTolerance || Math.Abs(data[a + 1] - other.data[b + 1]) > colorTolerance ||
                    Math.Abs(data[a + 2] - other.data[b + 2]) > colorTolerance) && ++changed > allowed) return false;
        }
        return true;
    }

    internal bool SampleMatches(ScrollFrame other, int shift, int height, CancellationToken token)
    {
        int xStep = Math.Max(1, Width / 32), yStep = Math.Max(1, height / 32), changed = 0;
        int samples = ((Width - 1) / xStep + 1) * ((height - 1) / yStep + 1);
        for (int y = 0; y < height; y += yStep)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < Width; x += xStep)
            {
                int a = ((y + shift) * Width + x) * 4, b = (y * Width + x) * 4;
                if ((Math.Abs(data[a] - other.data[b]) > 8 || Math.Abs(data[a + 1] - other.data[b + 1]) > 8 ||
                    Math.Abs(data[a + 2] - other.data[b + 2]) > 8) && ++changed > samples / 1000) return false;
            }
        }
        return true;
    }
}

public sealed class ScrollStitcher
{
    public const int MaxPixels = 32_000_000, MaxHeight = 30_000;
    public int Width => last.Width;
    public int Height { get; private set; }
    public int Frames { get; private set; } = 1;
    private ScrollFrame last;
    private readonly List<byte[]> chunks = [];

    public ScrollStitcher(ScrollFrame first)
    {
        if (first.Height > MaxHeight) throw new ArgumentException("Scroll frame is too tall.");
        last = first; Height = first.Height; chunks.Add(first.data);
    }

    public ScrollAppendResult Append(ScrollFrame next, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (next.Width != Width || next.Height != last.Height) throw new ArgumentException("Scroll frame size changed.");
        if (last.SimilarTo(next, token)) return ScrollAppendResult.Unchanged;
        int best = 0, minimumOverlap = Math.Max(32, last.Height / 4);
        for (int shift = 1; shift <= last.Height - minimumOverlap; shift++)
        {
            if ((shift & 31) == 0) token.ThrowIfCancellationRequested();
            int overlap = last.Height - shift, mismatches = 0;
            for (int y = 0; y < overlap; y++)
                if (last.rows[y + shift] != next.rows[y] && ++mismatches > overlap / 200) break;
            // Exact hashes are only a fast path: small repaint differences must reach pixel verification.
            if (mismatches > overlap / 200 && !last.SampleMatches(next, shift, overlap, token)) continue;
            // Plain areas and repeating rows do not establish a unique vertical position.
            var distinct = new HashSet<ulong>();
            for (int y = 0; y < overlap && distinct.Count < 8; y++)
                distinct.Add(next.rows[y]);
            if (distinct.Count < 8 || !last.Matches(next, shift, overlap, 0.001, token, 8)) continue;
            if (best != 0) return ScrollAppendResult.NoMatch;
            best = shift;
        }
        if (best == 0) return ScrollAppendResult.NoMatch;
        if ((long)(Height + best) * Width > MaxPixels || Height + best > MaxHeight) return ScrollAppendResult.LimitReached;
        var tail = new byte[checked(best * Width * 4)];
        Buffer.BlockCopy(next.data, (next.Height - best) * Width * 4, tail, 0, tail.Length);
        token.ThrowIfCancellationRequested();
        chunks.Add(tail); Height += best; Frames++; last = next;
        return ScrollAppendResult.Added;
    }

    public byte[] Compose(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var pixels = new byte[checked(Width * Height * 4)]; int offset = 0;
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            Buffer.BlockCopy(chunk, 0, pixels, offset, chunk.Length); offset += chunk.Length;
        }
        return pixels;
    }

    public (int Width, int Height, byte[] Pixels) Preview(int maxWidth, int maxHeight, CancellationToken token = default)
    {
        if (maxWidth < 1 || maxHeight < 1 || maxWidth > 1024 || maxHeight > 1024) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        double scale = Math.Min(1, Math.Min((double)maxWidth / Width, (double)maxHeight / Height));
        int width = Math.Max(1, (int)(Width * scale)), height = Math.Max(1, (int)(Height * scale));
        var pixels = new byte[width * height * 4]; int chunk = 0, rowOffset = 0;
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            int sourceY = (int)((long)y * Height / height);
            while (sourceY >= rowOffset + chunks[chunk].Length / (Width * 4))
            { rowOffset += chunks[chunk].Length / (Width * 4); chunk++; }
            for (int x = 0; x < width; x++)
            {
                int sourceX = (int)((long)x * Width / width);
                Buffer.BlockCopy(chunks[chunk], ((sourceY - rowOffset) * Width + sourceX) * 4, pixels, (y * width + x) * 4, 4);
            }
        }
        return (width, height, pixels);
    }
}
