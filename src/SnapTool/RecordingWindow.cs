using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SnapTool.Core;
using Windows.Graphics.Imaging;

namespace SnapTool;

internal sealed class RecordingWindow : Window
{
    private readonly PixelRect region, screen;
    private readonly RecordingSize size;
    private readonly Func<string, CancellationToken, Task> copyGif;
    private readonly Action<string, string> notify;
    private readonly CancellationTokenSource cancellation = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBlock status = new() { Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly Button stopButton, cancelButton;
    private readonly RecordingBorder border;
    private readonly RecordingJob job;
    private readonly Func<CancellationToken, Task<SoftwareBitmap>>? readFrame;
    private Task operation = Task.CompletedTask;
    private bool started, recording, stopping, closeAllowed, cancelRequested;
    internal Task Completion => operation;
    internal int Frames => job.Frames;

    internal RecordingWindow(PixelRect region, Action<string, string> notify,
        Func<CancellationToken, Task<SoftwareBitmap>>? readFrame = null,
        Func<string, CancellationToken, Task>? copyGif = null, string? temporaryDirectory = null)
    {
        this.region = region; this.notify = notify; this.copyGif = copyGif ?? ImageOutput.CopyGif;
        this.readFrame = readFrame;
        screen = Native.Monitors().Single(m => region.Intersect(m) == region);
        size = RecordingSize.Fit(region); job = new(size, temporaryDirectory); border = new(region);
        status.Text = $"准备录屏 · {size.Width} × {size.Height} · GIF 动图";
        Title = "Snap Tool · 录屏"; Width = 440; Height = 100;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowActivated = false; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(23, 30, 46));
        var panel = new StackPanel { Margin = new Thickness(8) }; panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        stopButton = new Button { Content = "完成", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 6, 0) };
        cancelButton = new Button { Content = "取消", Padding = new Thickness(12, 5, 12, 5) };
        stopButton.Click += async (_, _) => await Stop(); cancelButton.Click += async (_, _) => await Cancel();
        actions.Children.Add(stopButton); actions.Children.Add(cancelButton); panel.Children.Add(actions); Content = panel;
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; await Cancel(); } };
        timer.Tick += (_, _) => { if (recording && !stopping) status.Text = $"● 录制中 {job.Elapsed:mm\\:ss} · {size.Width} × {size.Height} · GIF 动图"; };
        Closing += OnClosing;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
        Closed += (_, _) => { timer.Stop(); border.Close(); cancellation.Dispose(); };
    }

    internal void Start()
    {
        ScrollCaptureView.Exclude(this); border.Start();
        ScrollCaptureView.Position(this, new(region.Left, region.Top, region.Left + 440, region.Top + 100));
        var work = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(region.Left, region.Top)).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var layout = ScrollViewLayout.Calculate(region, new(work.Left, work.Top, work.Right, work.Bottom), (int)(440 * dpi.DpiScaleX), (int)(100 * dpi.DpiScaleY));
        Show(); ScrollCaptureView.Position(this, layout.Toolbar);
        started = true; operation = Run();
    }

    private async Task Run()
    {
        try
        {
            stopButton.IsEnabled = false;
            for (int count = 3; count > 0; count--) { status.Text = $"{count} 秒后开始 · {size.Width} × {size.Height} · GIF 动图"; await Task.Delay(1000, cancellation.Token); }
            cancellation.Token.ThrowIfCancellationRequested();
            recording = true; stopButton.IsEnabled = true; timer.Start();
            await Task.Run(async () =>
            {
                if (readFrame != null) { await job.Run(readFrame, cancellation.Token).ConfigureAwait(false); return; }
                using var capture = new RecordingCapture(region, screen, size);
                await job.Run(capture.Read, cancellation.Token).ConfigureAwait(false);
            });
            recording = false; timer.Stop();
            await CopyPending();
        }
        catch (OperationCanceledException) when (cancelRequested) { }
        catch (Exception)
        {
            if (!cancelRequested) notify("录屏失败", "未生成完整动图，请检查系统录屏支持和临时磁盘空间后重试。");
            job.Discard(); FinishClose();
        }
        finally
        {
            recording = false; timer.Stop();
            if (cancelRequested) { job.Discard(); FinishClose(); }
        }
    }

    private async Task CopyPending()
    {
        if (job.PendingPath == null) return;
        stopping = true; stopButton.IsEnabled = false; status.Text = "正在复制 GIF 动图…";
        try
        {
            await copyGif(job.PendingPath, cancellation.Token);
            job.ReleaseToClipboard();
            notify("录屏已复制", "可粘贴到支持 GIF 文件的应用。"); FinishClose();
        }
        catch (OperationCanceledException) when (cancelRequested) { }
        catch (Exception)
        {
            if (!cancelRequested)
            {
                status.Text = "复制失败，动图已保留。请点击完成重试，或取消丢弃。";
                stopping = false; stopButton.IsEnabled = true;
            }
        }
        finally { if (cancelRequested) { job.Discard(); FinishClose(); } }
    }

    internal async Task Stop()
    {
        if (!started || closeAllowed || cancelRequested) return;
        if (stopping) { await operation; return; }
        if (job.PendingPath != null && operation.IsCompleted)
        {
            operation = CopyPending(); await operation; return;
        }
        if (!recording) { await Cancel(); return; }
        stopping = true; stopButton.IsEnabled = false; status.Text = "正在结束录屏并复制…"; job.Stop();
        await operation;
    }

    internal async Task Cancel()
    {
        if (closeAllowed) return;
        cancelRequested = true; cancellation.Cancel(); stopButton.IsEnabled = cancelButton.IsEnabled = false;
        await operation;
        job.Discard(); FinishClose();
    }

    internal Task EndForSystemChange() => Cancel();
    private void FinishClose() { if (!closeAllowed) { closeAllowed = true; Close(); } }
    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x02E0 && started) _ = EndForSystemChange(); // DPI changed on the recording monitor.
        return IntPtr.Zero;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeAllowed || !started) return;
        e.Cancel = true; await Cancel();
    }
}

internal sealed class RecordingBorder : Window
{
    private readonly PixelRect bounds;
    internal RecordingBorder(PixelRect region)
    {
        bounds = new(region.Left - 2, region.Top - 2, region.Right + 2, region.Bottom + 2);
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; IsHitTestVisible = false;
        Content = new Border { BorderBrush = Brushes.Red, BorderThickness = new Thickness(1) };
    }
    internal void Start() { ScrollCaptureView.Exclude(this, true); Show(); ScrollCaptureView.Position(this, bounds); }
}
