using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SnapTool;

internal static class TranslationUi
{
    internal static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(27, 39, 57));
    internal static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(86, 103, 124));
    internal static TextBlock Text(string text, double size = 14) => new()
    { Text = text, FontSize = size, Foreground = Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    internal static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(0, 0, 10, 0),
            Background = primary ? new SolidColorBrush(Color.FromRgb(92, 228, 197)) : Brushes.White,
            Foreground = Ink, BorderBrush = new SolidColorBrush(Color.FromRgb(210, 219, 228)), Cursor = Cursors.Hand };
        button.Click += (_, _) => action(); return button;
    }
    internal static void Style(Window window)
    {
        window.FontFamily = new FontFamily("Microsoft YaHei UI"); window.FontSize = 14;
        window.Background = new SolidColorBrush(Color.FromRgb(245, 248, 251));
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
}

internal sealed class TranslationSettingsPanel : ScrollViewer
{
    internal TranslationSettingsPanel(Func<YoudaoCredentials> load, Action<YoudaoCredentials> save,
        Func<bool>? loadCopySelection = null, Action<bool>? saveCopySelection = null)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = panel; VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        panel.Children.Add(TranslationUi.Text("快捷翻译", 23));
        var copySelection = new CheckBox { Content = "翻译前自动复制选中文字（仅快捷键触发）", IsChecked = true, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(copySelection);
        panel.Children.Add(TranslationUi.Text("勾选后：选中文字，按翻译快捷键即可自动复制并翻译。取消勾选：翻译已复制的文本。托盘菜单始终翻译已有剪贴板。", 12));
        var behaviorStatus = TranslationUi.Text("", 12); panel.Children.Add(behaviorStatus);
        try { copySelection.IsChecked = loadCopySelection?.Invoke() ?? true; }
        catch { behaviorStatus.Text = "快捷翻译配置无法读取，请选择后重新保存。"; }
        panel.Children.Add(TranslationUi.Button("保存翻译行为", () =>
        {
            try { saveCopySelection?.Invoke(copySelection.IsChecked == true); behaviorStatus.Text = "翻译行为已保存，下次启动继续使用。"; }
            catch { behaviorStatus.Text = "保存失败，原设置保持不变，请重试。"; }
        }));
        panel.Children.Add(TranslationUi.Text("连接有道文本翻译", 23));
        panel.Children.Add(TranslationUi.Text("请在有道智云创建应用，并绑定“文本翻译”服务。这里填写的是 API 应用凭据，不是有道账号密码。"));
        panel.Children.Add(TranslationUi.Text("应用 ID"));
        var appId = new TextBox { Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 15) };
        panel.Children.Add(appId);
        panel.Children.Add(TranslationUi.Text("应用密钥"));
        var secret = new PasswordBox { Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 15) };
        panel.Children.Add(secret);
        panel.Children.Add(TranslationUi.Text("仅在主动翻译时发送原文给有道，不上传截图。密钥加密保存在当前 Windows 用户下；保存设置不会调用接口。"));
        var status = TranslationUi.Text(""); status.Foreground = Brushes.Firebrick; panel.Children.Add(status);
        try { var saved = load(); appId.Text = saved.AppId; secret.Password = saved.Secret; }
        catch { status.Text = "已有配置无法读取，请重新填写并保存。"; }
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(TranslationUi.Button("保存", () =>
        {
            try { save(new(appId.Text.Trim(), secret.Password.Trim())); status.Foreground = TranslationUi.Muted; status.Text = "翻译设置已保存。"; }
            catch (ArgumentException ex) { status.Foreground = Brushes.Firebrick; status.Text = ex.Message; }
            catch { status.Foreground = Brushes.Firebrick; status.Text = "保存失败，请检查当前用户的配置目录权限后重试。"; }
        }, true));
        panel.Children.Add(actions);
    }
}

internal sealed class TranslationWindow : Window
{
    internal TextBox SourceBox { get; } = CreateTextBox(false);
    internal TextBox ResultBox { get; } = CreateTextBox(true);
    internal ComboBox TargetBox { get; } = new() { Width = 135, Margin = new Thickness(8, 0, 18, 0), VerticalContentAlignment = VerticalAlignment.Center };
    internal Button TranslateButton { get; }
    private readonly Button copyResult;
    private readonly TextBlock status;
    private readonly TranslationSession session;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<string, CancellationToken, Task> copyText;
    private readonly Func<ShortcutProfile> getShortcuts;
    private bool closed, closing;

