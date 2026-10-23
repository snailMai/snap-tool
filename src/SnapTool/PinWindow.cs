using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using SnapTool.Core;

namespace SnapTool;

internal sealed class PinWindow : Window
{
    internal const int FrameInset = 9; // Eight physical pixels for the glow and one for the blue border.
    internal BitmapSource? Original { get; private set; }
    internal PinGeometry Geometry { get; }
    internal bool Busy { get; private set; }
    internal string Status { get; private set; } = "";
    internal bool Recognizing { get; private set; }
    internal string SelectedText => textLayer.Selection?.Text ?? "";
    internal OcrTextLayout? TextLayout { get; private set; }
    private readonly Image picture;
    private readonly PinTextLayer textLayer;
    private readonly MenuItem copyTextMenu, retryTextMenu;
    private readonly Border frame;
    private readonly DropShadowEffect glow = new() { Color = Color.FromRgb(75, 145, 245), ShadowDepth = 0, Opacity = 0.55 };
    private readonly TextBlock status = new() { Foreground = Brushes.White, Background = Brushes.Black,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<BitmapSource, CancellationToken, Task> copy;
    private readonly Func<Window, string?> chooseSavePath;
    private readonly Func<ShortcutProfile> getShortcuts;
    private readonly Func<BitmapSource, CancellationToken, Task<OcrTextLayout>>? recognize;
    private readonly Func<string, CancellationToken, Task> copyText;
    private bool dragging, selectingText, recognitionStarted, positioning, closed;
    private OcrTextLayout? pendingLayout;
    private PixelPoint dragAnchor, dragOrigin;

    internal PinWindow(BitmapSource image, PixelRect selection,
        Func<BitmapSource, CancellationToken, Task>? copy = null, Func<Window, string?>? chooseSavePath = null,
        Func<ShortcutProfile>? getShortcuts = null,
        Func<BitmapSource, CancellationToken, Task<OcrTextLayout>>? recognize = null,
        Func<string, CancellationToken, Task>? copyText = null)
    {
        Original = image;
        this.copy = copy ?? ((source, token) => ImageOutput.Copy(source, token));
        this.chooseSavePath = chooseSavePath ?? ChooseSavePath;
        this.getShortcuts = getShortcuts ?? (() => ShortcutProfile.Default);
        this.recognize = recognize; this.copyText = copyText ?? ImageOutput.CopyText;
        Geometry = new(image.PixelWidth, image.PixelHeight, new(selection.Left, selection.Top), WorkArea(new(selection.Left, selection.Top)));
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; Background = Brushes.Transparent;
        Width = Geometry.Bounds.Width + FrameInset * 2; Height = Geometry.Bounds.Height + FrameInset * 2;
        picture = new Image { Source = image, Stretch = Stretch.Fill,
            ToolTip = "拖动移动 · 滚轮缩放 · 双击关闭\nCtrl+C 复制 · Ctrl+S 保存 · Esc 关闭 · 右键恢复原始比例" };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        textLayer = new(image.PixelWidth, image.PixelHeight) { IsHitTestVisible = false };
        var grid = new Grid { Background = Brushes.White }; grid.Children.Add(picture); grid.Children.Add(textLayer); grid.Children.Add(status);
        frame = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(100, 170, 255)),
            Background = Brushes.White, Child = grid };
        // Shadow lives behind the image so it cannot blur the image or its one-pixel border.
        var shadow = new Border { Background = frame.BorderBrush, Effect = glow, IsHitTestVisible = false };
        var root = new Grid(); root.Children.Add(shadow); root.Children.Add(frame); Content = root;
        var menu = new ContextMenu();
        AddMenu(menu, "复制图片    Ctrl+C", async () => await CopyImage());
        AddMenu(menu, "保存 PNG    Ctrl+S", SaveImage);
        AddMenu(menu, "恢复原始比例", ResetScale);
        menu.Items.Add(new Separator()); AddMenu(menu, "关闭贴图    Esc", Close);
        menu.Items.Add(new Separator());
        copyTextMenu = AddMenu(menu, "复制选中文字", async () => await CopySelectedText());
        retryTextMenu = AddMenu(menu, "重新识别文字", async () => await RecognizeText(true));
        var borderBrush = frame.BorderBrush;
        var removeDecorations = AddMenu(menu, "去除边框和阴影", () => { });
        removeDecorations.IsCheckable = true;
        removeDecorations.Checked += (_, _) =>
        { shadow.Visibility = Visibility.Collapsed; frame.BorderBrush = Brushes.Transparent; frame.Background = Brushes.Transparent; };
        removeDecorations.Unchecked += (_, _) =>
        { frame.BorderBrush = borderBrush; frame.Background = Brushes.White; shadow.Visibility = Visibility.Visible; };
        ContextMenu = menu;
        RefreshShortcuts();
        menu.Opened += (_, _) =>
        {
            foreach (var item in menu.Items) if (item is MenuItem action) action.IsEnabled = !Busy;
            RefreshShortcuts();
        };
        SourceInitialized += (_, _) => ApplyBounds();
        Loaded += (_, _) => ApplyBounds();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!closed) ApplyBounds(); }));
        MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            HandleDoubleClick(ImagePoint(e.GetPosition(picture)));
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (closed || Busy || e.ClickCount > 1) return;
            Activate(); Focus();
            var point = ImagePoint(e.GetPosition(picture));
            if (BeginTextSelection(point)) { CaptureMouse(); e.Handled = true; return; }
            dragAnchor = Native.CursorPosition(); dragOrigin = new(Geometry.Bounds.Left, Geometry.Bounds.Top);
            dragging = CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            var point = ImagePoint(e.GetPosition(picture));
            if (selectingText) { UpdateTextSelection(point); return; }
            if (!dragging) { Cursor = TextLayout?.Hit(point.X, point.Y) >= 0 ? Cursors.IBeam : Cursors.Arrow; return; }
            var p = Native.CursorPosition();
            Geometry.Move(new(dragOrigin.X + p.X - dragAnchor.X, dragOrigin.Y + p.Y - dragAnchor.Y), WorkArea(p)); ApplyBounds();
        };
        MouseLeftButtonUp += (_, _) => EndPointerDrag();
        LostMouseCapture += (_, _) => { dragging = selectingText = false; ApplyPendingLayout(); };
        MouseWheel += (_, e) => { if (!Busy && !dragging && !selectingText) { Zoom(e.Delta > 0 ? 1.1 : 1 / 1.1); e.Handled = true; } };
        MouseEnter += async (_, _) => await RecognizeText();
        PreviewKeyDown += async (_, e) => await HandleKey(e, Keyboard.Modifiers);
        Closed += (_, _) =>
        {
            closed = true; lifetime.Cancel(); lifetime.Dispose(); picture.Source = null; Original = null;
            TextLayout = pendingLayout = null; textLayer.Selection = null; textLayer.InvalidateVisual();
        };
    }

    internal void RefreshShortcuts()
    {
        var keys = getShortcuts();
        picture.ToolTip = $"文字处拖选 · 空白拖动移动 · 滚轮缩放 · 空白双击关闭\n{keys[ShortcutAction.Copy]} 复制选中文字，无选字时复制图片 · {keys[ShortcutAction.Save]} 保存 · {keys[ShortcutAction.Cancel]} 关闭";
        if (ContextMenu == null) return;
        foreach (var (index, label, action) in new[] { (0, "复制图片", ShortcutAction.Copy), (1, "保存 PNG", ShortcutAction.Save), (4, "关闭贴图", ShortcutAction.Cancel) })
        {
            var item = (MenuItem)ContextMenu.Items[index]; item.Header = label; item.InputGestureText = keys[action].ToString();
        }
        if (SelectedText.Length > 0) ((MenuItem)ContextMenu.Items[0]).InputGestureText = "";
        copyTextMenu.InputGestureText = SelectedText.Length > 0 ? keys[ShortcutAction.Copy].ToString() : "";
        copyTextMenu.IsEnabled = !Busy && !string.IsNullOrWhiteSpace(SelectedText);
        retryTextMenu.IsEnabled = !Busy && !Recognizing && recognize != null;
    }

    internal async Task HandleKey(KeyEventArgs e, ModifierKeys modifiers)
    {
        var keys = getShortcuts();
        if (keys.Matches(ShortcutAction.Cancel, e, modifiers)) { e.Handled = true; Close(); }
        else if (keys.Matches(ShortcutAction.Copy, e, modifiers))
        { e.Handled = true; if (SelectedText.Length > 0) await CopySelectedText(); else await CopyImage(); }
        else if (keys.Matches(ShortcutAction.Save, e, modifiers)) { e.Handled = true; SaveImage(); }
    }

    internal Point ImagePoint(Point point) => new(point.X * (Original?.PixelWidth ?? 0) / (picture.ActualWidth > 0 ? picture.ActualWidth : 1),
        point.Y * (Original?.PixelHeight ?? 0) / (picture.ActualHeight > 0 ? picture.ActualHeight : 1));

    internal void HandleDoubleClick(Point point)
    { if (!selectingText && !(TextLayout?.Hit(point.X, point.Y) >= 0)) Close(); }

    internal bool BeginTextSelection(Point point)
    {
        if (closed || Busy) return false;
        if (!Recognizing) SetStatus("");
        selectingText = textLayer.Selection?.Begin(point.X, point.Y) == true;
        textLayer.InvalidateVisual(); RefreshShortcuts();
        if (selectingText) Cursor = Cursors.IBeam;
        return selectingText;
    }
    internal void UpdateTextSelection(Point point)
    { if (selectingText && !closed) { textLayer.Selection?.Update(point.X, point.Y); textLayer.InvalidateVisual(); RefreshShortcuts(); } }
    internal void EndPointerDrag()
    {
        dragging = selectingText = false; ReleaseMouseCapture(); ApplyPendingLayout();
    }
    private void ApplyPendingLayout()
    {
        if (closed || pendingLayout == null) return;
        TextLayout = pendingLayout; pendingLayout = null;
        textLayer.Selection = new(TextLayout); textLayer.InvalidateVisual(); RefreshShortcuts();
    }
    internal async Task RecognizeText(bool retry = false)
    {
        if (closed || recognize == null || Original == null || Recognizing || (recognitionStarted && !retry)) return;
        recognitionStarted = true; Recognizing = true; SetStatus("正在识别贴图文字…");
        var token = lifetime.Token;
        try
        {
            var layout = await recognize(Original, token);
            if (closed || token.IsCancellationRequested) return;
            pendingLayout = layout;
            if (!dragging && !selectingText) ApplyPendingLayout();
            SetStatus(layout.Glyphs.Count == 0 ? "未识别到可选文字，仍可移动或复制图片。" : "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { if (!closed) SetStatus("文字识别失败，图片已保留，可右键重新识别。"); }
        finally { Recognizing = false; if (!closed) RefreshShortcuts(); }
    }
    internal async Task CopySelectedText()
    {
        if (closed || Busy || dragging || selectingText || string.IsNullOrWhiteSpace(SelectedText)) return;
        Busy = true; SetStatus(""); var token = lifetime.Token;
        try { await copyText(SelectedText, token); if (!closed) SetStatus("已复制选中文字"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { if (!closed) SetStatus("复制文字失败，选区已保留，请重试。"); }
        finally { Busy = false; }
    }

    internal static BitmapSource Detach(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        BitmapSource converted = image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        int stride = checked(image.PixelWidth * 4);
        using var section = MemoryMappedFile.CreateNew(null, checked(stride * image.PixelHeight));
        int rows = Math.Min(image.PixelHeight, Math.Max(1, 65536 / stride));
        var pixels = new byte[checked(stride * rows)];
        using (var view = section.CreateViewAccessor())
        {
            for (int y = 0; y < image.PixelHeight; y += rows)
            {
                int height = Math.Min(rows, image.PixelHeight - y);
                converted.CopyPixels(new Int32Rect(0, y, image.PixelWidth, height), pixels, stride, 0);
                view.WriteArray((long)y * stride, pixels, 0, height * stride);
            }
        }
        // Only the independent section remains; WIC owns its read-only view after this handle closes.
        var independent = Imaging.CreateBitmapSourceFromMemorySection(section.SafeMemoryMappedFileHandle.DangerousGetHandle(),
            image.PixelWidth, image.PixelHeight, PixelFormats.Pbgra32, stride, 0);
        independent.Freeze(); return independent;
    }

    private static MenuItem AddMenu(ContextMenu menu, string label, Action action)
    { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); return item; }
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
            var dpi = VisualTreeHelper.GetDpi(this);
            frame.Margin = new Thickness(8 / dpi.DpiScaleX, 8 / dpi.DpiScaleY, 8 / dpi.DpiScaleX, 8 / dpi.DpiScaleY);
            frame.BorderThickness = new Thickness(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY, 1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
            ((Border)((Grid)Content).Children[0]).Margin = frame.Margin;
            glow.BlurRadius = 8 / dpi.DpiScaleX;
            if (!Native.SetWindowPos(handle, new IntPtr(-1), r.Left - FrameInset, r.Top - FrameInset,
                r.Width + FrameInset * 2, r.Height + FrameInset * 2, 0x0010))
                throw new System.ComponentModel.Win32Exception();
            Title = $"Snap Tool · 贴图 {Geometry.Scale:P0}";
        }
        finally { positioning = false; }
    }
    private void SetStatus(string text)
    { Status = text; status.Text = text; status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    internal async Task CopyImage()
    {
        if (Busy || closed || dragging || selectingText || Original == null) return;
        Busy = true; SetStatus("");
        var token = lifetime.Token;
        try { await copy(Original, token); if (!closed) SetStatus("已复制图片"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!closed) SetStatus("复制失败，请重试。贴图已保留。"); }
        finally { Busy = false; }
    }
    internal void SaveImage()
    {
        if (Busy || closed || dragging || selectingText || Original == null) return;
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
