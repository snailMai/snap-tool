using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTool;

internal sealed class ClipboardTranslation(Func<string> read, Action<string> show, Action<string> report)
{
    private bool reading;

    internal async Task Run(CancellationToken token, Func<CancellationToken, Task<bool>>? copySelection = null)
    {
        if (reading || token.IsCancellationRequested) return;
        reading = true;
        try
        {
            if (copySelection != null && !await copySelection(token))
            {
                if (!token.IsCancellationRequested) report("未复制到选中文字，请保持原窗口不变，或关闭自动复制后手动复制。");
                return;
            }
            string text;
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try { text = read(); break; }
                catch (ExternalException) when (attempt < 3) { await Task.Delay(80, token); }
            }
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) { report("请先复制需要翻译的文字；图片和文件不能直接翻译。"); return; }
            if (text.EnumerateRunes().Count() > 5000) { report("原文超过 5000 字符，请分段复制后翻译。"); return; }
            show(text);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ExternalException) { if (!token.IsCancellationRequested) report("剪贴板暂时无法读取，请稍后重新触发翻译。"); }
        finally { reading = false; }
    }
}
