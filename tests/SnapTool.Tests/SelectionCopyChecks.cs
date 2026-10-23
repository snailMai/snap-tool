using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SnapTool;

namespace SnapTool.Tests;

internal static class SelectionCopyChecks
{
    private static readonly IntPtr Target = new(123);
    private static void Assert(bool condition) { if (!condition) throw new Exception("Selection copy assertion failed."); }
    private static string NewSettingsPath()
    {
        string path = Path.Combine(Program.FindRoot(), ".tmp", "selection-translation-checks", Guid.NewGuid().ToString("N"), "behavior.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); return path;
    }
    internal static void Run(Action<string, Action> test)
    {
        test("auto copy preference defaults checked and persists independently from credentials", () =>
        {
            string path = NewSettingsPath(); var store = new TranslationBehaviorSettings(path);
            Assert(store.Load() && !File.Exists(path)); File.WriteAllText(path, "{}"); Assert(store.Load());
            store.Save(false); Assert(!new TranslationBehaviorSettings(path).Load());
            store.Save(true); Assert(new TranslationBehaviorSettings(path).Load());
            Assert(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1);
        });
        test("corrupt auto copy configuration and failed save never replace previous settings", () =>
        {
            string path = NewSettingsPath(); var store = new TranslationBehaviorSettings(path);
            foreach (string invalid in new[] { "invalid", "null", "{\"CopySelection\":\"false\"}" })
            {
                File.WriteAllText(path, invalid); bool rejected = false;
                try { store.Load(); } catch (System.Text.Json.JsonException) { rejected = true; }
                Assert(rejected && File.ReadAllText(path) == invalid);
            }
            store.Save(false); File.SetAttributes(path, FileAttributes.ReadOnly); bool failed = false;
            try { store.Save(true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
            Assert(failed && !store.Load() && Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1);
        });
        test("auto copy checkbox saves without credentials and unsaved changes are discarded", () =>
        {
            string path = NewSettingsPath(); var store = new TranslationBehaviorSettings(path); int credentialSaves = 0;
            foreach (bool firstOpen in new[] { true, false })
            {
                var window = new SettingsWindow(ShortcutProfile.Default, _ => { }, () => new("", ""), _ => credentialSaves++, store.Load, store.Save);
                try
                {
                    var tabs = ((DockPanel)window.Content).Children.OfType<TabControl>().Single(); tabs.SelectedIndex = 1;
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new Size(690, 680)); root.Arrange(new Rect(0, 0, 690, 680)); root.UpdateLayout();
                    var panel = (DependencyObject)((TabItem)tabs.Items[1]).Content;
                    var option = RefreshChecks.Descendants<CheckBox>(panel).Single(); Assert(option.IsChecked == firstOpen);
                    option.IsChecked = false;
                    RefreshChecks.Descendants<Button>(panel).Single(b => Equals(b.Content, "保存翻译行为")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(!store.Load() && credentialSaves == 0);
                    option.IsChecked = true; window.Close(); Assert(!store.Load());
                }
                finally { window.Close(); }
            }
        });
        test("copy input uses correctly sized Ctrl C events and releases partial synthetic input", () =>
        {
            Assert(Marshal.SizeOf<Native.Input>() == (IntPtr.Size == 8 ? 40 : 28));
            int calls = 0;
            Assert(Native.SendCopy(inputs =>
            {
                calls++; Assert(inputs.Length == 4 && inputs.All(input => input.Type == 1));
                Assert(inputs.Select(input => input.Data.Keyboard.Key).SequenceEqual(new ushort[] { 0x11, 0x43, 0x43, 0x11 }));
                Assert(inputs.Select(input => input.Data.Keyboard.Flags).SequenceEqual(new uint[] { 0, 0, 2, 2 }));
                return 4;
            }) && calls == 1);
            calls = 0; Assert(!Native.SendCopy(_ => { calls++; return 0; }) && calls == 1);
            calls = 0; Assert(!Native.SendCopy(inputs =>
            {
                if (++calls == 1) return 2;
                Assert(inputs.Length == 2 && inputs.All(input => input.Data.Keyboard.Flags == 2)); return 2;
            }) && calls == 2);
        });
        test("selected text copy waits for hotkey release and accepts updated identical clipboard text", () => OcrChecks.Pump(async () =>
        {
            uint sequence = 7; int keyChecks = 0, sends = 0, reads = 0; string? shown = null;
            var copy = new SelectionCopy(() => Target, () => ++keyChecks < 3, () => sequence,
                () => { Assert(keyChecks >= 3); sends++; sequence++; return true; }, target => target == Target);
            var flow = new ClipboardTranslation(() => { reads++; return "same English text"; }, text => shown = text, _ => throw new Exception("Unexpected failure"));
            await flow.Run(default, token => copy.Run(Target, token));
            Assert(sends == 1 && reads == 1 && shown == "same English text");
        }));
        test("failed selected copy never reads or translates stale clipboard text", () => OcrChecks.Pump(async () =>
        {
            foreach (string failure in new[] { "no target", "held keys", "focus before", "denied", "unchanged", "foreign clipboard", "focus after", "no access" })
            {
                uint sequence = failure == "no access" ? 0u : 7u; IntPtr foreground = Target; int sends = 0, reads = 0, reports = 0;
                var copy = new SelectionCopy(() => foreground, () =>
                {
                    if (failure == "focus before") foreground = new(999);
                    return failure is "held keys" or "focus before";
                }, () => sequence, () =>
                {
                    sends++; if (failure == "denied") return false;
                    if (failure != "unchanged") sequence++;
                    if (failure == "focus after") foreground = new(999);
                    return true;
                }, _ => failure != "foreign clipboard");
                var flow = new ClipboardTranslation(() => { reads++; return "stale private text"; }, _ => throw new Exception("Stale translation"), _ => reports++);
                await flow.Run(default, token => copy.Run(failure == "no target" ? IntPtr.Zero : Target, token));
                Assert(reads == 0 && reports == 1 && sends <= 1);
                if (failure is "no target" or "held keys" or "focus before" or "no access") Assert(sends == 0);
            }
        }));
        test("exit and repeated triggers during selected copy do not send or translate twice", () => OcrChecks.Pump(async () =>
        {
            using var lifetime = new CancellationTokenSource(); int sends = 0, reads = 0, reports = 0;
            var copy = new SelectionCopy(() => Target, () => true, () => 7, () => { sends++; return true; }, _ => true);
            var flow = new ClipboardTranslation(() => { reads++; return "old"; }, _ => throw new Exception("Late translation"), _ => reports++);
            var pending = flow.Run(lifetime.Token, token => copy.Run(Target, token));
            await flow.Run(lifetime.Token, token => copy.Run(Target, token)); lifetime.Cancel(); await pending;
            Assert(sends == 0 && reads == 0 && reports == 0);
        }));
    }
}
