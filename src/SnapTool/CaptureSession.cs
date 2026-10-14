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
    Action<BitmapSource, PixelRect>? showPin = null, Func<BitmapSource, Task>? copyImage = null)
{
    internal DesktopSnapshot Snapshot { get; } = snapshot;
    internal SelectionModel Selection { get; } = new(snapshot.Bounds);
    internal Annotations Annotations { get; } = new();
    internal AnnotationKind? Tool { get; private set; }
    internal bool Annotating => Tool != null;
    internal bool Dragging => Selection.Dragging || Annotations.Dragging;
    internal PixelPoint Pointer { get; private set; } = new(snapshot.Bounds.Left, snapshot.Bounds.Top);
    internal bool Busy { get; private set; }
    internal bool Recognizing { get; private set; }
    internal string Status { get; private set; } = "";
    private CancellationTokenSource? recognitionCancellation;
    private readonly List<OverlayWindow> windows = [];
    private bool closed;

    internal void Show()
    {
        Pointer = Native.CursorPosition();
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
        var hit = Selection.HitTest(p, tolerance);
        if (!Annotating || Selection.Rectangle.IsEmpty || (hit != SelectionHit.None && hit != SelectionHit.Move))
            return (true, hit);
        return (false, Annotations.HitTest(p, tolerance).Hit);
    }

    internal void Begin(PixelPoint p, int tolerance)
    {
        if (Busy || closed || Dragging) return;
        Status = "";
        Pointer = p;
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
        Pointer = p;
        if (Selection.Dragging) Selection.Update(p);
        else if (Annotations.Dragging) Annotations.Update(p, square);
        Refresh();
    }

    internal void End(PixelPoint p, bool square = false)
    {
        if (Busy || closed) return;
        Pointer = p;
        if (Selection.Dragging) Selection.End(p);
        else if (Annotations.Dragging) Annotations.End(p, square);
        Refresh();
        if (Selection.Rectangle.IsEmpty) return;
        var current = windows.FirstOrDefault(w => w.Screen.Contains(p)) ?? windows.FirstOrDefault(w => !w.Screen.Intersect(Selection.Rectangle).IsEmpty);
        current?.Activate(); current?.ShowToolbar();
    }

    internal void SetTool(AnnotationKind? kind)
    {
        if (Busy || closed || Dragging || Selection.Rectangle.IsEmpty) return;
        Tool = kind; Annotations.Deselect(); Status = ""; Refresh();
    }

    internal void ToggleTool(AnnotationKind kind) => SetTool(Tool == kind ? null : kind);

    internal void SetAnnotationStyle(uint rgb, int thickness)
    {
        if (Busy || closed || Dragging) return;
        Annotations.SetStyle(rgb, thickness); Refresh();
    }

    internal void UndoAnnotation()
    {
        if (Busy || closed || Dragging) return;
        Annotations.Undo(); Refresh();
    }

    internal void DeleteAnnotation()
    {
        if (Busy || closed || Dragging || !Annotating) return;
        Annotations.Delete(); Refresh();
    }

    internal BitmapSource CreateOutput() => AnnotationRenderer.Compose(Snapshot, Selection.Rectangle, Annotations.Items);

    internal void Pin()
    {
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
        if (Busy || Dragging || Selection.Rectangle.IsEmpty || closed) return;
        Busy = true;
        try
        {
            var image = CreateOutput();
            if (copyImage != null) await copyImage(image);
            else await ImageOutput.Copy(image);
            if (!closed) { Close(); notify("已复制截图", "可以粘贴到聊天、文档或图片编辑器。"); }
        }
        catch (Exception ex)
        {
            if (!closed) MessageBox.Show(owner, "复制失败，请重试。选区已保留。\n" + ex.Message,
                "Snap Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy = false; }
    }

    internal void Save(OverlayWindow owner)
    {
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
        if (Busy || Dragging || Selection.Rectangle.IsEmpty || closed) return;
        Busy = true; Recognizing = true; Status = "正在识别文字… Esc 取消";
        using var cancellation = new CancellationTokenSource();
        recognitionCancellation = cancellation;
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
            recognitionCancellation = null;
            Busy = false; Recognizing = false;
            if (!closed) Refresh();
        }
    }

    internal void Close()
    {
        if (closed) return;
        closed = true; recognitionCancellation?.Cancel(); Selection.StopDragging();
        foreach (var window in windows.ToArray()) window.Close();
        windows.Clear(); finished();
    }
}
