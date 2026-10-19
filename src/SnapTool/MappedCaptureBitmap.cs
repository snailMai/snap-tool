using System;
using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapTool;

internal static class MappedCaptureBitmap
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ClrUsed, ClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc,
        ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();

    internal static BitmapSource Create(int width, int height, Action<IntPtr> draw)
    {
        if (width < 1 || height < 1 || (long)width * height > 128_000_000)
            throw new ArgumentOutOfRangeException(nameof(width));
        ArgumentNullException.ThrowIfNull(draw);
        int stride = checked(width * 4);
        using var section = MemoryMappedFile.CreateNew(null, checked((long)stride * height));
        var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        if (dc == IntPtr.Zero) throw new Win32Exception();
        try
        {
            bitmap = CreateDIBSection(dc, ref info, 0, out _, section.SafeMemoryMappedFileHandle.DangerousGetHandle(), 0);
            if (bitmap == IntPtr.Zero) throw new Win32Exception();
            previous = SelectObject(dc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception();
            // The new anonymous section is zero-filled, including gaps between monitors.
            draw(dc);
            if (!GdiFlush()) throw new Win32Exception();
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
        }
        // No GDI writer survives. WIC owns a read-only view after the section handle closes.
        var image = Imaging.CreateBitmapSourceFromMemorySection(section.SafeMemoryMappedFileHandle.DangerousGetHandle(),
            width, height, PixelFormats.Bgr32, stride, 0);
        image.Freeze(); return image;
    }
}