    internal TranslationWindow(string original, Func<string, string, CancellationToken, Task<string>> translate,
        Func<string, CancellationToken, Task>? copyText = null, Func<ShortcutProfile>? getShortcuts = null, bool fromClipboard = false)
    {
        this.copyText = copyText ?? ImageOutput.CopyText;
        this.getShortcuts = getShortcuts ?? (() => ShortcutProfile.Default);
        TranslationUi.Style(this); Title = "Snap Tool · 原文与译文";
        Width = 900; Height = 610; MinWidth = 730; MinHeight = 460;
        session = new TranslationSession(translate);
        var root = new Grid { Margin = new Thickness(25) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel(); heading.Children.Add(TranslationUi.Text(fromClipboard ? "剪贴板翻译" : "选区翻译", 25));
        heading.Children.Add(TranslationUi.Text(fromClipboard
            ? "有道文本翻译 · 仅发送本次复制的原文 · 可修改原文后重新翻译"
            : "有道文本翻译 · 仅发送原文，不上传截图 · 可修改 OCR 原文后重新翻译"));
        root.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 18) };
        actions.Children.Add(new TextBlock { Text = "译为", VerticalAlignment = VerticalAlignment.Center, Foreground = TranslationUi.Ink });
        TargetBox.Items.Add(new ComboBoxItem { Content = "简体中文", Tag = "zh-CHS" });
        TargetBox.Items.Add(new ComboBoxItem { Content = "英文", Tag = "en" }); TargetBox.SelectedIndex = 0;
        actions.Children.Add(TargetBox);
        TranslateButton = TranslationUi.Button("翻译", async () => await Translate(), true); actions.Children.Add(TranslateButton);
        Grid.SetRow(actions, 1); root.Children.Add(actions);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); columns.ColumnDefinitions.Add(new());
        FrameworkElement Pane(string title, TextBox text, Button copy)
        {
            var pane = new DockPanel(); var label = TranslationUi.Text(title, 16); DockPanel.SetDock(label, Dock.Top); pane.Children.Add(label);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            bottom.Children.Add(copy); DockPanel.SetDock(bottom, Dock.Bottom); pane.Children.Add(bottom); pane.Children.Add(text); return pane;
        }
        columns.Children.Add(Pane("原文 · 可编辑", SourceBox, TranslationUi.Button("复制原文", async () => await Copy(SourceBox.Text))));
        copyResult = TranslationUi.Button("复制译文", async () => await Copy(ResultBox.Text));
        var right = Pane("译文", ResultBox, copyResult); Grid.SetColumn(right, 2); columns.Children.Add(right);
        Grid.SetRow(columns, 2); root.Children.Add(columns);
        status = TranslationUi.Text("", 13); status.Margin = new Thickness(0, 16, 0, 0); status.Foreground = TranslationUi.Muted;
        Grid.SetRow(status, 3); root.Children.Add(status);
        SourceBox.Text = original;
        SourceBox.TextChanged += (_, _) => session.Invalidate();
        TargetBox.SelectionChanged += (_, _) => session.Invalidate();
        session.Changed += Refresh;
        Refresh();
        Loaded += async (_, _) => await Translate();
        Deactivated += (_, _) =>
        {
            if (closed || closing) return;
            // Finish native activation before closing, so the clicked window can receive focus.
            Dispatcher.BeginInvoke(new Action(() =>
            { if (!closed && !closing && !IsActive) Close(); }));
        };
        Closing += (_, _) => closing = true;
        PreviewKeyDown += (_, e) => HandleKey(e, Keyboard.Modifiers);
        Closed += (_, _) => { closed = true; session.Changed -= Refresh; session.Close(); lifetime.Cancel(); lifetime.Dispose(); };
    }

    internal void HandleKey(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (!getShortcuts().Matches(ShortcutAction.Cancel, e, modifiers)) return;
        e.Handled = true; if (session.Busy) session.Cancel(); else Close();
    }

    private static TextBox CreateTextBox(bool readOnly) => new()
    {
        IsReadOnly = readOnly, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(13),
        FontSize = 16, Foreground = TranslationUi.Ink, BorderBrush = new SolidColorBrush(Color.FromRgb(213, 221, 230))
    };

    private Task Translate() => session.Run(SourceBox.Text, (string)((ComboBoxItem)TargetBox.SelectedItem).Tag);
    private void Refresh()
    {
        if (closed) return;
        SourceBox.IsReadOnly = session.Busy; TargetBox.IsEnabled = !session.Busy;
        TranslateButton.IsEnabled = !session.Busy;
        ResultBox.Text = session.Result; status.Text = session.Status;
        copyResult.IsEnabled = !session.Busy && !string.IsNullOrWhiteSpace(session.Result);
    }
    private async Task Copy(string text)
    {
        if (closed || string.IsNullOrWhiteSpace(text)) return;
        try { await copyText(text, lifetime.Token); if (!closed) status.Text = "已复制，可以按 Ctrl+V 粘贴。"; }
        catch (OperationCanceledException) when (closed) { }
        catch { if (!closed) status.Text = "复制失败，请稍后重试。"; }
    }
}
