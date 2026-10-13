using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SnapTool.Core;

namespace SnapTool;

internal sealed class PinWindow : Window
{
    internal BitmapSource? Original { get; private set; }
    internal PinGeometry Geometry { get; }
    internal bool Busy { get; private set; }
    internal string Status { get; private set; } = "";
    private readonly Image picture;
    private readonly TextBlock status = new() { Foreground = Brushes.White, Background = Brushes.Black,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<BitmapSource, CancellationToken, Task> copy;
    private readonly Func<Window, string?> chooseSavePath;
    private bool dragging, positioning, closed;
    private PixelPoint dragAnchor, dragOrigin;

    internal PinWindow(BitmapSource image, PixelRect selection,
        Func<BitmapSource, CancellationToken, Task>? copy = null, Func<Window, string?>? chooseSavePath = null)
    {
        Original = image;
        this.copy = copy ?? ((source, token) => ImageOutput.Copy(source, token));
        this.chooseSavePath = chooseSavePath ?? ChooseSavePath;
        Geometry = new(image.PixelWidth, image.PixelHeight, new(selection.Left, selection.Top), WorkArea(new(selection.Left, selection.Top)));
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; Background = Brushes.Black;
        Width = Geometry.Bounds.Width; Height = Geometry.Bounds.Height;
        picture = new Image { Source = image, Stretch = Stretch.Fill,
            ToolTip = "拖动移动 · 滚轮缩放 · 双击关闭\nCtrl+C 复制 · Ctrl+S 保存 · Esc 关闭 · 右键恢复原始比例" };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var grid = new Grid(); grid.Children.Add(picture); grid.Children.Add(status); Content = grid;
        var menu = new ContextMenu();
        AddMenu(menu, "复制图片    Ctrl+C", async () => await CopyImage());
        AddMenu(menu, "保存 PNG    Ctrl+S", SaveImage);
        AddMenu(menu, "恢复原始比例", ResetScale);
        menu.Items.Add(new Separator()); AddMenu(menu, "关闭贴图    Esc", Close);
        ContextMenu = menu;
        menu.Opened += (_, _) => { foreach (var item in menu.Items) if (item is MenuItem action) action.IsEnabled = !Busy; };
        SourceInitialized += (_, _) => ApplyBounds();
        Loaded += (_, _) => ApplyBounds();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!closed) ApplyBounds(); }));
        MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) { e.Handled = true; Close(); }
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (closed || Busy || e.ClickCount > 1) return;
            Activate(); Focus();
            dragAnchor = Native.CursorPosition(); dragOrigin = new(Geometry.Bounds.Left, Geometry.Bounds.Top);
            dragging = CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, _) =>
        {
            if (!dragging) return;
            var p = Native.CursorPosition();
            Geometry.Move(new(dragOrigin.X + p.X - dragAnchor.X, dragOrigin.Y + p.Y - dragAnchor.Y), WorkArea(p)); ApplyBounds();
        };
        MouseLeftButtonUp += (_, _) => { dragging = false; ReleaseMouseCapture(); };
        LostMouseCapture += (_, _) => dragging = false;
        MouseWheel += (_, e) => { if (!Busy && !dragging) { Zoom(e.Delta > 0 ? 1.1 : 1 / 1.1); e.Handled = true; } };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await CopyImage(); }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SaveImage(); }
        };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); picture.Source = null; Original = null; };
    }

    internal static BitmapSource Detach(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        int stride = checked(image.PixelWidth * 4);
        var pixels = new byte[checked(stride * image.PixelHeight)]; converted.CopyPixels(pixels, stride, 0);
        var independent = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        independent.Freeze(); return independent;
    }

    private static void AddMenu(ContextMenu menu, string label, Action action)
    { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private static PixelRect WorkArea(PixelPoint point)
    {
        var r = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y)).WorkingArea;
        return new(r.Left, r.Top, r.Right, r.Bottom);
    }
    private PixelRect CurrentWorkArea() => WorkArea(new(Geometry.Bounds.Left + Geometry.Bounds.Width / 2, Geometry.Bounds.Top + Math.Min(20, Geometry.Bounds.Height / 2)));
    internal void Reposition() { Geometry.Move(new(Geometry.Bounds.Left, Geometry.Bounds.Top), CurrentWorkArea()); ApplyBounds(); }
    internal void Zoom(double factor) { if (Busy || closed) return; Geometry.Zoom(factor, CurrentWorkArea()); ApplyBounds(); }
    internal void ResetScale() { if (Busy || closed) return; Geometry.Reset(CurrentWorkArea()); ApplyBounds(); }
    private void ApplyBounds()
    {
        if (closed || positioning) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        positioning = true;
        try
        {
            var r = Geometry.Bounds;
            if (!Native.SetWindowPos(handle, new IntPtr(-1), r.Left, r.Top, r.Width, r.Height, 0x0010))
                throw new System.ComponentModel.Win32Exception();
            Title = $"Snap Tool · 贴图 {Geometry.Scale:P0}";
        }
        finally { positioning = false; }
    }
    private void SetStatus(string text)
    { Status = text; status.Text = text; status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    internal async Task CopyImage()
    {
        if (Busy || closed || dragging || Original == null) return;
        Busy = true; SetStatus("");
        var token = lifetime.Token;
        try { await copy(Original, token); if (!closed) SetStatus("已复制图片"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!closed) SetStatus("复制失败，请重试。贴图已保留。"); }
        finally { Busy = false; }
    }
    internal void SaveImage()
    {
        if (Busy || closed || dragging || Original == null) return;
        Busy = true; SetStatus("");
        try
        {
            string? path = chooseSavePath(this);
            if (path == null || closed) return;
            ImageOutput.SavePng(Original, path); SetStatus("图片已保存");
        }
        catch (Exception) { if (!closed) SetStatus("保存失败，请重试。贴图已保留。"); }
        finally { Busy = false; }
    }
    private static string? ChooseSavePath(Window owner)
    {
        var dialog = new SaveFileDialog { Title = "保存贴图", Filter = "PNG 图片 (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
            FileName = "Pin-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png", OverwritePrompt = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
