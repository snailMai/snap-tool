using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SnapTool;

internal sealed class App : Application
{
    private const int HotkeyId = 1;
    private Mutex? instance;
    private HwndSource? source;
    private Forms.NotifyIcon? tray;
    private Icon? icon;
    private CaptureSession? session;
    private bool registered;
    private readonly OcrService ocr = new();
    private readonly TranslationSettings translationSettings = new();
    private readonly YoudaoTranslator translator = new();
    private readonly PinWindows pins = new();

    [STAThread]
    public static void Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => app.Start(args.Contains("--capture"));
        app.DispatcherUnhandledException += (_, e) =>
        {
            app.session?.Close();
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
            MessageBox.Show("Snap Tool 已在运行。按 Ctrl+F2 截图，或从系统托盘打开。", "Snap Tool");
            Shutdown(); return;
        }
        source = new HwndSource(new HwndSourceParameters("Snap Tool Hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(WindowMessage);
        icon = MakeIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("截图    Ctrl+F2", null, (_, _) => QueueCapture());
        menu.Items.Add("重新注册 Ctrl+F2", null, (_, _) => RegisterHotkey(true));
        menu.Items.Add("翻译设置…", null, (_, _) => ShowTranslationSettings(null));
        menu.Items.Add("关闭全部贴图", null, (_, _) => pins.CloseAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        tray = new Forms.NotifyIcon { Icon = icon, Text = "Snap Tool · Ctrl+F2 截图", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => QueueCapture();
        RegisterHotkey(false);
        if (registered) Notify("已准备好", "按 Ctrl+F2 框选截图；右键托盘图标可退出。");
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
        if (registered)
        {
            if (notifySuccess) Notify("快捷键可用", "Ctrl+F2 已注册。");
            return;
        }
        registered = Native.RegisterHotKey(source!.Handle, HotkeyId, 0x0002 | 0x4000, 0x71);
        if (!registered)
        {
            int code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            MessageBox.Show("无法注册 Ctrl+F2，可能已被其他程序占用。\n请释放该快捷键后，从托盘菜单选择“重新注册 Ctrl+F2”。\n也可从托盘菜单截图。\n\n错误代码：" + code,
                "Snap Tool · 快捷键不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (notifySuccess) Notify("快捷键可用", "Ctrl+F2 已注册。");
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == HotkeyId)
        { handled = true; QueueCapture(); }
        else if (message == 0x007E) { session?.Close(); pins.Reposition(); } // Display topology changed.
        else if (message == 0x0011) { session?.Close(); Shutdown(); } // Session ending.
        return IntPtr.Zero;
    }

    private bool queued;
    private void QueueCapture()
    {
        if (session != null || queued) return;
        queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                // Give a closing tray menu a frame to disappear before freezing the desktop.
                await System.Threading.Tasks.Task.Delay(100);
                if (source == null) return;
                // Capture visible pins, then hide their interactive windows behind the capture overlay.
                var snapshot = DesktopSnapshot.Capture();
                pins.Suspend();
                session = new CaptureSession(snapshot, () => { session = null; pins.Resume(); },
                    (title, text) => Notify(title, text), ocr.Recognize, showTranslation: ShowTranslation,
                    showPin: (image, rectangle) => pins.Add(image, rectangle));
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

    private void Notify(string title, string text) => tray?.ShowBalloonTip(2000, title, text, Forms.ToolTipIcon.Info);

    private void ShowTranslationSettings(Window? owner)
    {
        var window = new TranslationSettingsWindow(translationSettings);
        if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        window.ShowDialog();
    }

    private void ShowTranslation(string text)
    {
        var window = new TranslationWindow(text,
            (original, target, token) => translator.Translate(original, target, translationSettings.Load(), token), ShowTranslationSettings);
        window.Show(); window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        pins.CloseAll();
        session?.Close();
        ocr.Dispose();
        translator.Dispose();
        if (registered && source != null) Native.UnregisterHotKey(source.Handle, HotkeyId);
        source?.Dispose(); source = null;
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        icon?.Dispose();
        if (instance != null) { instance.ReleaseMutex(); instance.Dispose(); }
        base.OnExit(e);
    }
}
