using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SnapTool;

internal sealed class SettingsWindow : Window
{
    internal SettingsWindow(ShortcutProfile current, Action<ShortcutProfile> saveShortcuts,
        Func<YoudaoCredentials> loadTranslation, Action<YoudaoCredentials> saveTranslation)
    {
        TranslationUi.Style(this); Title = "Snap Tool · 配置"; Width = 700; Height = 720; MinWidth = 650; MinHeight = 500;
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var close = TranslationUi.Button("关闭", Close); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        footer.Children.Add(TranslationUi.Text("两页独立保存，切换不保存；关闭时放弃未保存的修改。", 12));
        var tabs = new TabControl { Background = Background, BorderBrush = Brushes.LightGray };
        root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "快捷键", Padding = new Thickness(16, 8, 16, 8),
            Content = new ShortcutSettingsPanel(current, saveShortcuts) });
        var translation = new TabItem { Header = "翻译设置", Padding = new Thickness(16, 8, 16, 8) };
        tabs.Items.Add(translation);
        tabs.SelectionChanged += (_, _) =>
        {
            if (translation.IsSelected && translation.Content == null)
                translation.Content = new TranslationSettingsPanel(loadTranslation, saveTranslation);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
}

internal sealed class ShortcutSettingsPanel : UserControl
{
    private readonly Dictionary<ShortcutAction, (CheckBox Ctrl, CheckBox Alt, CheckBox Shift, ComboBox Key)> rows = new();
    private readonly TextBlock status;
    internal ShortcutSettingsPanel(ShortcutProfile current, Action<ShortcutProfile> save)
    {
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var heading = new StackPanel(); heading.Children.Add(TranslationUi.Text("快捷键配置", 24));
        heading.Children.Add(TranslationUi.Text("勾选组合键并选择按键。保存后立即生效，下次启动继续使用。"));
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(TranslationUi.Text("仅“开始截图”在全局生效，其余在对应窗口内生效。文字输入框保留标准编辑快捷键。", 12));
        status = TranslationUi.Text("", 12); status.Foreground = Brushes.Firebrick; bottom.Children.Add(status);
        var buttons = new WrapPanel(); bottom.Children.Add(buttons);
        buttons.Children.Add(TranslationUi.Button("保存", () =>
        {
            try { var profile = Read(); profile.Validate(); save(profile); status.Foreground = TranslationUi.Muted; status.Text = "快捷键已保存并生效。"; }
            catch (Exception ex) { status.Foreground = Brushes.Firebrick; status.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "保存失败，原配置保持不变。请检查配置目录权限后重试。"; }
        }, true));
        buttons.Children.Add(TranslationUi.Button("恢复默认", () => { Populate(ShortcutProfile.Default); status.Text = "已恢复默认，点击保存后生效。"; }));
        var list = new StackPanel(); root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var (action, label) in ShortcutProfile.Actions)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            row.Children.Add(new TextBlock { Text = label, Width = 158, VerticalAlignment = VerticalAlignment.Center });
            CheckBox Modifier(string text)
            {
                var box = new CheckBox { Content = text, Width = 64, VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(box, label + " " + text);
                row.Children.Add(box); return box;
            }
            var ctrl = Modifier("Ctrl"); var alt = Modifier("Alt"); var shift = Modifier("Shift");
            var key = new ComboBox { Width = 140, VerticalContentAlignment = VerticalAlignment.Center };
            foreach (var value in ShortcutProfile.Keys) key.Items.Add(new ComboBoxItem { Content = ShortcutGesture.KeyName(value), Tag = value });
            System.Windows.Automation.AutomationProperties.SetName(key, label + " 按键");
            row.Children.Add(key); list.Children.Add(row); rows.Add(action, (ctrl, alt, shift, key));
        }
        Populate(current);
    }

    private void Populate(ShortcutProfile profile)
    {
        foreach (var (action, row) in rows)
        {
            var gesture = profile[action];
            row.Ctrl.IsChecked = gesture.Modifiers.HasFlag(ModifierKeys.Control); row.Alt.IsChecked = gesture.Modifiers.HasFlag(ModifierKeys.Alt);
            row.Shift.IsChecked = gesture.Modifiers.HasFlag(ModifierKeys.Shift);
            foreach (ComboBoxItem item in row.Key.Items) if ((Key)item.Tag == gesture.Key) row.Key.SelectedItem = item;
        }
    }
    private ShortcutProfile Read()
    {
        var values = new Dictionary<ShortcutAction, ShortcutGesture>();
        foreach (var (action, row) in rows)
        {
            if (row.Key.SelectedItem is not ComboBoxItem key) throw new ArgumentException("请选择每项快捷键的按键。");
            var modifiers = (row.Ctrl.IsChecked == true ? ModifierKeys.Control : ModifierKeys.None) |
                (row.Alt.IsChecked == true ? ModifierKeys.Alt : ModifierKeys.None) | (row.Shift.IsChecked == true ? ModifierKeys.Shift : ModifierKeys.None);
            values.Add(action, new((Key)key.Tag, modifiers));
        }
        return new(values);
    }
}
