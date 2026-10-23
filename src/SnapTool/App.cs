using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapTool.Core;
using Forms = System.Windows.Forms;

namespace SnapTool;

internal sealed class App : Application
{
    private Mutex? instance;
    private HwndSource? source;
    private Forms.NotifyIcon? tray;
    private Icon? icon;
    private CaptureSession? session;
    private ScrollCaptureWindow? scrollCapture;
    private RecordingWindow? recording;
    private bool exiting;
    private bool sessionNotifications;
    private readonly ShortcutSettings shortcutSettings = new();
    private readonly AnnotationStyleSettings annotationStyleSettings = new();
    private AnnotationStyleProfile annotationStyle = new();
    private ShortcutProfile shortcuts = ShortcutProfile.Default;
    private GlobalHotkeys? hotkeys;
    private SettingsWindow? configuration;
    private Forms.ToolStripMenuItem? captureMenu;
    private Forms.ToolStripMenuItem? clipboardMenu;
    private TranslationWindow? clipboardWindow;
    private bool clipboardQueued;
    private readonly CancellationTokenSource clipboardLifetime = new();
    private readonly ClipboardTranslation clipboardTranslation;
    private readonly OcrService ocr = new();
    private readonly TranslationSettings translationSettings = new();
    private readonly TranslationBehaviorSettings translationBehaviorSettings = new();
    private readonly YoudaoTranslator translator = new();
    private readonly PinWindows pins;
    internal App()
    {
        pins = new(() => shortcuts, ocr.RecognizeLayout);
        clipboardTranslation = new(() => System.Windows.Clipboard.GetText(TextDataFormat.UnicodeText),
            ShowClipboardTranslation, message => UpdateStatus("剪贴板翻译", message));
    }

