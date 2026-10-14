using System;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal sealed class OverlayWindow : Window
{
    internal PixelRect Screen { get; }
    private readonly CaptureSession session;
    private readonly SelectionSurface surface;
    private readonly Canvas canvas;
    private readonly Border toolbar;
    private readonly List<Button> outputButtons = [];
    private Button rectangleButton = null!, lineButton = null!, arrowButton = null!, undoButton = null!, deleteButton = null!;
    private readonly ComboBox colorBox = new() { Width = 82, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox thicknessBox = new() { Width = 70, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    private StackPanel stylePanel = null!;
    private bool updatingStyle;
    private static readonly (string Name, uint Rgb)[] Colors = [("红色", 0xFF4545), ("黄色", 0xFFD447), ("绿色", 0x38D996), ("蓝色", 0x409CFF), ("白色", 0xFFFFFF), ("黑色", 0x000000)];
    private readonly TextBlock status = new() { Foreground = Brushes.White, FontSize = 12,
        MaxWidth = 480, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 6), Visibility = Visibility.Collapsed };

    internal OverlayWindow(CaptureSession session, PixelRect screen)
    {
        this.session = session; Screen = screen;
        Title = "Snap Tool · 区域截图";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        // Keep one capture entry reachable via Alt+Tab if another app takes keyboard focus.
        ShowInTaskbar = screen.Contains(session.Pointer); Topmost = true; ShowActivated = false;
        Background = Brushes.Black; UseLayoutRounding = true;
        Left = screen.Left; Top = screen.Top; Width = screen.Width; Height = screen.Height;
        surface = new SelectionSurface(this, session, session.Snapshot.Crop(screen));
        canvas = new Canvas { Background = Brushes.Transparent };
        var grid = new Grid(); grid.Children.Add(surface); grid.Children.Add(canvas);
        // The canvas itself must not consume input intended for the selection surface.
        canvas.Background = null;
        Content = grid;
        toolbar = CreateToolbar(); canvas.Children.Add(toolbar);
        SourceInitialized += (_, _) => PositionOnMonitor();
        Loaded += (_, _) => PositionOnMonitor();
        SizeChanged += (_, _) => { surface.InvalidateVisual(); if (toolbar.Visibility == Visibility.Visible) PlaceToolbar(); };
        PreviewKeyDown += OnKey;
        Closed += (_, _) => session.WindowClosed(this);
    }

    private void PositionOnMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!Native.SetWindowPos(handle, new IntPtr(-1), Screen.Left, Screen.Top, Screen.Width, Screen.Height, 0x0010 | 0x0040))
            throw new System.ComponentModel.Win32Exception();
    }

    private Border CreateToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        outputButtons.Add(AddButton(panel, "复制 Ctrl+C", "复制图片，Ctrl+C 或 Enter", async () => await session.Copy(this), true));
        outputButtons.Add(AddButton(panel, "贴图 Ctrl+V", "当前选区置顶贴图，Ctrl+V 或 F3", session.Pin));
        outputButtons.Add(AddButton(panel, "识别文字", "识别文字，Shift+C", async () => await session.RecognizeText()));
        outputButtons.Add(AddButton(panel, "翻译", "有道文本翻译，Shift+T", async () => await session.RecognizeText(true)));
        outputButtons.Add(AddButton(panel, "保存", "保存 PNG，Ctrl+S", () => session.Save(this)));
        AddButton(panel, "取消", "取消截图，Esc", () => session.Close());
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        rectangleButton = AddButton(tools, "矩形", "绘制矩形，R；Shift 正方形；再次点击或 V 退出绘制", () => session.ToggleTool(AnnotationKind.Rectangle));
        lineButton = AddButton(tools, "直线", "绘制直线，L；Shift 限制方向；再次点击或 V 退出绘制", () => session.ToggleTool(AnnotationKind.Line));
        arrowButton = AddButton(tools, "箭头", "绘制箭头，A；Shift 限制方向；再次点击或 V 退出绘制", () => session.ToggleTool(AnnotationKind.Arrow));
        undoButton = AddButton(tools, "撤销", "撤销标注，Ctrl+Z", session.UndoAnnotation);
        deleteButton = AddButton(tools, "删除", "删除选中标注，Delete", session.DeleteAnnotation);
        outputButtons.AddRange([rectangleButton, lineButton, arrowButton, undoButton, deleteButton]);
        stylePanel = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        stylePanel.Children.Add(new TextBlock { Text = "颜色", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0) });
        foreach (var color in Colors) colorBox.Items.Add(new ComboBoxItem { Content = color.Name, Tag = color.Rgb });
        colorBox.SelectedIndex = 0; stylePanel.Children.Add(colorBox);
        stylePanel.Children.Add(new TextBlock { Text = "粗细", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0) });
        foreach (int width in new[] { 2, 3, 5, 8 }) thicknessBox.Items.Add(new ComboBoxItem { Content = width + " px", Tag = width });
        thicknessBox.SelectedIndex = 1; stylePanel.Children.Add(thicknessBox);
        colorBox.SelectionChanged += (_, _) => ChangeStyle();
        thicknessBox.SelectionChanged += (_, _) => ChangeStyle();
        var content = new StackPanel(); content.Children.Add(panel); content.Children.Add(tools); content.Children.Add(stylePanel); content.Children.Add(status);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 30, 46)), CornerRadius = new CornerRadius(9),
            BorderBrush = new SolidColorBrush(Color.FromRgb(68, 81, 103)), BorderThickness = new Thickness(1),
            Padding = new Thickness(5), Child = content, Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.3 }
        };
    }

    private void ChangeStyle()
    {
        if (!updatingStyle && colorBox.SelectedItem is ComboBoxItem color && thicknessBox.SelectedItem is ComboBoxItem width)
            session.SetAnnotationStyle((uint)color.Tag, (int)width.Tag);
    }

    private static Button AddButton(Panel panel, string label, string tooltip, Action action, bool primary = false)
    {
        var button = new Button
        {
            Content = label, ToolTip = tooltip, FontSize = 13, FontFamily = new FontFamily("Microsoft YaHei UI"),
            Padding = new Thickness(15, 8, 15, 8), Margin = new Thickness(2), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(primary ? Color.FromRgb(92, 228, 197) : Color.FromRgb(40, 50, 70)),
            Foreground = primary ? new SolidColorBrush(Color.FromRgb(13, 40, 36)) : Brushes.White,
            Cursor = Cursors.Hand
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.8)); template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.6)); template.Triggers.Add(pressed);
        button.Template = template;
        button.Click += (_, _) => action(); panel.Children.Add(button);
        return button;
    }

    internal void HideToolbar() => toolbar.Visibility = Visibility.Collapsed;
    internal void ShowToolbar() { toolbar.Visibility = Visibility.Visible; Refresh(); }
    private void PlaceToolbar()
    {
        toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var r = session.Selection.Rectangle.Intersect(Screen);
        double sx = ActualWidth / Screen.Width, sy = ActualHeight / Screen.Height;
        double x = (r.Right - Screen.Left) * sx - toolbar.DesiredSize.Width;
        double y = (r.Bottom - Screen.Top) * sy + 12;
        if (y + toolbar.DesiredSize.Height > ActualHeight - 10)
            y = (r.Top - Screen.Top) * sy - toolbar.DesiredSize.Height - 12;
        Canvas.SetLeft(toolbar, Math.Clamp(x, 10, Math.Max(10, ActualWidth - toolbar.DesiredSize.Width - 10)));
        Canvas.SetTop(toolbar, Math.Clamp(y, 10, Math.Max(10, ActualHeight - toolbar.DesiredSize.Height - 10)));
    }

    internal void Refresh()
    {
        surface.InvalidateVisual();
        foreach (var button in outputButtons) { button.IsEnabled = !session.Busy; button.Opacity = session.Busy ? 0.5 : 1; }
        undoButton.IsEnabled = !session.Busy && session.Annotations.CanUndo;
        deleteButton.IsEnabled = !session.Busy && session.Annotating && session.Annotations.Selected != null;
        rectangleButton.Background = session.Tool == AnnotationKind.Rectangle ? Brushes.Teal : new SolidColorBrush(Color.FromRgb(40, 50, 70));
        lineButton.Background = session.Tool == AnnotationKind.Line ? Brushes.Teal : new SolidColorBrush(Color.FromRgb(40, 50, 70));
        arrowButton.Background = session.Tool == AnnotationKind.Arrow ? Brushes.Teal : new SolidColorBrush(Color.FromRgb(40, 50, 70));
        stylePanel.Visibility = session.Annotating ? Visibility.Visible : Visibility.Collapsed;
        colorBox.IsEnabled = thicknessBox.IsEnabled = !session.Busy;
        updatingStyle = true;
        foreach (ComboBoxItem item in colorBox.Items) if ((uint)item.Tag == session.Annotations.Rgb) colorBox.SelectedItem = item;
        foreach (ComboBoxItem item in thicknessBox.Items) if ((int)item.Tag == session.Annotations.Thickness) thicknessBox.SelectedItem = item;
        updatingStyle = false;
        status.Text = !string.IsNullOrEmpty(session.Status) ? session.Status : session.Tool switch
        {
            AnnotationKind.Rectangle => "拖动画框 · 拖动框线移动 · 拖动控制点缩放",
            AnnotationKind.Line or AnnotationKind.Arrow => "拖动绘制 · 拖动线身移动 · 拖动端点调整 · Shift 限制方向",
            _ => "拖动框内移动 · 拖动截图边缘或角点调整大小"
        };
        status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (toolbar.Visibility == Visibility.Visible) PlaceToolbar();
    }
    private async void OnKey(object sender, KeyEventArgs e) => await HandleKey(e, Keyboard.Modifiers);

    internal async Task HandleKey(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (e.Key == Key.Escape && (!session.Busy || session.Recognizing))
        { e.Handled = true; session.Close(); return; }
        if (session.Busy) { e.Handled = true; return; }
        if (e.Key == Key.C && modifiers == ModifierKeys.Control)
        { e.Handled = true; await session.Copy(this); }
        else if (e.Key == Key.V && modifiers == ModifierKeys.Control)
        { e.Handled = true; session.Pin(); }
        else if (e.Key == Key.F3 && modifiers == ModifierKeys.None)
        { e.Handled = true; session.Pin(); }
        else if (e.Key == Key.R && modifiers == ModifierKeys.None)
        { e.Handled = true; session.SetTool(AnnotationKind.Rectangle); }
        else if (e.Key == Key.L && modifiers == ModifierKeys.None)
        { e.Handled = true; session.SetTool(AnnotationKind.Line); }
        else if (e.Key == Key.A && modifiers == ModifierKeys.None)
        { e.Handled = true; session.SetTool(AnnotationKind.Arrow); }
        else if (e.Key == Key.V && modifiers == ModifierKeys.None)
        { e.Handled = true; session.SetTool(null); }
        else if (e.Key == Key.Z && modifiers == ModifierKeys.Control)
        { e.Handled = true; session.UndoAnnotation(); }
        else if (e.Key == Key.Delete && modifiers == ModifierKeys.None)
        { e.Handled = true; session.DeleteAnnotation(); }
        else if (e.Key == Key.C && modifiers == ModifierKeys.Shift)
        { e.Handled = true; await session.RecognizeText(); }
        else if (e.Key == Key.T && modifiers == ModifierKeys.Shift)
        { e.Handled = true; await session.RecognizeText(true); }
        else if (e.Key == Key.Enter && e.OriginalSource is not Button)
        { e.Handled = true; await session.Copy(this); }
        else if (e.Key == Key.S && modifiers == ModifierKeys.Control)
        { e.Handled = true; session.Save(this); }
    }
}

