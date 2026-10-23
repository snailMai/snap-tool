using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class ShortcutSettingsChecks
{
    private static readonly PixelRect Bounds = new(0, 0, 400, 300);
    private static string DirectoryPath => Path.Combine(Program.FindRoot(), ".tmp", "shortcut-settings");
    private static void Assert(bool condition, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    { if (!condition) throw new Exception("Shortcut settings assertion failed at line " + line); }
    private static ShortcutProfile Custom()
    {
        var values = ShortcutProfile.Default.Export();
        values[ShortcutAction.Capture] = new(Key.F8, ModifierKeys.Control | ModifierKeys.Alt);
        values[ShortcutAction.Copy] = new(Key.D, ModifierKeys.Control);
        values[ShortcutAction.Pin] = new(Key.P, ModifierKeys.Control);
        values[ShortcutAction.Save] = new(Key.S, ModifierKeys.Control | ModifierKeys.Shift);
        values[ShortcutAction.Ocr] = new(Key.O, ModifierKeys.Alt);
        values[ShortcutAction.Translate] = new(Key.T, ModifierKeys.Alt);
        values[ShortcutAction.Undo] = new(Key.Z, ModifierKeys.Alt);
        values[ShortcutAction.Delete] = new(Key.D, ModifierKeys.Alt);
        values[ShortcutAction.Cancel] = new(Key.Q, ModifierKeys.Alt);
        var profile = new ShortcutProfile(values); profile.Validate(); return profile;
    }
    private static bool Same(ShortcutProfile first, ShortcutProfile second) =>
        ShortcutProfile.Actions.All(a => first[a.Action] == second[a.Action]);
    private static SettingsWindow Configuration(ShortcutProfile profile, Action<ShortcutProfile> save) =>
        new(profile, save, () => new("synthetic-app", "synthetic-secret"), _ => { });
    private static void Layout(SettingsWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(690, 680)); content.Arrange(new Rect(0, 0, 690, 680)); content.UpdateLayout();
    }
    private static TabControl Tabs(SettingsWindow window) => ((DockPanel)window.Content).Children.OfType<TabControl>().Single();
    private static Button ButtonIn(DependencyObject panel, string label) =>
        RefreshChecks.Descendants<Button>(panel).Single(b => Equals(b.Content, label));
    private static void Click(DependencyObject panel, string label) => ButtonIn(panel, label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static KeyEventArgs Event(Window window, Key key) => new(Keyboard.PrimaryDevice,
        HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle()), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
    private static CaptureSession Session(Func<ShortcutProfile> keys, Action? copy = null, Action? pin = null, Func<string>? recognized = null, Action? copiedText = null, Action? translated = null, Action? closed = null) =>
        new(new(Bounds, [Bounds], OcrChecks.Sample(true, 400, 300)), closed ?? (() => { }), (_, _) => { }, (_, _) => Task.FromResult(recognized?.Invoke() ?? ""),
            (_, _) => { copiedText?.Invoke(); return Task.CompletedTask; }, _ => translated?.Invoke(), (_, _) => pin?.Invoke(),
            (_, _) => { copy?.Invoke(); return Task.CompletedTask; }, getShortcuts: keys);

    internal static void Run(Action<string, Action> test)
    {
        test("legacy shortcut migration preserves user bindings and original file", () =>
        {
            Directory.CreateDirectory(DirectoryPath);
            foreach (bool conflict in new[] { false, true })
            {
                string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".json"); var store = new ShortcutSettings(path);
                var values = Custom().Export(); values.Remove(ShortcutAction.ClipboardTranslate);
                if (conflict) values[ShortcutAction.Translate] = ShortcutProfile.Default[ShortcutAction.ClipboardTranslate];
                string legacy = System.Text.Json.JsonSerializer.Serialize(values, new System.Text.Json.JsonSerializerOptions
                { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
                File.WriteAllText(path, legacy); var loaded = store.Load();
                Assert(File.ReadAllText(path) == legacy && values.All(pair => loaded[pair.Key] == pair.Value));
                Assert(loaded[ShortcutAction.ClipboardTranslate] == (conflict ? new(Key.None, ModifierKeys.None) : ShortcutProfile.Default[ShortcutAction.ClipboardTranslate]));
                store.Save(loaded); Assert(Same(store.Load(), loaded));
            }
        });
        test("clipboard global shortcut validates reserved keys and missing old actions", () =>
        {
            foreach (var invalid in new[] { new ShortcutGesture(Key.F12, ModifierKeys.Control), new(Key.T, ModifierKeys.None),
                ShortcutProfile.Default[ShortcutAction.Capture], new(Key.None, ModifierKeys.Control) })
            {
                var values = ShortcutProfile.Default.Export(); values[ShortcutAction.ClipboardTranslate] = invalid;
                bool rejected = false; try { new ShortcutProfile(values).Validate(); } catch (ArgumentException) { rejected = true; }
                Assert(rejected);
            }
            string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "{}"); bool incomplete = false;
            try { new ShortcutSettings(path).Load(); } catch (ArgumentException) { incomplete = true; }
            Assert(incomplete && File.ReadAllText(path) == "{}");
        });
        test("two global hotkeys roll back both candidates on conflict or save failure", () =>
        {
            var active = new Dictionary<int, ShortcutGesture>(); ShortcutGesture? conflict = null; int saves = 0;
            bool Register(int id, ShortcutGesture value) { if (value == conflict) return false; active.Add(id, value); return true; }
            using var hotkeys = new GlobalHotkeys(Register, id => active.Remove(id));
            hotkeys.Apply(ShortcutProfile.Default, () => { });
            int captureId = active.Single(pair => pair.Value == ShortcutProfile.Default[ShortcutAction.Capture]).Key;
            int clipboardId = active.Single(pair => pair.Value == ShortcutProfile.Default[ShortcutAction.ClipboardTranslate]).Key;
            var nextCapture = new ShortcutGesture(Key.F9, ModifierKeys.Control); var nextClipboard = new ShortcutGesture(Key.F10, ModifierKeys.Control);
            var values = ShortcutProfile.Default.Export(); values[ShortcutAction.Capture] = nextCapture; values[ShortcutAction.ClipboardTranslate] = nextClipboard;
            var next = new ShortcutProfile(values);
            conflict = nextClipboard;
            try { hotkeys.Apply(next, () => saves++); } catch (InvalidOperationException) { }
            Assert(saves == 0 && active.Count == 2 && active.ContainsKey(captureId) && active.ContainsKey(clipboardId));
            conflict = null;
            try { hotkeys.Apply(next, () => throw new IOException("synthetic failure")); } catch (IOException) { }
            Assert(active.Count == 2 && hotkeys.Matches(ShortcutAction.Capture, captureId) && hotkeys.Matches(ShortcutAction.ClipboardTranslate, clipboardId));
            hotkeys.Apply(next, () => { Assert(active.Count == 4); saves++; });
            Assert(saves == 1 && active.Count == 2 && hotkeys.Current(ShortcutAction.Capture) == nextCapture && hotkeys.Current(ShortcutAction.ClipboardTranslate) == nextClipboard);
            Assert(!active.ContainsKey(captureId) && !active.ContainsKey(clipboardId));
            try { hotkeys.Apply(ShortcutAction.ClipboardTranslate, new(Key.None, ModifierKeys.None), () => throw new IOException("synthetic failure")); } catch (IOException) { }
            Assert(hotkeys.Registered(ShortcutAction.ClipboardTranslate) && active.Count == 2);
            hotkeys.Apply(ShortcutAction.ClipboardTranslate, new(Key.None, ModifierKeys.None), () => saves++);
            Assert(!hotkeys.Registered(ShortcutAction.ClipboardTranslate) && active.Count == 1);
            hotkeys.Apply(ShortcutAction.ClipboardTranslate, nextClipboard, () => { }); hotkeys.Dispose(); Assert(active.Count == 0);
        });
        test("shortcut defaults roundtrip through storage and returned dictionaries are independent", () =>
        {
            Directory.CreateDirectory(DirectoryPath); string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
            var store = new ShortcutSettings(path); Assert(Same(store.Load(), ShortcutProfile.Default));
            var profile = Custom(); store.Save(profile); Assert(Same(new ShortcutSettings(path).Load(), profile));
            var exported = profile.Export(); exported[ShortcutAction.Copy] = new(Key.A, ModifierKeys.Alt);
            Assert(profile[ShortcutAction.Copy].Key == Key.D && ShortcutProfile.Default[ShortcutAction.Copy].Key == Key.C);
        });
        test("invalid duplicate reserved and corrupt shortcuts never overwrite an existing configuration", () =>
        {
            string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".json"); var store = new ShortcutSettings(path);
            store.Save(Custom()); string original = File.ReadAllText(path);
            foreach (var value in new[] { ShortcutProfile.Default[ShortcutAction.Copy], new ShortcutGesture(Key.F12, ModifierKeys.Control), new ShortcutGesture(Key.A, ModifierKeys.None), new ShortcutGesture(Key.F2, ModifierKeys.Windows) })
            {
                var values = ShortcutProfile.Default.Export(); values[ShortcutAction.Capture] = value;
                bool rejected = false; try { store.Save(new(values)); } catch (ArgumentException) { rejected = true; }
                Assert(rejected && File.ReadAllText(path) == original);
            }
            File.WriteAllText(path, "invalid-json"); bool corrupt = false;
            try { store.Load(); } catch (System.Text.Json.JsonException) { corrupt = true; }
            Assert(corrupt && File.ReadAllText(path) == "invalid-json");
        });
        test("hotkey conflict and persistence failure retain the old registration and binding", () =>
        {
            var active = new Dictionary<int, ShortcutGesture>(); bool conflict = false; int saves = 0;
            using var hotkey = new GlobalHotkeys(
                (id, gesture) => { if (conflict) return false; active.Add(id, gesture); return true; }, id => active.Remove(id));
            var old = ShortcutProfile.Default[ShortcutAction.Capture];
            hotkey.Apply(ShortcutAction.Capture, old, () => { }); int oldId = active.Single().Key;
            conflict = true;
            try { hotkey.Apply(ShortcutAction.Capture, Custom()[ShortcutAction.Capture], () => saves++); } catch (InvalidOperationException) { }
            Assert(saves == 0 && hotkey.Current(ShortcutAction.Capture) == old && active.Count == 1 && active.ContainsKey(oldId));
            conflict = false;
            try { hotkey.Apply(ShortcutAction.Capture, Custom()[ShortcutAction.Capture], () => throw new IOException("synthetic write failure")); } catch (IOException) { }
            Assert(hotkey.Current(ShortcutAction.Capture) == old && hotkey.Matches(ShortcutAction.Capture, oldId) && active.Count == 1 && active[oldId] == old);
            hotkey.Apply(ShortcutAction.Capture, Custom()[ShortcutAction.Capture], () => { Assert(active.Count == 2 && active[oldId] == old); saves++; });
            Assert(saves == 1 && active.Count == 1 && !active.ContainsKey(oldId) && hotkey.Current(ShortcutAction.Capture) == Custom()[ShortcutAction.Capture]);
            int sameId = active.Single().Key; hotkey.Apply(ShortcutAction.Capture, Custom()[ShortcutAction.Capture], () => saves++);
            Assert(hotkey.Matches(ShortcutAction.Capture, sameId) && active.Count == 1 && saves == 2);
            hotkey.Dispose(); Assert(active.Count == 0);
        });
        test("configured screenshot copy and pin replace old keys and update hints live", () => OcrChecks.Pump(async () =>
        {
            foreach (bool pin in new[] { false, true })
            {
                int copies = 0, pins = 0; var current = ShortcutProfile.Default;
                var session = Session(() => current, () => copies++, () => pins++); session.Selection.Select(Bounds);
                var window = new OverlayWindow(session, Bounds);
                try
                {
                    current = Custom(); window.Refresh();
                    await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); await ShortcutChecks.Press(window, Key.T, ModifierKeys.Control);
                    Assert(copies == 0 && pins == 0);
                    var button = RefreshChecks.Descendants<Button>((DependencyObject)window.Content).Single(b => Equals(b.Tag, pin ? "贴图" : "复制"));
                    Assert(((string)button.ToolTip).Contains(pin ? "Ctrl+P" : "Ctrl+D"));
                    await ShortcutChecks.Press(window, pin ? Key.P : Key.D, ModifierKeys.Control);
                    Assert(pin ? pins == 1 && copies == 0 : copies == 1 && pins == 0);
                }
                finally { window.Close(); session.Close(); }
            }
        }));
        test("configured OCR translation undo delete and cancel keep their action scope", () => OcrChecks.Pump(async () =>
        {
            foreach (bool translate in new[] { false, true })
            {
                int calls = 0, copied = 0, translated = 0;
                var session = Session(Custom, recognized: () => { calls++; return "synthetic"; }, copiedText: () => copied++, translated: () => translated++);
                session.Selection.Select(Bounds); var window = new OverlayWindow(session, Bounds);
                try
                {
                    await ShortcutChecks.Press(window, Key.C, ModifierKeys.Shift); await ShortcutChecks.Press(window, Key.T, ModifierKeys.Shift);
                    Assert(calls == 0); await ShortcutChecks.Press(window, translate ? Key.T : Key.O, ModifierKeys.Alt);
                    Assert(calls == 1 && (translate ? translated == 1 && copied == 0 : copied == 1 && translated == 0));
                }
                finally { window.Close(); session.Close(); }
            }
            bool closed = false; var marks = Session(Custom, closed: () => closed = true); marks.Selection.Select(Bounds); marks.SetTool(AnnotationKind.Arrow);
            var overlay = new OverlayWindow(marks, Bounds);
            try
            {
                marks.Begin(new(50, 50), 2); marks.End(new(150, 100));
                await ShortcutChecks.Press(overlay, Key.Z, ModifierKeys.Control); Assert(marks.Annotations.Items.Count == 1);
                await ShortcutChecks.Press(overlay, Key.Z, ModifierKeys.Alt); Assert(marks.Annotations.Items.Count == 0);
                marks.Begin(new(50, 50), 2); marks.End(new(150, 100));
                await ShortcutChecks.Press(overlay, Key.Delete, ModifierKeys.None); Assert(marks.Annotations.Items.Count == 1);
                await ShortcutChecks.Press(overlay, Key.D, ModifierKeys.Alt); Assert(marks.Annotations.Items.Count == 0);
                await ShortcutChecks.Press(overlay, Key.Escape, ModifierKeys.None); Assert(!closed);
                await ShortcutChecks.Press(overlay, Key.Q, ModifierKeys.Alt); Assert(closed);
            }
            finally { overlay.Close(); marks.Close(); }
        }));
        test("pin image copy save and close use configured gestures and menu hints", () => OcrChecks.Pump(async () =>
        {
            int copies = 0, saves = 0; var current = ShortcutProfile.Default;
            string path = Path.Combine(DirectoryPath, "pin.png");
            var window = new PinWindow(OcrChecks.Sample(true, 100, 80), new(100, 100, 200, 180),
                (_, _) => { copies++; return Task.CompletedTask; }, _ => { saves++; return path; }, () => current);
            bool closed = false; window.Closed += (_, _) => closed = true;
            try
            {
                current = Custom(); window.RefreshShortcuts();
                Assert(((MenuItem)window.ContextMenu.Items[0]).InputGestureText == "Ctrl+D");
                await ShortcutChecks.Press(window, Key.C, ModifierKeys.Control); await ShortcutChecks.Press(window, Key.S, ModifierKeys.Control);
                Assert(copies == 0 && saves == 0);
                await ShortcutChecks.Press(window, Key.D, ModifierKeys.Control); Assert(copies == 1);
                await ShortcutChecks.Press(window, Key.S, ModifierKeys.Control | ModifierKeys.Shift); Assert(saves == 1 && File.Exists(path));
                await ShortcutChecks.Press(window, Key.Escape, ModifierKeys.None); Assert(!closed);
                await ShortcutChecks.Press(window, Key.Q, ModifierKeys.Alt); Assert(closed);
            }
            finally { window.Close(); }
        }));
        test("long screenshot and translation use configured completion and cancellation", () => OcrChecks.Pump(async () =>
        {
            var image = OcrChecks.Sample(true, 100, 80); int copies = 0;
            var scroll = new ScrollCaptureWindow(new(100, 100, 200, 180), image, (_, _, _) => { copies++; return Task.CompletedTask; }, () => image, getShortcuts: Custom);
            try
            {
                await ShortcutChecks.Press(scroll, Key.C, ModifierKeys.Control); Assert(copies == 0);
                await ShortcutChecks.Press(scroll, Key.D, ModifierKeys.Control); Assert(copies == 1 && scroll.LivePreview == null);
            }
            finally { scroll.Close(); }
            var canceled = new ScrollCaptureWindow(new(100, 100, 200, 180), image, (_, _, _) => throw new Exception("Unexpected output"), () => image, getShortcuts: Custom);
            try
            {
                await ShortcutChecks.Press(canceled, Key.Escape, ModifierKeys.None); Assert(canceled.LivePreview != null);
                await ShortcutChecks.Press(canceled, Key.Q, ModifierKeys.Alt); Assert(canceled.LivePreview == null);
            }
            finally { canceled.Close(); }
            var translation = new TranslationWindow("synthetic", (_, _, _) => Task.FromResult("result"), getShortcuts: Custom); bool closed = false;
            translation.Closed += (_, _) => closed = true;
            try
            {
                translation.HandleKey(Event(translation, Key.Escape), ModifierKeys.None); Assert(!closed);
                translation.HandleKey(Event(translation, Key.Q), ModifierKeys.Alt); Assert(closed);
            }
            finally { translation.Close(); }
        }));
        test("configuration restore default requires save and invalid duplicates never reach persistence", () =>
        {
            ShortcutProfile? saved = null;
            var window = Configuration(Custom(), profile => saved = profile);
            try
            {
                var content = (FrameworkElement)window.Content; content.Measure(new Size(640, 610)); content.Arrange(new Rect(0, 0, 640, 610)); content.UpdateLayout();
                var controls = RefreshChecks.Descendants<ComboBox>((DependencyObject)window.Content).ToArray();
                foreach (ComboBoxItem item in controls[0].Items) if ((Key)item.Tag == Key.D) controls[0].SelectedItem = item;
                var checks = RefreshChecks.Descendants<CheckBox>((DependencyObject)window.Content).ToArray(); checks[1].IsChecked = false;
                Button Find(string name) => RefreshChecks.Descendants<Button>((DependencyObject)window.Content).Single(b => Equals(b.Content, name));
                Find("保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(saved == null);
                Find("恢复默认").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(saved == null);
                Find("保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(saved != null && Same(saved, ShortcutProfile.Default));
            }
            finally { window.Close(); }
        });
        test("configuration lazily loads translation and saves each page independently without losing drafts", () =>
        {
            string path = Path.Combine(DirectoryPath, "unified-" + Guid.NewGuid().ToString("N") + ".json");
            var settings = new TranslationSettings(path); int loads = 0, shortcutSaves = 0;
            settings.Save(new("initial-app", "synthetic-secret"));
            var window = new SettingsWindow(Custom(), _ => shortcutSaves++, () => { loads++; return settings.Load(); }, settings.Save);
            try
            {
                Layout(window); var tabs = Tabs(window); Assert(loads == 0 && tabs.Items.Count == 2);
                var shortcuts = (DependencyObject)((TabItem)tabs.Items[0]).Content;
                tabs.SelectedIndex = 1; Layout(window); Assert(loads == 1);
                Assert(((FrameworkElement)window.Content).ActualWidth <= 690);
                var translation = (DependencyObject)((TabItem)tabs.Items[1]).Content;
                var appId = RefreshChecks.Descendants<TextBox>(translation).Single();
                var secret = RefreshChecks.Descendants<PasswordBox>(translation).Single();
                appId.Text = "edited-app"; secret.Password = "edited-synthetic-secret";
                tabs.SelectedIndex = 0; Layout(window); Click(shortcuts, "恢复默认");
                Assert(shortcutSaves == 0 && settings.Load().AppId == "initial-app");
                Click(shortcuts, "保存"); Assert(shortcutSaves == 1 && settings.Load().AppId == "initial-app");
                tabs.SelectedIndex = 1; Layout(window);
                Assert(loads == 1 && appId.Text == "edited-app" && secret.Password == "edited-synthetic-secret");
                Click(translation, "保存"); Assert(shortcutSaves == 1);
                Assert(settings.Load().AppId == "edited-app" && settings.Load().Secret == "edited-synthetic-secret");
                Assert(!File.ReadAllText(path).Contains("edited-synthetic-secret"));
                appId.Text = "unsaved-app";
                Click((DependencyObject)window.Content, "关闭"); Assert(settings.Load().AppId == "edited-app");
            }
            finally { window.Close(); if (File.Exists(path)) File.Delete(path); }
        });
        test("configuration retains drafts on load or save failure and allows retry", () =>
        {
            string path = Path.Combine(DirectoryPath, "retry-" + Guid.NewGuid().ToString("N") + ".json");
            var settings = new TranslationSettings(path); bool failShortcut = true, failTranslation = true;
            int shortcutSaves = 0;
            var window = new SettingsWindow(Custom(), _ =>
            {
                if (failShortcut) throw new IOException("synthetic failure"); shortcutSaves++;
            }, () => throw new InvalidOperationException("synthetic load failure"), credentials =>
            {
                if (failTranslation) throw new IOException("synthetic failure"); settings.Save(credentials);
            });
            try
            {
                Layout(window); var tabs = Tabs(window);
                var shortcuts = (DependencyObject)((TabItem)tabs.Items[0]).Content;
                Click(shortcuts, "保存"); Assert(shortcutSaves == 0);
                failShortcut = false; Click(shortcuts, "保存"); Assert(shortcutSaves == 1);
                tabs.SelectedIndex = 1; Layout(window);
                var translation = (DependencyObject)((TabItem)tabs.Items[1]).Content;
                Assert(RefreshChecks.Descendants<TextBlock>(translation).Any(t => t.Text.Contains("已有配置无法读取")));
                var appId = RefreshChecks.Descendants<TextBox>(translation).Single();
                var secret = RefreshChecks.Descendants<PasswordBox>(translation).Single();
                appId.Text = "retry-app"; secret.Password = "retry-synthetic-secret";
                Click(translation, "保存"); Assert(!File.Exists(path) && appId.Text == "retry-app");
                failTranslation = false; secret.Password = ""; Click(translation, "保存"); Assert(!File.Exists(path));
                secret.Password = "retry-synthetic-secret"; Click(translation, "保存");
                Assert(settings.Load().AppId == "retry-app" && shortcutSaves == 1);
            }
            finally { window.Close(); if (File.Exists(path)) File.Delete(path); }
        });
    }

    internal static void CheckNative()
    {
        using var owner = new HwndSource(new HwndSourceParameters("Snap Tool synthetic shortcut owner"));
        using var contender = new HwndSource(new HwndSourceParameters("Snap Tool synthetic shortcut contender"));
        var available = new List<ShortcutGesture>();
        foreach (var key in new[] { Key.F23, Key.F22, Key.F21, Key.F20, Key.F19 })
        {
            var gesture = new ShortcutGesture(key, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
            if (!Native.RegisterHotKey(contender.Handle, 90, (uint)gesture.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(key))) continue;
            Native.UnregisterHotKey(contender.Handle, 90); available.Add(gesture); if (available.Count == 4) break;
        }
        Assert(available.Count == 4);
        using var registration = new GlobalHotkeys(
            (id, gesture) => Native.RegisterHotKey(owner.Handle, id, (uint)gesture.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)),
            id => { Native.UnregisterHotKey(owner.Handle, id); });
        try
        {
            registration.Apply(ShortcutAction.Capture, available[0], () => { });
            Assert(Native.RegisterHotKey(contender.Handle, 91, (uint)available[1].Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(available[1].Key)));
            bool refused = false;
            try { registration.Apply(ShortcutAction.Capture, available[1], () => throw new Exception("Should not save")); } catch (InvalidOperationException) { refused = true; }
            Assert(refused && registration.Current(ShortcutAction.Capture) == available[0]); Native.UnregisterHotKey(contender.Handle, 91);
            registration.Apply(ShortcutAction.Capture, available[1], () => { });
            Assert(Native.RegisterHotKey(contender.Handle, 92, (uint)available[0].Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(available[0].Key)));
            Console.WriteLine("PASS native hotkey conflict preserves old registration and successful update releases old key; no user shortcuts or keyboard input used.");
        }
        finally { Native.UnregisterHotKey(contender.Handle, 91); Native.UnregisterHotKey(contender.Handle, 92); }
        registration.Dispose();
        using (var hotkeys = new GlobalHotkeys(
            (id, gesture) => Native.RegisterHotKey(owner.Handle, id, (uint)gesture.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)),
            id => { Native.UnregisterHotKey(owner.Handle, id); }))
        {
            ShortcutProfile Profile(int capture, int clipboard)
            {
                var bindings = ShortcutProfile.Default.Export(); bindings[ShortcutAction.Capture] = available[capture];
                bindings[ShortcutAction.ClipboardTranslate] = available[clipboard]; return new(bindings);
            }
            hotkeys.Apply(Profile(0, 1), () => { });
            Assert(Native.RegisterHotKey(contender.Handle, 93, (uint)available[3].Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(available[3].Key)));
            try
            {
                bool refused = false;
                try { hotkeys.Apply(Profile(2, 3), () => throw new Exception("Should not save")); }
                catch (InvalidOperationException) { refused = true; }
                Assert(refused && hotkeys.Current(ShortcutAction.Capture) == available[0] && hotkeys.Current(ShortcutAction.ClipboardTranslate) == available[1]);
            }
            finally { Native.UnregisterHotKey(contender.Handle, 93); }
            bool saveFailed = false;
            try { hotkeys.Apply(Profile(2, 3), () => throw new IOException("synthetic save failure")); }
            catch (IOException) { saveFailed = true; }
            Assert(saveFailed && hotkeys.Current(ShortcutAction.Capture) == available[0] && hotkeys.Current(ShortcutAction.ClipboardTranslate) == available[1]);
            hotkeys.Apply(Profile(2, 3), () => { });
            hotkeys.Apply(Profile(3, 2), () => { });
            Assert(hotkeys.Current(ShortcutAction.Capture) == available[3] && hotkeys.Current(ShortcutAction.ClipboardTranslate) == available[2]);
            // Moving one action onto the other's old key must also reuse that registration.
            hotkeys.Apply(Profile(2, 0), () => { });
            Assert(hotkeys.Current(ShortcutAction.Capture) == available[2] && hotkeys.Current(ShortcutAction.ClipboardTranslate) == available[0]);
            Console.WriteLine("PASS native two global hotkeys: conflict/save failure roll back both; update, swap and key reuse succeed.");
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var configuration = Configuration(Custom(), _ => { });
        try
        {
            OcrChecks.Pump(async () =>
            {
                configuration.Show(); configuration.Activate(); await Task.Delay(100);
                Assert(configuration.IsVisible && configuration.ActualWidth > 0);
                var tabs = Tabs(configuration); tabs.SelectedIndex = 1; await Task.Delay(50);
                Assert(configuration.IsVisible && RefreshChecks.Descendants<PasswordBox>((DependencyObject)configuration.Content).Any());
                var translation = (FrameworkElement)((TabItem)tabs.Items[1]).Content;
                Assert(translation.ActualWidth <= tabs.ActualWidth);
                Assert(RefreshChecks.Descendants<CheckBox>(translation).Single().IsChecked == true);
                tabs.SelectedIndex = 0; await Task.Delay(50);
                var monitor = Native.Monitors()[0];
                var recorder = new RecordingWindow(new(monitor.Left + 120, monitor.Top + 120, monitor.Left + 440, monitor.Top + 300),
                    (_, _) => throw new Exception("Unexpected recording output"), getShortcuts: Custom);
                try
                {
                    recorder.Start();
                    await ShortcutChecks.Press(recorder, Key.Escape, ModifierKeys.None); Assert(recorder.IsVisible);
                    await ShortcutChecks.Press(recorder, Key.Q, ModifierKeys.Alt); Assert(!recorder.IsVisible);
                }
                finally { recorder.Close(); }
                configuration.RaiseEvent(Event(configuration, Key.Escape)); Assert(!configuration.IsVisible);
                Console.WriteLine("PASS native configuration tab switching, bounded translation layout and Esc close; synthetic credentials only.");
            });
        }
        finally { configuration.Close(); app.Shutdown(); }
    }

    internal static void RenderPreview()
    {
        var window = Configuration(ShortcutProfile.Default, _ => { });
        try
        {
            var root = (FrameworkElement)window.Content; root.Width = 642; root.Height = 632;
            Layout(window);
            foreach (int page in new[] { 0, 1 })
            {
                Tabs(window).SelectedIndex = page; Layout(window);
                var content = (FrameworkElement)window.Content;
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(window.Background, null, new Rect(0, 0, 690, 680));
                    dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(0, 0, 690, 680), Stretch = Stretch.Fill }, null, new Rect(0, 0, 690, 680));
                }
                var image = new RenderTargetBitmap(690, 680, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                Directory.CreateDirectory(DirectoryPath); ImageOutput.SavePng(image, Path.Combine(DirectoryPath, page == 0 ? "preview.png" : "translation-preview.png"));
            }
        }
        finally { window.Close(); }
    }
}
