using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal readonly record struct ScrollViewLayout(PixelRect Toolbar, PixelRect Preview)
{
    internal static ScrollViewLayout Calculate(PixelRect region, PixelRect work, int toolbarWidth, int toolbarHeight)
    {
        if (work.IsEmpty) throw new ArgumentException("Empty work area.");
        int width = Math.Min(work.Width, Math.Clamp(region.Width / 2, 180, 420));
        int right = work.Right - region.Right - 12, left = region.Left - work.Left - 12;
        int x;
        if (right >= 180) { width = Math.Min(width, right); x = region.Right + 12; }
        else if (left >= 180) { width = Math.Min(width, left); x = region.Left - 12 - width; }
        else x = work.Right - width;
        toolbarWidth = Math.Min(work.Width, toolbarWidth); toolbarHeight = Math.Min(work.Height, toolbarHeight);
        int height = Math.Min(Math.Max(200, region.Height), Math.Max(1, work.Height - toolbarHeight - 24));
        int y = Math.Clamp(region.Top, work.Top, work.Bottom - height);
        int tx = Math.Clamp(region.Left, work.Left, work.Right - toolbarWidth);
        int ty = region.Bottom + 10;
        if (ty + toolbarHeight > work.Bottom) ty = region.Top - toolbarHeight - 10;
        if (ty < work.Top) ty = work.Bottom - toolbarHeight;
        ty = Math.Clamp(ty, work.Top, work.Bottom - toolbarHeight);
        return new(new(tx, ty, tx + toolbarWidth, ty + toolbarHeight), new(x, y, x + width, y + height));
    }
}

internal static class ScrollCaptureView
{
    internal static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(53, 130, 255));
    internal static readonly Brush Green = new SolidColorBrush(Color.FromRgb(131, 226, 83));

    internal static void Exclude(Window window, bool clickThrough = false)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (clickThrough)
        {
            const long flags = 0x20 | 0x08000000; // WS_EX_TRANSPARENT | WS_EX_NOACTIVATE on a layered window.
            Native.SetWindowLongPtr(handle, -20, new(Native.GetWindowLongPtr(handle, -20).ToInt64() | flags));
            if ((Native.GetWindowLongPtr(handle, -20).ToInt64() & flags) != flags)
                throw new InvalidOperationException("无法启用滚动区域的鼠标穿透。");
        }
        if (!Native.SetWindowDisplayAffinity(handle, 0x11))
            throw new InvalidOperationException("无法将长截图界面排除在截图之外，请重试。");
    }

    internal static void Position(Window window, PixelRect bounds)
    {
        if (!Native.SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010))
            throw new System.ComponentModel.Win32Exception();
    }
}

internal sealed class ScrollRegionWindow : Window
{
    private readonly PixelRect screen;
    internal ScrollRegionWindow(PixelRect region, PixelRect screen)
    {
        this.screen = screen;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; Topmost = true; ShowActivated = false; ShowInTaskbar = false;
        Focusable = false; IsHitTestVisible = false; Content = new ScrollRegionSurface(region, screen);
    }
    internal void Start() { ScrollCaptureView.Exclude(this, true); Show(); ScrollCaptureView.Position(this, screen); }
}

internal sealed class ScrollRegionSurface(PixelRect region, PixelRect screen) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double sx = ActualWidth / screen.Width, sy = ActualHeight / screen.Height;
        var selected = new Rect((region.Left - screen.Left) * sx, (region.Top - screen.Top) * sy, region.Width * sx, region.Height * sy);
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)), new RectangleGeometry(selected));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, shade);
        dc.DrawRectangle(null, new Pen(ScrollCaptureView.Blue, 2 * sx), selected);
    }
}

internal sealed class ScrollPreviewWindow : Window
{
    internal ScrollPreviewSurface Surface { get; } = new();
    internal ScrollPreviewWindow()
    {
        Title = "Snap Tool · 实时长图"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        Content = new Border { BorderBrush = ScrollCaptureView.Blue, BorderThickness = new Thickness(2), Child = Surface };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        ToolTip = "实时拼接预览 · 绿色框为当前画面位置 · 可拖动预览";
        Closed += (_, _) => Surface.Clear();
    }
    internal void Start(PixelRect bounds) { ScrollCaptureView.Exclude(this); Show(); ScrollCaptureView.Position(this, bounds); }
}

internal sealed class ScrollPreviewSurface : FrameworkElement
{
    internal BitmapSource? Image { get; private set; }
    internal int TotalHeight { get; private set; }
    internal int ViewportHeight { get; private set; }
    internal bool Matched { get; private set; } = true;
    internal void Update(BitmapSource image, int totalHeight, int viewportHeight)
    { Image = image; TotalHeight = totalHeight; ViewportHeight = viewportHeight; InvalidateVisual(); }
    internal void SetMatched(bool value) { if (Matched != value) { Matched = value; InvalidateVisual(); } }
    internal void Clear() { Image = null; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        if (Image == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        double scale = Math.Min(ActualWidth / Image.PixelWidth, ActualHeight / Image.PixelHeight);
        double width = Image.PixelWidth * scale, height = Image.PixelHeight * scale, left = (ActualWidth - width) / 2;
        dc.DrawImage(Image, new Rect(left, 0, width, height));
        double currentHeight = height * Math.Min(ViewportHeight, TotalHeight) / Math.Max(1, TotalHeight);
        dc.DrawRectangle(null, new Pen(Matched ? ScrollCaptureView.Green : Brushes.Orange, 3),
            new Rect(left + 1.5, Math.Max(1.5, height - currentHeight), Math.Max(0, width - 3), Math.Max(0, currentHeight - 3)));
    }
}
