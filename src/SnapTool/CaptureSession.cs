using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SnapTool.Core;

namespace SnapTool;

internal sealed class CaptureSession(DesktopSnapshot snapshot, Action finished, Action<string, string> notify,
    Func<BitmapSource, CancellationToken, Task<string>> recognize,
    Func<string, CancellationToken, Task>? copyText = null, Action<string>? showTranslation = null,
    Action<BitmapSource, PixelRect>? showPin = null, Func<BitmapSource, CancellationToken, Task>? copyImage = null,
    IReadOnlyList<PixelRect>? windowTargets = null, Action<PixelRect, BitmapSource>? showScroll = null,
    Func<PixelRect, bool>? showRecording = null)
{
    internal DesktopSnapshot Snapshot { get; } = snapshot;
    internal SelectionModel Selection { get; } = new(snapshot.Bounds);
    internal Annotations Annotations { get; } = new();
    internal MosaicRenderer Mosaic { get; } = new(snapshot);
    internal AnnotationKind? Tool { get; private set; }
    internal bool Annotating => Tool != null;
    internal bool Dragging => pendingWindowClick || Selection.Dragging || Annotations.Dragging;
    internal bool AutoSelecting { get; private set; } = windowTargets != null;
    internal PixelRect WindowPreview { get; private set; }
    internal PixelRect DisplayRectangle => AutoSelecting ? WindowPreview : Selection.Rectangle;
    internal PixelPoint Pointer { get; private set; } = new(snapshot.Bounds.Left, snapshot.Bounds.Top);
    internal bool Busy { get; private set; }
    internal bool Recognizing { get; private set; }
    internal OverlayWindow? TextEditor { get; private set; }
    internal string Status { get; private set; } = "";
    private CancellationTokenSource? outputCancellation;
    private readonly List<OverlayWindow> windows = [];
    private bool closed;
    private readonly PixelRect[] targets = windowTargets?.Select(r => r.Intersect(snapshot.Bounds)).Where(r => !r.IsEmpty).ToArray() ?? [];
    private bool pendingWindowClick;
    private PixelPoint clickAnchor;
    private int dragThreshold;

    internal void Show()
    {
        Pointer = Native.CursorPosition();
        HoverWindow(Pointer);
        foreach (var screen in Snapshot.Monitors)
        {
            var window = new OverlayWindow(this, screen);
            windows.Add(window);
            window.Show();
        }
        var current = windows.FirstOrDefault(w => w.Screen.Contains(Pointer)) ?? windows[0];
        current.Activate(); current.Focus();
    }

    internal (bool Selection, SelectionHit Hit) HitTest(PixelPoint p, int tolerance)
    {
        if (AutoSelecting) return (true, SelectionHit.None);
        var hit = Selection.HitTest(p, tolerance);
        if (!Annotating || Selection.Rectangle.IsEmpty || (hit != SelectionHit.None && hit != SelectionHit.Move))
            return (true, hit);
        return (false, Annotations.HitTest(p, tolerance).Hit);
    }

    internal void Begin(PixelPoint p, int tolerance)
    {
        FinishTextEditing();
        if (Busy || closed || Dragging) return;
        Status = "";
        Pointer = p;
        if (AutoSelecting)
        {
            HoverWindow(p); clickAnchor = p; dragThreshold = Math.Max(1, tolerance); pendingWindowClick = true;
            Refresh(); return;
        }
        if (HitTest(p, tolerance).Selection)
        { Annotations.Deselect(); Selection.Begin(p, tolerance); }
        else if (Tool is { } kind) Annotations.Begin(p, Selection.Rectangle, tolerance, kind);
        if (!Dragging) return;
        foreach (var window in windows) window.HideToolbar();
        Refresh();
    }

    internal void Move(PixelPoint p, bool square = false)
    {
        if (Busy || closed) return;
        var previousPointer = Pointer;
        var previousRectangle = DisplayRectangle;
        Pointer = p;
        if (AutoSelecting)
        {
            if (!pendingWindowClick)
            {
                HoverWindow(p);
                RefreshPointer(previousPointer, previousRectangle);
                return;
            }
            if (Math.Abs((long)p.X - clickAnchor.X) < dragThreshold && Math.Abs((long)p.Y - clickAnchor.Y) < dragThreshold) return;
            pendingWindowClick = false; AutoSelecting = false; WindowPreview = default;
            Selection.Select(default); Selection.Begin(clickAnchor, 0);
        }
        if (Selection.Dragging) Selection.Update(p);
        else if (Annotations.Dragging) Annotations.Update(p, square);
        if (Dragging) { foreach (var window in windows) window.RefreshSurface(); }
        else RefreshPointer(previousPointer, previousRectangle);
    }

    private void RefreshPointer(PixelPoint previousPointer, PixelRect previousRectangle)
    {
        foreach (var window in windows)
        {
            bool moved = previousPointer != Pointer;
            bool wasInside = window.Screen.Contains(previousPointer), inside = window.Screen.Contains(Pointer);
            if (previousRectangle != DisplayRectangle ||
                (DisplayRectangle.IsEmpty && moved && (wasInside || inside)) ||
                (AutoSelecting && wasInside != inside))
                window.RefreshSurface();
        }
    }

    internal void End(PixelPoint p, bool square = false)
    {
        if (Busy || closed) return;
        Pointer = p;
        if (pendingWindowClick)
        {
            Move(p, square);
            if (pendingWindowClick)
            {
                pendingWindowClick = false;
                Selection.Select(WindowPreview);
                AutoSelecting = Selection.Rectangle.IsEmpty;
                WindowPreview = default;
                if (AutoSelecting) HoverWindow(p);
            }
        }
        if (Selection.Dragging) Selection.End(p);
        else if (Annotations.Dragging) Annotations.End(p, square);
        Refresh();
        if (Selection.Rectangle.IsEmpty) return;
        var current = windows.FirstOrDefault(w => w.Screen.Contains(p)) ?? windows.FirstOrDefault(w => !w.Screen.Intersect(Selection.Rectangle).IsEmpty);
        current?.Activate(); current?.ShowToolbar();
    }

    private void HoverWindow(PixelPoint p)
    {
        if (!AutoSelecting || pendingWindowClick) return;
        WindowPreview = targets.FirstOrDefault(r => r.Contains(p));
    }

    internal void SetTool(AnnotationKind? kind)
    {
        FinishTextEditing();
        if (Busy || closed || Dragging || Selection.Rectangle.IsEmpty) return;
        Tool = kind; Annotations.Deselect(); Status = ""; Refresh();
    }

    internal void ToggleTool(AnnotationKind kind) => SetTool(Tool == kind ? null : kind);

    internal void SetAnnotationStyle(uint rgb, int thickness)
    {
        if (Busy || closed || Dragging) return;
        Annotations.SetStyle(rgb, thickness, Tool ?? AnnotationKind.Rectangle); Refresh();
    }

    internal bool TryEditText(OverlayWindow window, PixelPoint point, int tolerance, bool editExisting = false)
    {
        if (Busy || closed || Dragging || !Annotating || !Selection.Rectangle.Contains(point) || HitTest(point, tolerance).Selection) return false;
        FinishTextEditing();
        var hit = Annotations.HitTest(point, tolerance);
        if (!(editExisting && hit.Index >= 0 && Annotations.Items[hit.Index].Kind == AnnotationKind.Text) &&
            !(Tool == AnnotationKind.Text && hit.Index < 0)) return false;
        Annotations.BeginText(point, hit.Index);
        TextEditor = window; window.OpenTextEditor(); Refresh(); return true;
    }

    internal void UpdateText(string text)
    {
        if (!Annotations.EditingText || closed) return;
        Annotations.UpdateText(text, TextAnnotationRenderer.Measure(text, Annotations.TextStyle)); Refresh();
    }

    internal void SetTextStyle(TextAnnotationStyle style)
    {
        if (Busy || closed || Dragging) return;
        var text = Annotations.Selected is { Kind: AnnotationKind.Text } mark ? mark.Text : "";
        Annotations.SetTextStyle(style, TextAnnotationRenderer.Measure(text, style)); Refresh();
    }

    internal void FinishTextEditing(bool cancel = false)
    {
        if (TextEditor == null) return;
        var owner = TextEditor; TextEditor = null;
        owner.CloseTextEditor(); Annotations.EndText(cancel);
        if (!closed) Refresh();
    }

    internal void UndoAnnotation()
    {
        FinishTextEditing();
        if (Busy || closed || Dragging) return;
        Annotations.Undo(); Mosaic.Retain(Annotations.Items); Refresh();
    }

    internal void DeleteAnnotation()
    {
        FinishTextEditing();
        if (Busy || closed || Dragging || !Annotating) return;
        Annotations.Delete(); Mosaic.Retain(Annotations.Items); Refresh();
    }

    internal BitmapSource CreateOutput()
    {
        FinishTextEditing();
        return AnnotationRenderer.Compose(Snapshot, Selection.Rectangle, Annotations.Items, Mosaic);
    }

    internal void StartRecording()
    {
        FinishTextEditing();
        if (Busy || Dragging || Selection.Rectangle.IsEmpty || closed) return;
        if (Annotations.Items.Count != 0) { Status = "请先撤销标注再录屏；录屏不叠加截图标注。"; Refresh(); return; }
        var region = Selection.Rectangle;
        try
        {
            RecordingSize.Fit(region);
            if (!Snapshot.Monitors.Any(m => region.Intersect(m) == region)) throw new InvalidOperationException("请在同一显示器内框选录屏范围。");
            Busy = true;
            if (showRecording == null) throw new InvalidOperationException("录屏入口不可用。");
            if (showRecording(region)) Close();
        }
        catch (Exception ex) { if (!closed) { Status = "无法开始录屏，选区已保留。" + ex.Message; Refresh(); } }
        finally { Busy = false; if (!closed) Refresh(); }
    }

    internal void StartScroll()
    {
        FinishTextEditing();
        if (Busy || Dragging || Selection.Rectangle.IsEmpty || closed) return;
        var region = Selection.Rectangle;
        if (Annotations.Items.Count != 0)
        { Status = "请先撤销标注再开始长截图，避免标注随页面重复。"; Refresh(); return; }
        if (region.Width < 32 || region.Height < 64 || (long)region.Width * region.Height > ScrollStitcher.MaxPixels ||
            !Snapshot.Monitors.Any(m => region.Intersect(m) == region))
        { Status = "请在同一显示器内框选至少 32 × 64 px 的滚动内容区，并缩小过大的选区。"; Refresh(); return; }
        Busy = true;
        try
        {
            if (showScroll == null) throw new InvalidOperationException("长截图入口不可用。");
            showScroll(region, Snapshot.Crop(region)); Close();
        }
        catch (Exception ex) { if (!closed) { Status = "无法开始长截图，选区已保留。" + ex.Message; Refresh(); } }
        finally { Busy = false; if (!closed) Refresh(); }
    }

    internal void Pin()
    {
        FinishTextEditing();
        if (Busy || Dragging || Selection.Rectangle.IsEmpty || closed) return;
        Busy = true; Status = "";
        try
        {
            if (showPin == null) throw new InvalidOperationException("贴图入口不可用。");
            var image = PinWindow.Detach(CreateOutput());
            showPin(image, Selection.Rectangle);
            Close();
        }
        catch (Exception) { if (!closed) { Status = "创建贴图失败，选区已保留，请重试。"; Refresh(); } }
        finally { Busy = false; if (!closed) Refresh(); }
    }

    internal void Refresh() { foreach (var window in windows) window.Refresh(); }

    internal void WindowClosed(OverlayWindow window)
    {
        windows.Remove(window);
        Close();
    }

    internal async Task Copy(OverlayWindow owner)
    {
        FinishTextEditing();
        if (Busy || Dragging || Selection.Rectangle.IsEmpty || closed) return;
        Busy = true;
        using var cancellation = new CancellationTokenSource();
        outputCancellation = cancellation;
        try
        {
            var image = CreateOutput();
            await (copyImage ?? ImageOutput.Copy)(image, cancellation.Token);
            if (!closed) { Close(); notify("已复制截图", "可以粘贴到聊天、文档或图片编辑器。"); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!closed) MessageBox.Show(owner, "复制失败，请重试。选区已保留。\n" + ex.Message,
                "Snap Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { outputCancellation = null; Busy = false; }
    }

    internal void Save(OverlayWindow owner)
    {
        FinishTextEditing();
        if (Busy || Dragging || Selection.Rectangle.IsEmpty || closed) return;
        Busy = true;
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存截图", Filter = "PNG 图片 (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
                FileName = "Screenshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png", OverwritePrompt = true
            };
            if (dialog.ShowDialog(owner) != true || closed) return;
            ImageOutput.SavePng(CreateOutput(), dialog.FileName);
            Close(); notify("截图已保存", "PNG 图片已保存到所选位置。");
        }
        catch (Exception ex)
        {
            if (!closed) MessageBox.Show(owner, "保存失败，请重试。选区已保留。\n" + ex.Message,
                "Snap Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy = false; }
    }

    internal async Task RecognizeText(bool translate = false)
    {
        FinishTextEditing();
        if (Busy || Dragging || Selection.Rectangle.IsEmpty || closed) return;
        Busy = true; Recognizing = true; Status = "正在识别文字… Esc 取消";
        using var cancellation = new CancellationTokenSource();
        outputCancellation = cancellation;
        Refresh();
        try
        {
            var image = Snapshot.Crop(Selection.Rectangle);
            string text = await recognize(image, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closed) return;
            if (string.IsNullOrWhiteSpace(text))
            { Status = "未识别到文字，请调整选区后重试。"; return; }
            if (translate)
            {
                if (showTranslation == null) throw new InvalidOperationException("翻译入口不可用。");
                Close(); showTranslation(text); return;
            }
            await (copyText ?? ImageOutput.CopyText)(text, cancellation.Token);
            if (!closed)
            {
                // Output has succeeded; closing now cancels no pending clipboard operation.
                Close(); notify("文字已复制", "按 Ctrl+V 粘贴识别结果。");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!closed) Status = "识别或复制失败，选区已保留。" + ex.Message;
        }
        finally
        {
            outputCancellation = null;
            Busy = false; Recognizing = false;
            if (!closed) Refresh();
        }
    }

    internal void Close()
    {
        if (closed) return;
        closed = true; pendingWindowClick = false; outputCancellation?.Cancel(); Selection.StopDragging();
        FinishTextEditing(true);
        foreach (var window in windows.ToArray()) window.Close();
        windows.Clear(); Mosaic.Clear(); finished();
    }
}
