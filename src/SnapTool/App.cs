using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapTool.Core;
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
    private ScrollCaptureWindow? scrollCapture;
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
            app.scrollCapture?.Close();
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
        {
            handled = true;
            if (scrollCapture != null) _ = scrollCapture.Finish();
            else QueueCapture();
        }
        else if (message == 0x007E) { session?.Close(); scrollCapture?.Close(); pins.Reposition(); } // Display topology changed.
        else if (message == 0x0011) { session?.Close(); Shutdown(); } // Session ending.
        return IntPtr.Zero;
    }

    private bool queued;
    private void QueueCapture()
    {
        if (session != null || scrollCapture != null || queued) return;
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
                session = new CaptureSession(snapshot, () => { session = null; if (scrollCapture == null) pins.Resume(); },
                    (title, text) => Notify(title, text), ocr.Recognize, showTranslation: ShowTranslation,
                    showPin: (image, rectangle) => pins.Add(image, rectangle),
                    windowTargets: windowTargets.Select(target => target.Bounds).ToArray(),
                    showScroll: (region, image) => ShowScrollCapture(region, image,
                        windowTargets.FirstOrDefault(w => w.Bounds.Contains(new(region.Left + region.Width / 2, region.Top + region.Height / 2)))));
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

    private void ShowScrollCapture(PixelRect region, BitmapSource first, WindowTarget target)
    {
        if (target.Handle == IntPtr.Zero || target.Bounds.Intersect(region) != region)
            throw new InvalidOperationException("请将选区完整放在同一个窗口的滚动内容区内。");
        var window = new ScrollCaptureWindow(region, first,
            async (image, warning, token) =>
            {
                await ImageOutput.Copy(image, token);
                token.ThrowIfCancellationRequested();
                Notify("长截图已复制", warning.Length == 0 ? "可直接粘贴到其他应用。" : "已复制成功拼接的部分。" + warning);
            },
            canCapture: () => scrollCapture != null && WindowTargets.CanCaptureRegion(target, region, scrollCapture.CaptureWindows));
        scrollCapture = window;
        window.Closed += (_, _) => { if (scrollCapture == window) scrollCapture = null; if (session == null) pins.Resume(); };
        try { window.Start(); }
        catch { window.Close(); throw; }
    }

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
        scrollCapture?.Close();
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
