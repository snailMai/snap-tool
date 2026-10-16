using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SnapTool.Core;

namespace SnapTool;

internal sealed class PinWindows(System.Func<ShortcutProfile>? getShortcuts = null)
{
    private readonly List<PinWindow> windows = [];
    private bool suspended;
    private PinWindow? pending;
    internal int Count => windows.Count;
    internal PinWindow Add(BitmapSource image, PixelRect selection)
    {
        var window = new PinWindow(image, selection, getShortcuts: getShortcuts);
        window.Closed += (_, _) => { windows.Remove(window); if (pending == window) pending = null; };
        windows.Add(window);
        try
        {
            if (suspended)
            {
                // Validate native window creation before the capture session is discarded.
                new WindowInteropHelper(window).EnsureHandle(); pending = window;
            }
            else { window.Show(); window.Activate(); }
            return window;
        }
        catch { window.Close(); throw; }
    }
    internal void Suspend()
    { suspended = true; foreach (var window in windows.ToArray()) window.Hide(); }
    internal void Resume()
    {
        if (!suspended) return;
        suspended = false;
        foreach (var window in windows.ToArray()) window.Show();
        pending?.Activate(); pending = null;
    }
    internal void Reposition() { foreach (var window in windows.ToArray()) window.Reposition(); }
    internal void CloseAll()
    { foreach (var window in windows.ToArray()) window.Close(); pending = null; }
}
