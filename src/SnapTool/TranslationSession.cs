using System;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTool;

internal sealed class TranslationSession(Func<string, string, CancellationToken, Task<string>> translate)
{
    internal string Result { get; private set; } = "";
    internal string Status { get; private set; } = "";
    internal bool Busy => request != null;
    internal event Action? Changed;
    private CancellationTokenSource? request;
    private bool closed;

    internal async Task Run(string text, string target)
    {
        if (closed || Busy) return;
        using var current = new CancellationTokenSource();
        request = current; Result = ""; Status = "正在翻译…"; Changed?.Invoke();
        try
        {
            string result = await translate(text, target, current.Token);
            if (closed || request != current || current.IsCancellationRequested) return;
            Result = result; Status = "翻译完成，点击“复制译文”可复制结果。";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception ex)
        { if (!closed && request == current) Status = ex is InvalidOperationException ? ex.Message : "翻译失败，原文已保留，请重试。"; }
        finally
        {
            if (request == current) { request = null; if (!closed) Changed?.Invoke(); }
        }
    }

    internal void Invalidate()
    {
        Cancel(); Result = ""; Status = "原文或目标语言已修改，请点击“翻译”。"; Changed?.Invoke();
    }
    internal void Cancel()
    {
        var previous = request; request = null;
        previous?.Cancel();
        if (previous != null) { Status = "已取消，原文已保留。"; Changed?.Invoke(); }
    }
    internal void Close() { closed = true; Cancel(); }
}
