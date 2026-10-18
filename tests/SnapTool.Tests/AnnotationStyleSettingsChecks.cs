using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class AnnotationStyleSettingsChecks
{
    private static readonly PixelRect Bounds = new(0, 0, 400, 300);
    private static void Assert(bool value) { if (!value) throw new Exception("Annotation style settings check failed."); }
    private static CaptureSession Session(AnnotationStyleProfile profile, Action<AnnotationStyleProfile>? save = null) =>
        new(new(Bounds, [Bounds], OcrChecks.Sample(true, 400, 300)), () => { }, (_, _) => { }, (_, _) => Task.FromResult(""),
            initialStyle: profile, rememberStyle: save);
    private static string NewPath()
    {
        string directory = Path.Combine(Program.FindRoot(), ".tmp", "annotation-style-settings"); Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException) { return; }
        throw new Exception("Invalid annotation settings were accepted.");
    }

    internal static void Run(Action<string, Action> test)
    {
        test("annotation settings restore defaults and roundtrip styles without marks or undo history", () =>
        {
            string path = NewPath();
            try
            {
                var settings = new AnnotationStyleSettings(path);
                Assert(settings.Load() == AnnotationStyleProfile.From(new Annotations()));
                var profile = new AnnotationStyleProfile { Rgb = 0x123456, Thickness = 5, MosaicSize = 24,
                    Text = new() { FontFamily = "Segoe UI", FontSize = 32, Bold = true, Italic = true, Outline = false, Background = true } };
                settings.Save(profile); var restored = new AnnotationStyleSettings(path).Load();
                Assert(restored == profile);
                var marks = restored.CreateAnnotations();
                Assert(marks.Items.Count == 0 && !marks.CanUndo && !marks.EditingText && marks.NextNumber == 1);
                Assert(AnnotationStyleProfile.From(marks) == profile);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        test("text toolbar custom size bold and italic persist into a fresh capture and fresh settings instance", () =>
        {
            string path = NewPath(); var settings = new AnnotationStyleSettings(path);
            var session = Session(settings.Load(), settings.Save); session.Selection.Select(Bounds);
            var window = new OverlayWindow(session, Bounds);
            try
            {
                session.SetTool(AnnotationKind.Text); window.ShowToolbar(); window.Refresh();
                var content = (FrameworkElement)window.Content; content.Measure(new Size(400, 300)); content.Arrange(new Rect(0, 0, 400, 300)); content.UpdateLayout();
                var toggles = RefreshChecks.Descendants<ToggleButton>(content).ToArray();
                toggles.Single(t => Equals(t.Content, "B")).IsChecked = true;
                var italic = toggles.Single(t => Equals(t.Content, "I")); italic.IsChecked = true;
                italic.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var size = RefreshChecks.Descendants<ComboBox>(content).Single(c => AutomationProperties.GetName(c) == "文字字号");
                size.Text = "32";
                size.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle()),
                    Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert(session.Annotations.TextStyle.FontSize == 32 && session.Annotations.TextStyle.Bold && session.Annotations.TextStyle.Italic);
                Assert(session.TryEditText(window, new(20, 40), 2)); window.TextEditorBox!.Text = "synthetic-text-not-to-save";
                session.SetAnnotationStyle(0x123456, 8); session.Close();
                var next = Session(new AnnotationStyleSettings(path).Load());
                var nextWindow = new OverlayWindow(next, Bounds); next.Selection.Select(Bounds); next.SetTool(AnnotationKind.Text);
                try
                {
                    Assert(next.Annotations.Items.Count == 0 && !next.Annotations.CanUndo);
                    Assert(next.TryEditText(nextWindow, new(20, 40), 2));
                    var mark = next.Annotations.Selected!;
                    Assert(mark.TextStyle.FontSize == 32 && mark.TextStyle.Bold && mark.TextStyle.Italic && mark.Rgb == 0x123456);
                    Assert(!File.ReadAllText(path).Contains("synthetic-text-not-to-save"));
                }
                finally { nextWindow.Close(); next.Close(); }
            }
            finally { window.Close(); session.Close(); if (File.Exists(path)) File.Delete(path); }
        });
        test("shared color stroke and mosaic defaults persist independently of text styles", () =>
        {
            AnnotationStyleProfile saved = new(); var session = Session(saved, profile => saved = profile);
            session.Selection.Select(Bounds);
            try
            {
                session.SetAnnotationStyle(0x13579B, 5);
                session.SetTool(AnnotationKind.Mosaic); session.SetAnnotationStyle(0x13579B, 16);
                session.SetTextStyle(session.Annotations.TextStyle with { FontSize = 32, Italic = true });
                var next = Session(saved);
                try { Assert(next.Annotations.Rgb == 0x13579B && next.Annotations.Thickness == 5 && next.Annotations.MosaicSize == 16 && next.Annotations.TextStyle.FontSize == 32); }
                finally { next.Close(); }
            }
            finally { session.Close(); }
        });
        test("invalid annotation settings never replace a valid file and corrupt loads never rewrite it", () =>
        {
            string path = NewPath(); var settings = new AnnotationStyleSettings(path);
            try
            {
                settings.Save(new()); string before = File.ReadAllText(path);
                using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                { Reject(() => settings.Save(new() { Text = new() { FontSize = 32 } })); Assert(File.ReadAllText(path) == before); }
                Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp").Any());
                foreach (var invalid in new[] { new AnnotationStyleProfile { Rgb = 0x1000000 }, new() { Thickness = 0 }, new() { MosaicSize = 31 },
                    new() { Text = new() { FontSize = 201 } }, new() { Text = null! } })
                { Reject(() => settings.Save(invalid)); Assert(File.ReadAllText(path) == before); }
                foreach (string corrupt in new[] { "{", "null", "{\"Text\":null}", "{\"Text\":{\"FontSize\":0}}" })
                { File.WriteAllText(path, corrupt); Reject(() => settings.Load()); Assert(File.ReadAllText(path) == corrupt); }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        test("style save failure leaves the applied style usable and a later change can retry", () =>
        {
            bool fail = true; AnnotationStyleProfile memory = new();
            var session = Session(memory, profile => { memory = profile; if (fail) throw new IOException("synthetic failure"); });
            try
            {
                session.SetTextStyle(session.Annotations.TextStyle with { FontSize = 32, Italic = true });
                Assert(session.Annotations.TextStyle.FontSize == 32 && memory.Text.Italic && session.Status.Contains("未能保存"));
                fail = false; session.SetTextStyle(session.Annotations.TextStyle with { FontSize = 33 });
                Assert(memory.Text.FontSize == 33 && session.Status == "");
            }
            finally { session.Close(); }
        });
    }
}
