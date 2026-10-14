using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool;
using SnapTool.Core;

namespace SnapTool.Tests;

internal static class RefreshChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("idle pointer refresh allocation sample", () =>
        {
            var bounds = new PixelRect(0, 0, 400, 300);
            var s = new CaptureSession(new(bounds, [bounds], OcrChecks.Sample(true, 400, 300)),
                () => { }, (_, _) => { }, (_, _) => Task.FromResult(""));
            s.Selection.Select(new(20, 20, 300, 200));
            var window = new OverlayWindow(s, bounds);
            var windows = (List<OverlayWindow>)typeof(CaptureSession).GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s)!;
            windows.Add(window);
            var button = Descendants<Button>((DependencyObject)window.Content).Single(b => Equals(b.Content, "矩形"));
            var descriptor = DependencyPropertyDescriptor.FromProperty(Control.BackgroundProperty, typeof(Button));
            int changes = 0;
            EventHandler changed = (_, _) => changes++;
            descriptor.AddValueChanged(button, changed);
            try
            {
                for (int i = 0; i < 100; i++) s.Move(new(50 + i % 100, 80));
                changes = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) s.Move(new(50 + i % 100, 80));
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Console.WriteLine($"REFRESH 1000 idle moves: button changes={changes}, allocated bytes={allocated}");
                Assert(changes == 0);
            }
            finally { descriptor.RemoveValueChanged(button, changed); s.Close(); }
        });
        test("pointer refresh preserves crosshair window preview and dragging pixels", () =>
        {
            var bounds = new PixelRect(0, 0, 400, 300);
            var s = new CaptureSession(new(bounds, [bounds], OcrChecks.Sample(true, 400, 300)),
                () => { }, (_, _) => { }, (_, _) => Task.FromResult(""), windowTargets: [new(40, 100, 240, 250)]);
            var window = new OverlayWindow(s, bounds);
            var windows = (List<OverlayWindow>)typeof(CaptureSession).GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s)!;
            windows.Add(window);
            byte[] Render()
            {
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(400, 300)); content.Arrange(new Rect(0, 0, 400, 300)); content.UpdateLayout();
                var image = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
                image.Render(content);
                var pixels = new byte[400 * 300 * 4]; image.CopyPixels(pixels, 1600, 0); return pixels;
            }
            try
            {
                s.Move(new(300, 200)); var first = Render();
                s.Move(new(350, 200)); var second = Render(); Assert(!first.SequenceEqual(second));
                s.Move(new(100, 200)); var preview = Render(); Assert(!second.SequenceEqual(preview));
                s.Move(new(110, 200)); Assert(preview.SequenceEqual(Render()));
                s.Begin(new(110, 200), 6); s.End(new(110, 200)); var fixedSelection = Render();
                s.Move(new(150, 210)); Assert(fixedSelection.SequenceEqual(Render()));
                s.Begin(new(240, 250), 6); s.Move(new(300, 270)); Assert(!fixedSelection.SequenceEqual(Render()));
                s.End(new(300, 270)); s.SetTool(AnnotationKind.Arrow);
                s.Begin(new(70, 150), 2); s.Move(new(150, 200)); var arrow = Render();
                s.Move(new(210, 200)); Assert(!arrow.SequenceEqual(Render()));
            }
            finally { s.Close(); }
        });
    }

    private static void Assert(bool value) { if (!value) throw new Exception("Refresh assertion failed"); }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
