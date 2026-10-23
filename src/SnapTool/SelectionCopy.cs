using System;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTool;

internal sealed class SelectionCopy(Func<IntPtr> foreground, Func<bool> keysPressed, Func<uint> sequence,
    Func<bool> sendCopy, Func<IntPtr, bool> ownsClipboard)
{
    internal static SelectionCopy ForShortcut(ShortcutGesture shortcut) => new(Native.GetForegroundWindow,
        () => Native.CopyKeysPressed(shortcut), Native.GetClipboardSequenceNumber, () => Native.SendCopy(), Native.OwnsClipboard);

    internal async Task<bool> Run(IntPtr target, CancellationToken token)
    {
        // Do not release physical keys on the user's behalf or redirect input to another window.
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (target == IntPtr.Zero || foreground() != target) return false;
            if (!keysPressed()) break;
            if (attempt == 74) return false;
            await Task.Delay(20, token);
        }
        uint before = sequence();
        if (before == 0 || !sendCopy()) return false;
        for (int attempt = 0; attempt < 75; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (foreground() != target) return false;
            uint current = sequence();
            if (current != 0 && current != before) return ownsClipboard(target);
            await Task.Delay(20, token);
        }
        return false;
    }
}
