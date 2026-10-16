using System;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal sealed partial class OverlayWindow : Window
{
    internal PixelRect Screen { get; }
    private readonly CaptureSession session;
    private readonly SelectionSurface surface;
    private readonly Canvas canvas;
    private readonly Border toolbar;
    private readonly List<Button> outputButtons = [];
    private Button cancelButton = null!;
    private Button rectangleButton = null!, arrowButton = null!, textButton = null!, numberButton = null!, mosaicButton = null!, undoButton = null!;
    private TextBlock colorLabel = null!;
    private TextBlock thicknessLabel = null!;
    private readonly ComboBox colorBox = new() { Width = 82, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox thicknessBox = new() { Width = 70, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    private WrapPanel stylePanel = null!;
    private bool updatingStyle;
    private bool mosaicWidths;
    private static readonly Brush ToolBackground = CreateToolBackground();
    private static readonly Brush ActiveToolBackground = new SolidColorBrush(Color.FromRgb(215, 240, 233));

    private static Brush CreateToolBackground()
    {
        var brush = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        brush.Freeze(); return brush;
    }
    private static readonly (string Name, uint Rgb)[] Colors = [("红色", 0xFF4545), ("黄色", 0xFFD447), ("绿色", 0x38D996), ("蓝色", 0x409CFF), ("白色", 0xFFFFFF), ("黑色", 0x000000)];
    private readonly TextBlock status = new() { Foreground = Brushes.DimGray, FontSize = 12,
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
        PreviewMouseDown += (_, e) =>
        {
            if (session.TextEditor != null && e.OriginalSource is DependencyObject source &&
                !Inside(textEditor, source) && !Inside(textControls, source) && !Inside(colorBox, source))
                session.FinishTextEditing();
        };
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
        outputButtons.Add(AddButton(panel, "复制", "复制图片，Ctrl+C", async () => await session.Copy(this), true));
        outputButtons.Add(AddButton(panel, "贴图", "当前选区置顶贴图，Ctrl+T", session.Pin));
        outputButtons.Add(AddButton(panel, "长截图", "手动向下滚动并拼接截图", session.StartScroll));
        outputButtons.Add(AddButton(panel, "录屏", "录制选区为 GIF 动图；录屏窗口内 Ctrl+C 完成并复制，取消放弃", session.StartRecording));
        outputButtons.Add(AddButton(panel, "OCR", "识别文字，Shift+C", async () => await session.RecognizeText()));
        outputButtons.Add(AddButton(panel, "翻译", "有道文本翻译，Shift+T", async () => await session.RecognizeText(true)));
        outputButtons.Add(AddButton(panel, "保存", "保存 PNG，Ctrl+S", () => session.Save(this)));
        cancelButton = AddButton(panel, "取消", "取消截图，Esc", () => session.Close());
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        rectangleButton = AddButton(tools, "矩形", "绘制矩形；Shift 正方形；再次点击退出绘制", () => session.ToggleTool(AnnotationKind.Rectangle));
        arrowButton = AddButton(tools, "箭头", "绘制箭头；Shift 限制方向；再次点击退出绘制", () => session.ToggleTool(AnnotationKind.Arrow));
        textButton = AddButton(tools, "文字", "点击输入文字，双击已有文字编辑；拖动文字移动；再次点击退出", () => session.ToggleTool(AnnotationKind.Text));
        numberButton = AddButton(tools, "序号", "点击添加自增数字，从 1 开始；拖动已有序号移动；再次点击退出", () => session.ToggleTool(AnnotationKind.Number));
        mosaicButton = AddButton(tools, "马赛克", "拖动框选马赛克；拖动区域移动；控制点缩放；再次点击退出", () => session.ToggleTool(AnnotationKind.Mosaic));
        undoButton = AddButton(tools, "撤销", "撤销标注，Ctrl+Z", session.UndoAnnotation);
        outputButtons.AddRange([rectangleButton, arrowButton, textButton, numberButton, mosaicButton, undoButton]);
        stylePanel = new WrapPanel { Visibility = Visibility.Collapsed };
        colorLabel = new TextBlock { Text = "颜色", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0) };
        stylePanel.Children.Add(colorLabel);
        foreach (var color in Colors) colorBox.Items.Add(new ComboBoxItem { Content = color.Name, Tag = color.Rgb });
        colorBox.SelectedIndex = 0; stylePanel.Children.Add(colorBox);
        thicknessLabel = new TextBlock { Text = "粗细", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0) };
        stylePanel.Children.Add(thicknessLabel);
        foreach (int width in new[] { 2, 3, 5, 8 }) thicknessBox.Items.Add(new ComboBoxItem { Content = width + " px", Tag = width });
        foreach (ComboBoxItem item in thicknessBox.Items) if ((int)item.Tag == session.Annotations.Thickness) thicknessBox.SelectedItem = item;
        stylePanel.Children.Add(thicknessBox);
        colorBox.SelectionChanged += (_, _) => ChangeStyle();
        thicknessBox.SelectionChanged += (_, _) => ChangeStyle();
        textControls = new TextAnnotationControls(session.SetTextStyle);
        stylePanel.Children.Add(textControls);
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(tools);
        row.Children.Add(new Border { Width = 1, Height = 26, Background = Brushes.Gainsboro, Margin = new Thickness(8, 5, 8, 5) });
        row.Children.Add(panel);
        var content = new StackPanel(); content.Children.Add(row); content.Children.Add(stylePanel); content.Children.Add(status);
        return new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(12),
            BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(1),
            Padding = new Thickness(7), Child = content, Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.18 }
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
            Tag = label, ToolTip = tooltip,
            Width = 38, Height = 38, Padding = new Thickness(7), Margin = new Thickness(2), BorderThickness = new Thickness(0),
            Background = primary ? new SolidColorBrush(Color.FromRgb(229, 247, 241)) : ToolBackground,
            Foreground = new SolidColorBrush(Color.FromRgb(32, 35, 40)),
            Cursor = Cursors.Hand
        };
        var icon = new ToolbarIcon(label);
        icon.SetBinding(ToolbarIcon.StrokeProperty, new Binding(nameof(Button.Foreground)) { Source = button });
        button.Content = icon;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, tooltip);
        ToolTipService.SetInitialShowDelay(button, 300);
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "IconButtonChrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Gainsboro, "IconButtonChrome")); template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.6, "IconButtonChrome")); template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.3, "IconButtonChrome")); template.Triggers.Add(disabled);
        button.Template = template;
        button.Click += (_, _) => action(); panel.Children.Add(button);
        return button;
    }

    internal void HideToolbar() => toolbar.Visibility = Visibility.Collapsed;
    internal void ShowToolbar() { toolbar.Visibility = Visibility.Visible; Refresh(); }
    private void PlaceToolbar()
    {
        toolbar.MaxWidth = Math.Max(100, (ActualWidth > 0 ? ActualWidth : Width) - 20);
        textControls.MaxWidth = Math.Max(80, toolbar.MaxWidth - 20);
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

    internal void RefreshSurface() => surface.InvalidateVisual();

    internal void Refresh()
    {
        surface.InvalidateVisual();
        foreach (var button in outputButtons)
        {
            string? tooltip = (button.Tag as string) switch
            {
                "复制" => $"复制图片，{session.Shortcuts[ShortcutAction.Copy]}",
                "贴图" => $"当前选区置顶贴图，{session.Shortcuts[ShortcutAction.Pin]}",
                "OCR" => $"识别文字，{session.Shortcuts[ShortcutAction.Ocr]}",
                "翻译" => $"有道文本翻译，{session.Shortcuts[ShortcutAction.Translate]}",
                "保存" => $"保存 PNG，{session.Shortcuts[ShortcutAction.Save]}",
                "撤销" => $"撤销标注，{session.Shortcuts[ShortcutAction.Undo]}",
                "录屏" => $"录制选区为 GIF 动图；录屏窗口内 {session.Shortcuts[ShortcutAction.Copy]} 完成并复制，取消放弃",
                _ => null
            };
            if (tooltip != null)
            { button.ToolTip = tooltip; System.Windows.Automation.AutomationProperties.SetHelpText(button, tooltip); }
        }
        cancelButton.ToolTip = $"取消截图，{session.Shortcuts[ShortcutAction.Cancel]}";
        System.Windows.Automation.AutomationProperties.SetHelpText(cancelButton, (string)cancelButton.ToolTip);
        foreach (var button in outputButtons) { button.IsEnabled = !session.Busy; button.Opacity = session.Busy ? 0.5 : 1; }
        undoButton.IsEnabled = !session.Busy && session.Annotations.CanUndo;
        rectangleButton.Background = session.Tool == AnnotationKind.Rectangle ? ActiveToolBackground : ToolBackground;
        arrowButton.Background = session.Tool == AnnotationKind.Arrow ? ActiveToolBackground : ToolBackground;
        numberButton.Background = session.Tool == AnnotationKind.Number ? ActiveToolBackground : ToolBackground;
        mosaicButton.Background = session.Tool == AnnotationKind.Mosaic ? ActiveToolBackground : ToolBackground;
        textButton.Background = session.Tool == AnnotationKind.Text ? ActiveToolBackground : ToolBackground;
        stylePanel.Visibility = session.Annotating ? Visibility.Visible : Visibility.Collapsed;
        colorBox.IsEnabled = thicknessBox.IsEnabled = !session.Busy;
        updatingStyle = true;
        var styleKind = session.Annotations.Selected?.Kind ?? session.Tool;
        bool numberStyle = styleKind == AnnotationKind.Number, mosaicStyle = styleKind == AnnotationKind.Mosaic, textStyle = styleKind == AnnotationKind.Text;
        textControls.Visibility = textStyle ? Visibility.Visible : Visibility.Collapsed;
        textControls.IsEnabled = !session.Busy;
        textControls.Sync(session.Annotations.TextStyle);
        thicknessLabel.Visibility = thicknessBox.Visibility = textStyle ? Visibility.Collapsed : Visibility.Visible;
        colorLabel.Visibility = colorBox.Visibility = mosaicStyle ? Visibility.Collapsed : Visibility.Visible;
        thicknessLabel.Text = numberStyle ? "直径" : mosaicStyle ? "粒度" : "粗细";
        if (mosaicWidths != mosaicStyle)
        {
            mosaicWidths = mosaicStyle; thicknessBox.Items.Clear();
            foreach (int width in mosaicStyle ? new[] { 8, 16, 24, 30 } : new[] { 2, 3, 5, 8 })
                thicknessBox.Items.Add(new ComboBoxItem { Content = width + " px", Tag = width });
        }
        foreach (ComboBoxItem item in thicknessBox.Items) item.Content = (numberStyle ? 16 + (int)item.Tag * 4 : (int)item.Tag) + " px";
        foreach (ComboBoxItem item in colorBox.Items) if ((uint)item.Tag == session.Annotations.Rgb) colorBox.SelectedItem = item;
        int styleWidth = mosaicStyle ? session.Annotations.MosaicSize : session.Annotations.Thickness;
        foreach (ComboBoxItem item in thicknessBox.Items) if ((int)item.Tag == styleWidth) thicknessBox.SelectedItem = item;
        updatingStyle = false;
        RefreshTextEditor();
        status.Text = session.Status;
        status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (toolbar.Visibility == Visibility.Visible) PlaceToolbar();
    }
    private async void OnKey(object sender, KeyEventArgs e) => await HandleKey(e, Keyboard.Modifiers);

    internal async Task HandleKey(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (textEditor != null && (textEditor.IsKeyboardFocusWithin || Inside(textEditor, e.OriginalSource as DependencyObject)))
        {
            if (e.Key == Key.Escape) { e.Handled = true; session.FinishTextEditing(true); surface.Focus(); }
            else if (e.Key == Key.Enter && modifiers == ModifierKeys.Control) { e.Handled = true; session.FinishTextEditing(); surface.Focus(); }
            return;
        }
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (session.Shortcuts.Matches(ShortcutAction.Cancel, e, modifiers) && (!session.Busy || session.Recognizing))
        { e.Handled = true; session.Close(); return; }
        if (session.Busy) { e.Handled = true; return; }
        if (session.Shortcuts.Matches(ShortcutAction.Copy, e, modifiers))
        { e.Handled = true; await session.Copy(this); }
        else if (session.Shortcuts.Matches(ShortcutAction.Pin, e, modifiers))
        { e.Handled = true; session.Pin(); }
        else if (session.Shortcuts.Matches(ShortcutAction.Undo, e, modifiers))
        { e.Handled = true; session.UndoAnnotation(); }
        else if (session.Shortcuts.Matches(ShortcutAction.Delete, e, modifiers))
        { e.Handled = true; session.DeleteAnnotation(); }
        else if (session.Shortcuts.Matches(ShortcutAction.Ocr, e, modifiers))
        { e.Handled = true; await session.RecognizeText(); }
        else if (session.Shortcuts.Matches(ShortcutAction.Translate, e, modifiers))
        { e.Handled = true; await session.RecognizeText(true); }
        else if (session.Shortcuts.Matches(ShortcutAction.Save, e, modifiers))
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
        session.FinishTextEditing();
        var p = Native.CursorPosition();
        if (session.TryEditText(window, p, Tolerance, e.ClickCount == 2)) { e.Handled = true; return; }
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
            SelectionHit.Left or SelectionHit.Right when !target.Selection && session.Annotations.Selected?.Kind is not (AnnotationKind.Rectangle or AnnotationKind.Mosaic) => Cursors.Cross,
            SelectionHit.Left or SelectionHit.Right => Cursors.SizeWE,
            SelectionHit.Top or SelectionHit.Bottom => Cursors.SizeNS,
            (SelectionHit.Left | SelectionHit.Top) or (SelectionHit.Right | SelectionHit.Bottom) => Cursors.SizeNWSE,
            (SelectionHit.Right | SelectionHit.Top) or (SelectionHit.Left | SelectionHit.Bottom) => Cursors.SizeNESW,
            _ => session.Tool == AnnotationKind.Text && !target.Selection ? Cursors.IBeam : Cursors.Cross
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
        var selected = session.DisplayRectangle;
        var screen = window.Screen;
        double sx = ActualWidth / screen.Width, sy = ActualHeight / screen.Height;
        if (!selected.IsEmpty)
        {
            var r = new Rect((selected.Left - screen.Left) * sx, (selected.Top - screen.Top) * sy, selected.Width * sx, selected.Height * sy);
            var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(viewport), new RectangleGeometry(r));
            dc.DrawGeometry(Shade, null, shade);
            dc.PushClip(new RectangleGeometry(r));
            dc.PushTransform(new MatrixTransform(sx, 0, 0, sy, -screen.Left * sx, -screen.Top * sy));
            var marks = session.Annotations.Items;
            AnnotationRenderer.Draw(dc, session.Annotations.EditingText ? System.Linq.Enumerable.Where(marks, mark => !ReferenceEquals(mark, session.Annotations.Selected)) : marks, session.Mosaic);
            dc.Pop();
            if (session.Annotating && session.Annotations.Selected is { } mark && !session.Dragging && !session.Annotations.EditingText)
                foreach (var handle in Annotations.Handles(mark))
                {
                    double hx = (handle.Point.X - screen.Left) * sx, hy = (handle.Point.Y - screen.Top) * sy;
                    dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(hx - 3, hy - 3, 6, 6));
                }
            dc.Pop();
            dc.DrawRectangle(null, new Pen(Accent, 1.5), r);
            if (!session.Dragging && !session.AutoSelecting)
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
            DrawHint(dc, "SNAP TOOL   /   区域截图", $"拖动框选区域 · {session.Shortcuts[ShortcutAction.Cancel]} 或右键取消", 32);
        }
        if (session.AutoSelecting && !selected.IsEmpty && screen.Contains(session.Pointer))
            DrawHint(dc, "自动识别窗口", $"单击固定窗口选区 · 拖动自行框选 · {session.Shortcuts[ShortcutAction.Cancel]} 或右键取消", 32);
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
