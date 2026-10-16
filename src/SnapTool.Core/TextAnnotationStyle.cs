namespace SnapTool.Core;

public sealed record TextAnnotationStyle
{
    public string FontFamily { get; init; } = "Microsoft YaHei";
    public int FontSize { get; init; } = 29;
    public bool Bold { get; init; } = true;
    public bool Italic { get; init; }
    public bool Outline { get; init; } = true;
    public bool Background { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 128 || FontSize < 8 || FontSize > 200)
            throw new ArgumentOutOfRangeException(nameof(FontSize));
    }
}
