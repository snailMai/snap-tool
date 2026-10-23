using System;
using System.IO;
using System.Text.Json;

namespace SnapTool;

internal sealed class TranslationBehaviorSettings(string? filePath = null)
{
    private readonly string path = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnapTool", "translation-behavior.json");
    private sealed record Options(bool CopySelection = true);
    internal bool Load() => !File.Exists(path) ||
        (JsonSerializer.Deserialize<Options>(File.ReadAllText(path)) ?? throw new JsonException("快捷翻译配置为空。")).CopySelection;

    internal void Save(bool copySelection)
    {
        string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Options(copySelection)));
            File.Move(temp, fullPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
