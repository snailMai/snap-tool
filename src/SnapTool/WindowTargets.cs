using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SnapTool.Core;

namespace SnapTool;

internal readonly record struct WindowTarget(IntPtr Handle, PixelRect Bounds);

internal static class WindowTargets
{
    internal static bool CanCaptureRegion(WindowTarget target, PixelRect region, params IntPtr[] ignoredWindows)
    {
        foreach (var window in Capture())
        {
            if (Array.IndexOf(ignoredWindows, window.Handle) >= 0) continue;
            if (window.Handle == target.Handle) return window.Bounds == target.Bounds && region.Intersect(window.Bounds) == region;
            if (!window.Bounds.Intersect(region).IsEmpty) return false;
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Native.Rect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetFrame(IntPtr window, int attribute, out Native.Rect rectangle, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetCloaked(IntPtr window, int attribute, out int cloaked, int size);

    // Read once before the screenshot overlay exists; never inspect another app's text or controls.
    internal static IReadOnlyList<WindowTarget> Capture()
    {
        var targets = new List<WindowTarget>();
        var visited = new HashSet<IntPtr>();
        var shell = GetShellWindow();
        // Windows can disappear or reorder during traversal. Bound the walk and reject repeated handles.
        for (var window = GetTopWindow(IntPtr.Zero);
            window != IntPtr.Zero && visited.Count < 4096 && visited.Add(window); window = GetWindow(window, 2))
        {
            if (window == shell || !IsWindowVisible(window) || IsIconic(window)) continue;
            if (GetCloaked(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW") continue;
            // DWM bounds exclude invisible resize borders and are already physical screen pixels.
            if (GetFrame(window, 9, out var rect, Marshal.SizeOf<Native.Rect>()) != 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                if (!GetWindowRect(window, out rect)) continue;
            var bounds = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (!bounds.IsEmpty) targets.Add(new(window, bounds));
        }
        return targets;
    }
}
