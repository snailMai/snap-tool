using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapTool.Tests;

// Dedicated synthetic content for manual capture tests. No user files are loaded.
internal sealed class FixtureWindow : Window
{
    private readonly TextBlock status;
    private readonly Image preview;
    internal FixtureWindow(bool secondary)
    {
        Title = secondary ? "Snap Tool · Test Pattern 2" : "Snap Tool · Test Pattern 1";
        var monitors = Native.Monitors().OrderBy(s => s.Left).ToArray();
        var screen = monitors[secondary && monitors.Length > 1 ? 1 : 0];
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Width = screen.Width; Height = screen.Height;
        void Position() => Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            screen.Left, screen.Top, screen.Width, screen.Height, 0x0010 | 0x0040);
        SourceInitialized += (_, _) => Position(); Loaded += (_, _) => Position();
        Background = new SolidColorBrush(Color.FromRgb(14, 21, 35));
        var panel = new StackPanel { Margin = new Thickness(40) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "SNAP TOOL / CAPTURE TEST", Foreground = Brushes.White, FontSize = 28, Margin = new Thickness(0, 0, 0, 15) });
        panel.Children.Add(new TextBlock { Text = "Synthetic content only · Ctrl+F2 to capture · Ctrl+V to inspect copied image", Foreground = Brushes.LightGray, FontSize = 14 });
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 30, 0, 15) };
        foreach (var (label, color) in new[] { ("MINT", "#5CE4C5"), ("BLUE", "#68A8FF"), ("GOLD", "#F5CF77"), ("CORAL", "#F58E91") })
            swatches.Children.Add(new Border { Width = 220, Height = 150, Background = (Brush)new BrushConverter().ConvertFromString(color)!,
                Child = new TextBlock { Text = label, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        panel.Children.Add(swatches);
        panel.Children.Add(new TextBlock { Text = "0123456789   ABCDEFGHIJKLMNOPQRSTUVWXYZ   截图像素与文字检查", Foreground = Brushes.White, FontSize = 20 });
        status = new TextBlock { Text = "Waiting for a capture…", Foreground = Brushes.LightGray, FontSize = 15, Margin = new Thickness(0, 20, 0, 10) };
        panel.Children.Add(status);
        preview = new Image { MaxWidth = 900, MaxHeight = 240, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(preview);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (Clipboard.ContainsText())
                {
                    status.Text = "Clipboard text:\n" + Clipboard.GetText();
                    preview.Source = null; return;
                }
                var image = Clipboard.GetImage();
                if (image == null) { status.Text = "Clipboard has no image"; return; }
                preview.Source = image;
                status.Text = $"Clipboard image: {image.PixelWidth} × {image.PixelHeight} px";
                var path = Path.Combine(Program.FindRoot(), ".tmp", "ui-validation", "clipboard.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = File.Create(path); var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
            }
        };
    }
}
