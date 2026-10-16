using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapTool.Core;

namespace SnapTool;

internal sealed class ScrollCaptureWindow : Window
{
    private readonly PixelRect region;
    private readonly Func<BitmapSource> capture;
    private readonly Func<bool> canCapture;
    private readonly Func<BitmapSource, string, CancellationToken, Task> completed;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ScrollPreviewWindow preview = new();
    private ScrollRegionWindow? regionWindow;
    private readonly Button finish;
    private ScrollStitcher? stitcher;
    private ScrollFrame? candidate;
    private Task sampling = Task.CompletedTask;
    private bool processing, finishing, closed, limitReached;
    private string problem = "";
    internal string Status => status.Text;
    internal int ImageHeight => stitcher?.Height ?? 0;
    internal BitmapSource? LivePreview => preview.Surface.Image;
    internal bool PreviewMatched => preview.Surface.Matched;
    internal IntPtr[] CaptureWindows => [new WindowInteropHelper(this).Handle,
        regionWindow == null ? IntPtr.Zero : new WindowInteropHelper(regionWindow).Handle, new WindowInteropHelper(preview).Handle];

    internal ScrollCaptureWindow(PixelRect region, BitmapSource first, Func<BitmapSource, string, CancellationToken, Task> completed,
        Func<BitmapSource>? capture = null, Func<bool>? canCapture = null)
    {
        this.region = region; this.completed = completed;
        this.capture = capture ?? (() => DesktopSnapshot.CaptureRegion(region));
        this.canCapture = canCapture ?? (() => true);
        candidate = ScrollImages.Frame(first); stitcher = new(candidate);
        preview.Surface.Update(ScrollImages.Preview(stitcher), stitcher.Height, region.Height);
        Title = "Snap Tool · 长截图"; Width = 480; Height = 112;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Background = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        finish = Button("完成", async () => await Finish()); actions.Children.Add(finish);
        actions.Children.Add(Button("取消", Close));
        actions.Children.Add(new TextBlock { Text = "向下滚动 · 停顿后自动拼接", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        panel.Children.Add(actions); panel.Children.Add(status);
        Content = new Border { Background = Brushes.White, BorderBrush = ScrollCaptureView.Blue, BorderThickness = new Thickness(1), Child = panel };
        ToolTip = "每次保留至少四分之一重叠内容；本窗口内 Ctrl+C 完成并复制，Esc 取消。";
        status.Text = $"已采集 {region.Width} × {region.Height} px，等待滚动。";
        timer.Tick += (_, _) => { if (!processing && !finishing) sampling = SampleOnce(); };
        PreviewKeyDown += OnKey;
        preview.PreviewKeyDown += OnKey;
        preview.Closed += (_, _) => { if (!closed) Close(); };
        Closed += (_, _) => { closed = true; timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); stitcher = null; candidate = null;
            regionWindow?.Close(); preview.Close(); };
    }

    private async void OnKey(object sender, KeyEventArgs e) => await HandleKey(e, Keyboard.Modifiers);

    internal async Task HandleKey(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.C && modifiers == ModifierKeys.Control) { e.Handled = true; await Finish(); }
    }

    internal void Start()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new InvalidOperationException("长截图需要 Windows 10 2004 或更新版本。");
        ScrollCaptureView.Exclude(this);
        var work = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(region.Left, region.Top)).WorkingArea;
        regionWindow = new ScrollRegionWindow(region, Native.Monitors().Single(m => region.Intersect(m) == region));
        regionWindow.Start();
        // Move to the target monitor before measuring the toolbar's per-monitor DPI.
        ScrollCaptureView.Position(this, new(region.Left, region.Top, region.Left + 480, region.Top + 112));
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(480 * dpi.DpiScaleX), height = (int)Math.Ceiling(112 * dpi.DpiScaleY);
        var layout = ScrollViewLayout.Calculate(region, new(work.Left, work.Top, work.Right, work.Bottom), width, height);
        preview.Start(layout.Preview); Show(); ScrollCaptureView.Position(this, layout.Toolbar);
        timer.Start();
    }

    internal Task SampleOnce()
    {
        if (closed || limitReached || stitcher == null) return Task.CompletedTask;
        if (!processing) sampling = SampleCore();
        return sampling;
    }

    private async Task SampleCore()
    {
        if (closed || processing || limitReached || stitcher == null) return;
        processing = true;
        try
        {
            if (!canCapture())
            {
                candidate = null; problem = "原窗口被遮挡、最小化或位置改变。请恢复原窗口，或完成已有部分。";
                status.Text = problem; preview.Surface.SetMatched(false); return;
            }
            var image = capture(); var previous = candidate; var current = stitcher; var token = lifetime.Token;
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var frame = ScrollImages.Frame(image);
                bool stable = previous != null && previous.SimilarTo(frame, token);
                var appended = stable ? current.Append(frame, token) : ScrollAppendResult.Unchanged;
                return (Frame: frame, Stable: stable, Result: appended,
                    Preview: appended == ScrollAppendResult.Added ? ScrollImages.Preview(current, token) : null);
            }, token);
            if (closed) return;
            candidate = result.Frame;
            if (!result.Stable) problem = "页面尚未稳定，末段暂未拼接。请停顿片刻。";
            else if (result.Result == ScrollAppendResult.NoMatch)
                problem = "未能可靠拼接。请缓慢回到上次位置后向下滚动，或完成已有部分；固定栏需排除在选区外。";
            else if (result.Result == ScrollAppendResult.LimitReached)
            { limitReached = true; timer.Stop(); problem = "已达长截图尺寸上限，请完成并复制已有部分。"; }
            else problem = "";
            if (result.Preview != null) preview.Surface.Update(result.Preview, current.Height, region.Height);
            preview.Surface.SetMatched(result.Stable && problem.Length == 0);
            status.Text = $"{current.Width} × {current.Height} px · 已拼接 {current.Frames} 帧\n" +
                (problem.Length == 0 ? "可继续向下滚动，或点击完成。" : problem);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception)
        { if (!closed) { candidate = null; problem = "采集失败，已保留成功部分。请恢复原页面后重试，或点击完成。"; status.Text = problem; preview.Surface.SetMatched(false); } }
        finally { processing = false; }
    }

    internal async Task Finish()
    {
        if (closed || finishing) return;
        finishing = true; timer.Stop(); finish.IsEnabled = false;
        try
        {
            await sampling;
            if (closed) return;
            var token = lifetime.Token;
            // A final stable pair includes the last wheel movement before the user clicked Finish.
            await SampleOnce(); await Task.Delay(350, token); await SampleOnce();
            if (closed || stitcher == null) return;
            var current = stitcher;
            var image = await Task.Run(() => ScrollImages.Compose(current, token), token);
            token.ThrowIfCancellationRequested();
            status.Text = "正在复制长截图…";
            await completed(image, problem, token);
            token.ThrowIfCancellationRequested();
            Close();
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception) { if (!closed) status.Text = "复制失败，已保留采集内容，请点击完成重试或取消。"; }
        finally { finishing = false; if (!closed) finish.IsEnabled = true; }
    }

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action(); return button;
    }
}
