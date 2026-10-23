using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace SnapTool;

internal enum ShortcutAction { Capture, Copy, Pin, Save, Ocr, Translate, Undo, Delete, Cancel, ClipboardTranslate }

internal sealed record ShortcutGesture(Key Key, ModifierKeys Modifiers)
{
    internal bool Matches(Key key, ModifierKeys modifiers) => Key == key && Modifiers == modifiers;
    internal static string KeyName(Key key) => key switch
    {
        Key.Escape => "Esc", Key.Return => "Enter", Key.Back => "Backspace",
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => key.ToString()
    };
    public override string ToString() =>
        (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
        (Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
        (Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + KeyName(Key);
}

internal sealed class ShortcutProfile
{
    internal static readonly (ShortcutAction Action, string Label)[] Actions = [
        (ShortcutAction.Capture, "开始截图（全局）"), (ShortcutAction.ClipboardTranslate, "剪贴板翻译（全局）"), (ShortcutAction.Copy, "复制／完成并复制"),
        (ShortcutAction.Pin, "选区贴图"), (ShortcutAction.Save, "保存图片"), (ShortcutAction.Ocr, "OCR"),
        (ShortcutAction.Translate, "选区翻译"), (ShortcutAction.Undo, "撤销标注"),
        (ShortcutAction.Delete, "删除标注"), (ShortcutAction.Cancel, "取消／关闭")];
    internal static readonly Key[] Keys = Enum.GetValues<Key>().Distinct().Where(key =>
        key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.F1 and <= Key.F24 or
            Key.Escape or Key.Delete or Key.Return or Key.Back or Key.Tab or Key.Space or Key.Insert or
            Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down or Key.Snapshot).ToArray();
    internal static ShortcutProfile Default { get; } = new(new()
    {
        [ShortcutAction.Capture] = new(Key.F2, ModifierKeys.Control),
        [ShortcutAction.ClipboardTranslate] = new(Key.T, ModifierKeys.Control | ModifierKeys.Alt),
        [ShortcutAction.Copy] = new(Key.C, ModifierKeys.Control),
        [ShortcutAction.Pin] = new(Key.T, ModifierKeys.Control), [ShortcutAction.Save] = new(Key.S, ModifierKeys.Control),
        [ShortcutAction.Ocr] = new(Key.C, ModifierKeys.Shift), [ShortcutAction.Translate] = new(Key.T, ModifierKeys.Shift),
        [ShortcutAction.Undo] = new(Key.Z, ModifierKeys.Control), [ShortcutAction.Delete] = new(Key.Delete, ModifierKeys.None),
        [ShortcutAction.Cancel] = new(Key.Escape, ModifierKeys.None)
    });
    private readonly Dictionary<ShortcutAction, ShortcutGesture> bindings;
    internal ShortcutGesture this[ShortcutAction action] => bindings[action];
    internal ShortcutProfile(Dictionary<ShortcutAction, ShortcutGesture> bindings) => this.bindings = new(bindings);
    internal Dictionary<ShortcutAction, ShortcutGesture> Export() => new(bindings);
    internal bool Matches(ShortcutAction action, KeyEventArgs e, ModifierKeys modifiers) =>
        this[action].Matches(e.Key == Key.System ? e.SystemKey : e.Key, modifiers);
    internal void Validate()
    {
        if (bindings.Count != Actions.Length || Actions.Any(a => !bindings.ContainsKey(a.Action)))
            throw new ArgumentException("快捷键配置不完整，请恢复默认后重试。");
        foreach (var (action, label) in Actions)
        {
            var value = bindings[action];
            if (action == ShortcutAction.ClipboardTranslate && value is { Key: Key.None, Modifiers: ModifierKeys.None }) continue;
            if (value == null || !Keys.Contains(value.Key) || (value.Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) != 0)
                throw new ArgumentException($"“{label}”的快捷键无效。");
        }
        foreach (var action in new[] { ShortcutAction.Capture, ShortcutAction.ClipboardTranslate })
        {
            var gesture = this[action];
            if (action == ShortcutAction.ClipboardTranslate && gesture.Key == Key.None) continue;
            if (gesture.Key == Key.F12) throw new ArgumentException("F12 是 Windows 调试器保留键，请选择其他全局快捷键。");
            if ((gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 &&
                !(gesture.Key is >= Key.F1 and <= Key.F24 or Key.Snapshot))
                throw new ArgumentException("全局快捷键请使用 Ctrl／Alt 组合键，或 F1～F24 功能键。");
        }
        foreach (var group in Actions.Where(a => bindings[a.Action].Key != Key.None).GroupBy(a => bindings[a.Action]).Where(g => g.Count() > 1))
            throw new ArgumentException($"{group.Key} 重复用于 {string.Join("、", group.Select(a => a.Label))}，请分别设置。");
    }
}

internal sealed class ShortcutSettings(string? filePath = null)
{
    private readonly string path = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapTool", "shortcuts.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    internal ShortcutProfile Load()
    {
        if (!File.Exists(path)) return ShortcutProfile.Default;
        var bindings = JsonSerializer.Deserialize<Dictionary<ShortcutAction, ShortcutGesture>>(File.ReadAllText(path), Options)
            ?? throw new JsonException("快捷键配置为空。");
        if (!bindings.ContainsKey(ShortcutAction.ClipboardTranslate))
        {
            var proposed = ShortcutProfile.Default[ShortcutAction.ClipboardTranslate];
            bindings.Add(ShortcutAction.ClipboardTranslate, bindings.Values.Contains(proposed) ? new(Key.None, ModifierKeys.None) : proposed);
        }
        var profile = new ShortcutProfile(bindings); profile.Validate(); return profile;
    }
    internal void Save(ShortcutProfile profile)
    {
        profile.Validate();
        string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(profile.Export(), Options)); File.Move(temp, fullPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class GlobalHotkeys(Func<int, ShortcutGesture, bool> register, Action<int> unregister) : IDisposable
{
    private Dictionary<ShortcutAction, (int Id, ShortcutGesture Gesture)> active = new();
    internal bool Matches(ShortcutAction action, int id) => active.TryGetValue(action, out var value) && value.Id == id;
    internal bool Registered(ShortcutAction action) => active.ContainsKey(action);
    internal ShortcutGesture? Current(ShortcutAction action) => active.TryGetValue(action, out var value) ? value.Gesture : null;

    internal void Apply(ShortcutProfile next, Action persist) => Apply(new Dictionary<ShortcutAction, ShortcutGesture>
    {
        [ShortcutAction.Capture] = next[ShortcutAction.Capture],
        [ShortcutAction.ClipboardTranslate] = next[ShortcutAction.ClipboardTranslate]
    }, persist);

    internal void Apply(ShortcutAction action, ShortcutGesture gesture, Action persist)
    {
        var next = active.ToDictionary(pair => pair.Key, pair => pair.Value.Gesture);
        next[action] = gesture; Apply(next, persist);
    }

    private void Apply(Dictionary<ShortcutAction, ShortcutGesture> next, Action persist)
    {
        var pending = new Dictionary<ShortcutAction, (int Id, ShortcutGesture Gesture)>();
        var added = new List<int>();
        try
        {
            foreach (var (action, gesture) in next)
            {
                if (gesture.Key == Key.None) continue;
                // Reuse registrations by gesture, including when the two actions exchange keys.
                var existing = active.Values.FirstOrDefault(value => value.Gesture == gesture);
                if (existing.Id != 0) { pending.Add(action, existing); continue; }
                int id = Enumerable.Range(1, 4).First(candidate => !active.Values.Any(value => value.Id == candidate) && !added.Contains(candidate));
                if (!register(id, gesture)) throw new InvalidOperationException($"无法注册 {gesture}，可能已被其他程序占用。原设置已保留。");
                added.Add(id); pending.Add(action, (id, gesture));
            }
            persist();
        }
        catch { foreach (int id in added) unregister(id); throw; }
        foreach (var previous in active.Values)
            if (!pending.Values.Any(value => value.Id == previous.Id)) unregister(previous.Id);
        active = pending;
    }
    public void Dispose() { foreach (var value in active.Values) unregister(value.Id); active.Clear(); }
}