internal sealed class SelectionSurface : FrameworkElement
{
    private readonly OverlayWindow window;
    private readonly CaptureSession session;
    private readonly BitmapSource background;
    private static readonly Brush Shade = new SolidColorBrush(Color.FromArgb(150, 5, 10, 20));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(92, 228, 197));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(23, 30, 46));
    private static readonly Typeface Typeface = new("Microsoft YaHei UI");

    internal SelectionSurface(OverlayWindow window, CaptureSession session, BitmapSource background)
    {
        this.window = window; this.session = session; this.background = background;
        Cursor = Cursors.Cross; Focusable = true; ClipToBounds = true;
        MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up;
        LostMouseCapture += (_, _) => { if (session.Dragging) session.End(Native.CursorPosition(), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); };
        MouseRightButtonDown += (_, e) => { if (!session.Busy || session.Recognizing) { e.Handled = true; session.Close(); } };
    }

    private int Tolerance => Math.Max(1, (int)Math.Round(6 * window.Screen.Width / Math.Max(1, ActualWidth)));
    private async void Down(object sender, MouseButtonEventArgs e)
    {
        if (session.Busy) return;
        var p = Native.CursorPosition();
        if (!session.Annotating && e.ClickCount == 2 && session.HitTest(p, Tolerance) == (true, SelectionHit.Move))
        { e.Handled = true; await session.Copy(window); return; }
        Focus(); session.Begin(p, Tolerance); if (session.Dragging) CaptureMouse(); e.Handled = true;
    }
    private void Move(object sender, MouseEventArgs e)
    {
        if (session.Busy) return;
        var p = Native.CursorPosition(); session.Move(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        var target = session.HitTest(p, Tolerance);
        var hit = target.Hit;
        Cursor = hit switch
        {
            SelectionHit.Move => Cursors.SizeAll,
            SelectionHit.Left or SelectionHit.Right when !target.Selection && session.Annotations.Selected?.Kind != AnnotationKind.Rectangle => Cursors.Cross,
            SelectionHit.Left or SelectionHit.Right => Cursors.SizeWE,
            SelectionHit.Top or SelectionHit.Bottom => Cursors.SizeNS,
            (SelectionHit.Left | SelectionHit.Top) or (SelectionHit.Right | SelectionHit.Bottom) => Cursors.SizeNWSE,
            (SelectionHit.Right | SelectionHit.Top) or (SelectionHit.Left | SelectionHit.Bottom) => Cursors.SizeNESW,
            _ => Cursors.Cross
        };
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!session.Dragging) return;
        session.End(Native.CursorPosition(), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); ReleaseMouseCapture(); e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawImage(background, viewport);
        var selected = session.Selection.Rectangle;
        var screen = window.Screen;
        double sx = ActualWidth / screen.Width, sy = ActualHeight / screen.Height;
        if (!selected.IsEmpty)
        {
            var r = new Rect((selected.Left - screen.Left) * sx, (selected.Top - screen.Top) * sy, selected.Width * sx, selected.Height * sy);
            var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(viewport), new RectangleGeometry(r));
            dc.DrawGeometry(Shade, null, shade);
            dc.PushClip(new RectangleGeometry(r));
            dc.PushTransform(new MatrixTransform(sx, 0, 0, sy, -screen.Left * sx, -screen.Top * sy));
            AnnotationRenderer.Draw(dc, session.Annotations.Items);
            dc.Pop();
            if (session.Annotating && session.Annotations.Selected is { } mark && !session.Dragging)
                foreach (var handle in Annotations.Handles(mark))
                {
                    double hx = (handle.Point.X - screen.Left) * sx, hy = (handle.Point.Y - screen.Top) * sy;
                    dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(hx - 3, hy - 3, 6, 6));
                }
            dc.Pop();
            dc.DrawRectangle(null, new Pen(Accent, 1.5), r);
            if (!session.Dragging)
            {
                foreach (double x in new[] { r.Left, (r.Left + r.Right) / 2, r.Right })
                    foreach (double y in new[] { r.Top, (r.Top + r.Bottom) / 2, r.Bottom })
                        if (x != (r.Left + r.Right) / 2 || y != (r.Top + r.Bottom) / 2)
                            dc.DrawRectangle(Panel, new Pen(Accent, 1), new Rect(x - 3, y - 3, 6, 6));
            }
            if (!screen.Intersect(selected).IsEmpty)
            {
                var text = Text(selected.Width + " × " + selected.Height + " px", 12, Brushes.White);
                double x = Math.Clamp(r.Left, 8, Math.Max(8, ActualWidth - text.Width - 26));
                double y = r.Top >= 34 ? r.Top - 32 : Math.Max(8, r.Top + 12);
                y = Math.Min(y, Math.Max(8, ActualHeight - 34));
                dc.DrawRoundedRectangle(Panel, null, new Rect(x, y, text.Width + 20, 25), 5, 5);
                dc.DrawText(text, new Point(x + 10, y + 4));
            }
        }
        else
        {
            dc.DrawRectangle(Shade, null, viewport);
            if (screen.Contains(session.Pointer))
            {
                double x = (session.Pointer.X - screen.Left) * sx, y = (session.Pointer.Y - screen.Top) * sy;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
                dc.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
                dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
            }
            DrawHint(dc, "SNAP TOOL   /   区域截图", "拖动框选区域 · Esc 或右键取消", 32);
        }
    }

    private FormattedText Text(string value, double size, Brush brush) =>
        new(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, Typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawHint(DrawingContext dc, string heading, string detail, double top)
    {
        var title = Text(heading, 15, Accent); var subtitle = Text(detail, 12, Brushes.White);
        double width = Math.Max(title.Width, subtitle.Width) + 48, left = (ActualWidth - width) / 2;
        dc.DrawRoundedRectangle(Panel, new Pen(new SolidColorBrush(Color.FromRgb(58, 72, 95)), 1), new Rect(left, top, width, 76), 12, 12);
        dc.DrawText(title, new Point(left + 24, top + 15));
        dc.DrawText(subtitle, new Point(left + 24, top + 43));
    }
}
