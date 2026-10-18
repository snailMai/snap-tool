using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SnapTool.Core;

namespace SnapTool;

internal sealed partial class OverlayWindow
{
    private TextAnnotationControls textControls = null!;
    private TextBox? textEditor;
    internal TextBox? TextEditorBox => textEditor;
    private static bool Inside(FrameworkElement? parent, DependencyObject? child) =>
        parent != null && child != null && (parent == child || parent.IsAncestorOf(child));

    internal void OpenTextEditor()
    {
        var mark = session.Annotations.Selected!;
        textEditor = new TextBox
        {
            Text = mark.Text, AcceptsReturn = true, AcceptsTab = false, MaxLength = 4096,
            TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinWidth = 100, MinHeight = 38,
            Padding = new Thickness(2), BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(1),
            Background = Brushes.Transparent, ToolTip = "Enter 换行，Ctrl+Enter 完成文字编辑，Esc 放弃本次编辑；点击其他位置完成。"
        };
        System.Windows.Automation.AutomationProperties.SetName(textEditor, "文字输入");
        textEditor.TextChanged += (_, _) => { if (textEditor != null) session.UpdateText(textEditor.Text); };
        canvas.Children.Add(textEditor);
        session.UpdateText(mark.Text);
        RefreshTextEditor(); textEditor.Focus(); textEditor.SelectAll();
    }

    internal void CloseTextEditor()
    {
        if (textEditor == null) return;
        var previous = textEditor; textEditor = null; canvas.Children.Remove(previous);
    }

    private void RefreshTextEditor()
    {
        if (textEditor == null || session.Annotations.Selected is not { } mark) return;
        double width = ActualWidth > 0 ? ActualWidth : Width, height = ActualHeight > 0 ? ActualHeight : Height;
        double sx = width / Screen.Width, sy = height / Screen.Height;
        double x = Math.Clamp((mark.Start.X - Screen.Left) * sx, 0, Math.Max(0, width - 110));
        double y = Math.Clamp((mark.Start.Y - Screen.Top) * sy, 0, Math.Max(0, height - 80));
        textEditor.MaxWidth = Math.Max(100, width - x - 8); textEditor.MaxHeight = Math.Max(60, height - y - 8);
        textEditor.FontFamily = new FontFamily(mark.TextStyle.FontFamily);
        textEditor.FontSize = mark.TextStyle.FontSize * sy;
        textEditor.FontWeight = mark.TextStyle.Bold ? FontWeights.Bold : FontWeights.Normal;
        textEditor.FontStyle = mark.TextStyle.Italic ? FontStyles.Italic : FontStyles.Normal;
        textEditor.Foreground = new SolidColorBrush(Color.FromRgb((byte)(mark.Rgb >> 16), (byte)(mark.Rgb >> 8), (byte)mark.Rgb));
        Canvas.SetLeft(textEditor, x); Canvas.SetTop(textEditor, y);
    }
}

internal sealed class TextAnnotationControls : WrapPanel
{
    private readonly ComboBox font = new() { Width = 180, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox size = new() { Width = 65, Margin = new Thickness(4), IsEditable = true, IsTextSearchEnabled = false, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ToggleButton bold, italic, outline, background;
    private readonly Action<TextAnnotationStyle> changed;
    private bool updating;

    internal TextAnnotationControls(Action<TextAnnotationStyle> changed)
    {
        this.changed = changed; Visibility = Visibility.Collapsed;
        bold = Toggle("B", "加粗"); bold.FontWeight = FontWeights.Bold;
        italic = Toggle("I", "斜体"); italic.FontStyle = FontStyles.Italic;
        outline = Toggle("A", "白色文字描边");
        background = Toggle("白底", "白色文字背景");
        foreach (string family in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) font.Items.Add(family);
        foreach (int value in new[] { 8, 12, 16, 20, 24, 29, 36, 48, 64, 96, 128, 200 }) size.Items.Add(value.ToString(CultureInfo.InvariantCulture));
        font.ToolTip = "字体"; size.ToolTip = "字号，8～200 个截图像素";
        System.Windows.Automation.AutomationProperties.SetName(font, "文字字体");
        System.Windows.Automation.AutomationProperties.SetName(size, "文字字号");
        Children.Add(font); Children.Add(size);
        font.SelectionChanged += (_, _) => Change(); size.SelectionChanged += (_, _) => { if (!updating) size.Dispatcher.BeginInvoke(new Action(Change)); };
        size.LostKeyboardFocus += (_, _) => Change();
        size.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Change(); } };
    }

    private ToggleButton Toggle(string label, string tooltip)
    {
        var button = new ToggleButton { Content = label, ToolTip = tooltip, Tag = "文字" + tooltip,
            MinWidth = 32, Height = 30, Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(2), FontSize = 15 };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => Change(); Children.Add(button); return button;
    }

    internal void Sync(TextAnnotationStyle style)
    {
        updating = true;
        if (!font.Items.Contains(style.FontFamily)) font.Items.Add(style.FontFamily);
        font.SelectedItem = style.FontFamily; size.Text = style.FontSize.ToString(CultureInfo.InvariantCulture);
        bold.IsChecked = style.Bold; italic.IsChecked = style.Italic; outline.IsChecked = style.Outline; background.IsChecked = style.Background;
        updating = false;
    }

    private void Change()
    {
        if (updating || font.SelectedItem is not string family || !int.TryParse(size.Text, out int value) || value < 8 || value > 200) return;
        changed(new() { FontFamily = family, FontSize = value, Bold = bold.IsChecked == true, Italic = italic.IsChecked == true,
            Outline = outline.IsChecked == true, Background = background.IsChecked == true });
    }
}
