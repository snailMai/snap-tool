using System;
using System.IO;
using System.Text.Json;
using SnapTool.Core;

namespace SnapTool;

internal sealed record AnnotationStyleProfile
{
    public uint Rgb { get; init; } = 0xFF4545;
    public int Thickness { get; init; } = 8;
    public int MosaicSize { get; init; } = 30;
    public TextAnnotationStyle Text { get; init; } = new();

    internal void Validate()
    {
        if (Rgb > 0xFFFFFF || Thickness < 1 || Thickness > 12 || MosaicSize < 1 || MosaicSize > 30 || Text == null)
            throw new ArgumentException("标注样式配置无效。");
        Text.Validate();
    }

    internal Annotations CreateAnnotations()
    {
        Validate();
        var marks = new Annotations();
        marks.SetStyle(Rgb, Thickness);
        marks.SetStyle(Rgb, MosaicSize, AnnotationKind.Mosaic);
        marks.SetTextStyle(Text, new(0, 0));
        return marks;
    }

    internal static AnnotationStyleProfile From(Annotations marks) => new()
    { Rgb = marks.Rgb, Thickness = marks.Thickness, MosaicSize = marks.MosaicSize, Text = marks.TextStyle };
}

internal sealed class AnnotationStyleSettings(string? filePath = null)
{
    private readonly string path = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnapTool", "annotation-style.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    internal AnnotationStyleProfile Load()
    {
        if (!File.Exists(path)) return new();
        var profile = JsonSerializer.Deserialize<AnnotationStyleProfile>(File.ReadAllText(path), Options)
            ?? throw new JsonException("标注样式配置为空。");
        profile.Validate(); return profile;
    }

    internal void Save(AnnotationStyleProfile profile)
    {
        profile.Validate();
        string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(profile, Options)); File.Move(temp, fullPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