    [STAThread]
    public static void Main(string[] args)
    {
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(Native.SetCurrentProcessExplicitAppUserModelID(Native.ApplicationId));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => app.Start(args.Contains("--capture"));
        app.DispatcherUnhandledException += (_, e) =>
        {
            app.session?.Close();
            app.scrollCapture?.Close();
            if (app.recording != null) _ = app.recording.EndForSystemChange();
            MessageBox.Show("操作失败，请重试。\n" + e.Exception.Message, "Snap Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        app.Run();
    }

    private void Start(bool capture)
    {
        instance = new Mutex(true, @"Local\SnapTool.RegionCapture.v1", out bool created);
        if (!created)
        {
            instance.Dispose(); instance = null;
            MessageBox.Show("Snap Tool 已在运行，请使用已配置的截图快捷键，或从系统托盘打开。", "Snap Tool");
            Shutdown(); return;
        }
        source = new HwndSource(new HwndSourceParameters("Snap Tool Hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(WindowMessage);
        bool invalidSettings = false;
        bool invalidStyleSettings = false;
        try { shortcuts = shortcutSettings.Load(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { shortcuts = ShortcutProfile.Default; invalidSettings = true; }
        try { annotationStyle = annotationStyleSettings.Load(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { annotationStyle = new(); invalidStyleSettings = true; }
        hotkeys = new(
            (id, key) => Native.RegisterHotKey(source.Handle, id, (uint)key.Modifiers | 0x4000, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key.Key)),
            id => { if (source != null) Native.UnregisterHotKey(source.Handle, id); });
        icon = MakeIcon();
        var menu = new Forms.ContextMenuStrip();
        captureMenu = (Forms.ToolStripMenuItem)menu.Items.Add("截图", null, (_, _) => QueueCapture());
        clipboardMenu = (Forms.ToolStripMenuItem)menu.Items.Add("翻译剪贴板", null, (_, _) => QueueClipboardTranslation());
        menu.Items.Add("配置…", null, (_, _) => ShowConfiguration());
        menu.Items.Add("重新注册截图快捷键", null, (_, _) => RegisterHotkey(true));
        menu.Items.Add("关闭全部贴图", null, (_, _) => pins.CloseAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitSafely());
        tray = new Forms.NotifyIcon { Icon = icon, ContextMenuStrip = menu, Visible = true };
        RefreshShortcuts();
        tray.DoubleClick += (_, _) => QueueCapture();
        RegisterHotkey(false);
        try
        {
            hotkeys.Apply(ShortcutAction.ClipboardTranslate, shortcuts[ShortcutAction.ClipboardTranslate], () => { }); RefreshShortcuts();
            if (shortcuts[ShortcutAction.ClipboardTranslate].Key == System.Windows.Input.Key.None)
                UpdateStatus("剪贴板翻译快捷键未设置", "旧快捷键已保留，请在配置中设置，或使用托盘菜单。");
        }
        catch (InvalidOperationException)
        { RefreshShortcuts(); UpdateStatus("剪贴板翻译快捷键不可用", "可在配置中改键，或使用托盘“翻译剪贴板”。"); }
        sessionNotifications = Native.WTSRegisterSessionNotification(source.Handle, 0);
        if (invalidSettings) UpdateStatus("快捷键配置无法读取", "已临时使用默认快捷键；可在托盘右键“配置”中重新保存，原文件未覆盖。");
        else if (invalidStyleSettings) UpdateStatus("样式配置无法读取", "已临时使用默认样式；原文件未覆盖，重新修改样式后保存。");
        if (capture) QueueCapture();
    }

    private static Icon MakeIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(24, 31, 49));
            using var pen = new Pen(Color.FromArgb(92, 228, 197), 3);
            g.DrawLine(pen, 7, 5, 7, 24); g.DrawLine(pen, 7, 24, 27, 24);
            g.DrawLine(pen, 5, 9, 24, 9); g.DrawLine(pen, 24, 9, 24, 28);
        }
        var handle = bitmap.GetHicon();
        try { using var nativeIcon = Icon.FromHandle(handle); return (Icon)nativeIcon.Clone(); }
        finally { Native.DestroyIcon(handle); }
    }

    private void RegisterHotkey(bool notifySuccess)
    {
        try
        {
            hotkeys!.Apply(ShortcutAction.Capture, shortcuts[ShortcutAction.Capture], () => { });
            if (notifySuccess) UpdateStatus("快捷键可用", $"{shortcuts[ShortcutAction.Capture]} 已注册。");
        }
        catch (InvalidOperationException ex)
        { MessageBox.Show(ex.Message + "\n可通过托盘右键“配置”修改，或释放占用后重新注册。也可从托盘菜单截图。", "Snap Tool · 快捷键不可用", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ShowConfiguration()
    {
        if (configuration != null) { configuration.Activate(); return; }
        configuration = new(shortcuts, next =>
        {
            next.Validate(); hotkeys!.Apply(next, () => shortcutSettings.Save(next));
            shortcuts = next; RefreshShortcuts();
        }, translationSettings.Load, translationSettings.Save, translationBehaviorSettings.Load, translationBehaviorSettings.Save);
        try { configuration.ShowDialog(); }
        finally { configuration = null; }
    }

    private void RefreshShortcuts()
    {
        if (captureMenu != null) captureMenu.Text = $"截图    {shortcuts[ShortcutAction.Capture]}";
        if (clipboardMenu != null) clipboardMenu.Text = "翻译剪贴板    " +
            (hotkeys?.Registered(ShortcutAction.ClipboardTranslate) == true ? shortcuts[ShortcutAction.ClipboardTranslate].ToString() : "快捷键未启用");
        if (tray != null) tray.Text = $"Snap Tool · {shortcuts[ShortcutAction.Capture]} 截图";
        foreach (Window window in Windows)
            switch (window)
            {
                case OverlayWindow overlay: overlay.Refresh(); break;
                case PinWindow pin: pin.RefreshShortcuts(); break;
                case ScrollCaptureWindow scroll: scroll.RefreshShortcuts(); break;
                case RecordingWindow recordingWindow: recordingWindow.RefreshShortcuts(); break;
            }
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && hotkeys?.Matches(ShortcutAction.Capture, wParam.ToInt32()) == true)
        {
            handled = true;
            QueueCapture();
        }
        else if (message == 0x0312 && hotkeys?.Matches(ShortcutAction.ClipboardTranslate, wParam.ToInt32()) == true)
        {
            handled = true;
            QueueClipboardTranslation(copySelection: true);
        }
        else if (message == 0x007E) { session?.Close(); scrollCapture?.Close(); if (recording != null) _ = recording.EndForSystemChange(); pins.Reposition(); }
        else if (message == 0x02B1 && wParam.ToInt32() == 7) { if (recording != null) _ = recording.EndForSystemChange(); } // Session locked.
        else if (message == 0x0011) { handled = true; _ = ExitSafely(); return new IntPtr(1); }
        return IntPtr.Zero;
    }

    private bool queued;
    private void QueueCapture()
    {
        if (session != null || scrollCapture != null || recording != null || configuration != null || queued || clipboardQueued || exiting) return;
        queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                // Give a closing tray menu a frame to disappear before freezing the desktop.
                await System.Threading.Tasks.Task.Delay(100);
                if (source == null) return;
                // Capture visible pins, then hide their interactive windows behind the capture overlay.
                var windowTargets = WindowTargets.Capture();
                var snapshot = DesktopSnapshot.Capture();
                pins.Suspend();
                session = new CaptureSession(snapshot, () => { session = null; if (scrollCapture == null && recording == null) pins.Resume(); },
                    UpdateStatus, ocr.Recognize, showTranslation: ShowTranslation,
                    showPin: (image, rectangle) => pins.Add(image, rectangle),
                    windowTargets: windowTargets.Select(target => target.Bounds).ToArray(),
                    showScroll: (region, image) => ShowScrollCapture(region, image,
                        windowTargets.FirstOrDefault(w => w.Bounds.Contains(new(region.Left + region.Width / 2, region.Top + region.Height / 2)))),
                    showRecording: ShowRecording, getShortcuts: () => shortcuts, initialStyle: annotationStyle,
                    rememberStyle: next => { annotationStyle = next; annotationStyleSettings.Save(next); });
                session.Show();
            }
            catch (Exception ex)
            {
                session?.Close(); session = null; pins.Resume();
                MessageBox.Show("无法开始截图。\n" + ex.Message, "Snap Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { queued = false; }
        }));
    }

    private void UpdateStatus(string title, string text)
    {
        if (tray == null) return;
        string status = title + "\n" + text;
        int length = Math.Min(status.Length, 63);
        if (length < status.Length && char.IsHighSurrogate(status[length - 1])) length--;
        tray.Text = status[..length];
    }

    private bool ShowRecording(PixelRect region)
    {
        if (!sessionNotifications) throw new InvalidOperationException("无法监听锁屏状态，请退出后重新启动再录屏。");
        if (!global::Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("当前系统不支持屏幕录制。");
        var window = new RecordingWindow(region, UpdateStatus, getShortcuts: () => shortcuts); recording = window;
        window.Closed += (_, _) => { if (recording == window) recording = null; if (session == null) pins.Resume(); };
        try { window.Start(); return true; }
        catch { window.Close(); throw; }
    }

    private async Task ExitSafely()
    {
        if (exiting) return;
        exiting = true; clipboardLifetime.Cancel(); clipboardWindow?.Close(); session?.Close(); scrollCapture?.Close();
        if (recording != null) await recording.EndForSystemChange();
        Shutdown();
    }

    private void ShowScrollCapture(PixelRect region, BitmapSource first, WindowTarget target)
    {
        if (target.Handle == IntPtr.Zero || target.Bounds.Intersect(region) != region)
            throw new InvalidOperationException("请将选区完整放在同一个窗口的滚动内容区内。");
        var window = new ScrollCaptureWindow(region, first,
            async (image, warning, token) =>
            {
                await ImageOutput.Copy(image, token);
                token.ThrowIfCancellationRequested();
                UpdateStatus("长截图已复制", warning.Length == 0 ? "可直接粘贴到其他应用。" : "已复制成功拼接的部分。" + warning);
            },
            canCapture: () => scrollCapture != null && WindowTargets.CanCaptureRegion(target, region, scrollCapture.CaptureWindows), getShortcuts: () => shortcuts);
        scrollCapture = window;
        window.Closed += (_, _) => { if (scrollCapture == window) scrollCapture = null; if (session == null) pins.Resume(); };
        try { window.Start(); }
        catch { window.Close(); throw; }
    }

    private void ShowTranslation(string text)
    {
        var window = new TranslationWindow(text,
            (original, target, token) => translator.Translate(original, target, translationSettings.Load(), token), getShortcuts: () => shortcuts);
        window.Show(); window.Activate();
    }

    private void QueueClipboardTranslation(bool copySelection = false)
    {
        if (session != null || scrollCapture != null || recording != null || configuration != null || queued || clipboardQueued || exiting) return;
        if (clipboardWindow != null) { clipboardWindow.Activate(); return; }
        IntPtr target = copySelection ? Native.GetForegroundWindow() : IntPtr.Zero;
        clipboardQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                bool copy = copySelection && translationBehaviorSettings.Load();
                var selection = copy ? SelectionCopy.ForShortcut(shortcuts[ShortcutAction.ClipboardTranslate]) : null;
                await clipboardTranslation.Run(clipboardLifetime.Token,
                    selection == null ? null : token => selection.Run(target, token));
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { if (!exiting) UpdateStatus("快捷翻译配置无法读取", "请在配置的翻译设置页重新保存翻译行为。"); }
            finally { clipboardQueued = false; }
        }));
    }

    private void ShowClipboardTranslation(string text)
    {
        if (exiting || session != null || scrollCapture != null || recording != null || configuration != null) return;
        var window = new TranslationWindow(text,
            (original, target, token) => translator.Translate(original, target, translationSettings.Load(), token),
            getShortcuts: () => shortcuts, fromClipboard: true);
        clipboardWindow = window;
        window.Closed += (_, _) => { if (clipboardWindow == window) clipboardWindow = null; };
        window.Show(); window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        clipboardLifetime.Cancel(); clipboardLifetime.Dispose();
        pins.CloseAll();
        scrollCapture?.Close();
        session?.Close();
        ocr.Dispose();
        translator.Dispose();
        if (source != null) Native.WTSUnRegisterSessionNotification(source.Handle);
        hotkeys?.Dispose();
        source?.Dispose(); source = null;
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        icon?.Dispose();
        if (instance != null) { instance.ReleaseMutex(); instance.Dispose(); }
        base.OnExit(e);
    }
}
