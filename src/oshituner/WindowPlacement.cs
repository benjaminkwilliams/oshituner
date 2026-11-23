using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace oshituner;

internal sealed record WindowBounds(int X, int Y, int Width, int Height, string? Monitor);

internal static class WindowPlacement
{
    public static WindowBounds? Read(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || window.WindowState != WindowState.Normal || !GetWindowRect(handle, out var rectangle))
            return null;
        return new(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top,
            GetMonitor(MonitorFromWindow(handle, 2))?.Device);
    }

    public static void Apply(Window window, WindowBounds? bounds, Window fallback)
    {
        if (bounds is not { Width: > 0 and <= 32768, Height: > 0 and <= 32768 }) return;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var fallbackHandle = new WindowInteropHelper(fallback).Handle;
        var rectangle = new NativeRect(bounds.X, bounds.Y,
            (int)Math.Clamp((long)bounds.X + bounds.Width, int.MinValue, int.MaxValue),
            (int)Math.Clamp((long)bounds.Y + bounds.Height, int.MinValue, int.MaxValue));

        MonitorInfo? selected = null;
        if (!string.IsNullOrEmpty(bounds.Monitor))
        {
            EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
            {
                var candidate = GetMonitor(monitor);
                if (!string.Equals(candidate?.Device, bounds.Monitor, StringComparison.OrdinalIgnoreCase)) return true;
                selected = candidate;
                return false;
            }, 0);
        }

        selected ??= GetMonitor(bounds.Monitor is null ? MonitorFromRect(ref rectangle, 2)
            : MonitorFromWindow(fallbackHandle, 2));
        if (selected is not { } info) return;

        var area = info.Work;
        var width = Math.Min(bounds.Width, area.Right - area.Left);
        var height = Math.Min(bounds.Height, area.Bottom - area.Top);
        if (width <= 0 || height <= 0) return;
        SetWindowPos(handle, 0, Math.Clamp(bounds.X, area.Left, area.Right - width),
            Math.Clamp(bounds.Y, area.Top, area.Bottom - height), width, height, 0x0014);
    }

    public static WindowBounds NearPointer(WindowBounds original, NativePoint offset) =>
        GetCursorPos(out var point) ? original with { X = point.X - offset.X, Y = point.Y - offset.Y, Monitor = null } : original;

    public static NativePoint PointerOffset(WindowBounds bounds) =>
        GetCursorPos(out var point) ? new(point.X - bounds.X, point.Y - bounds.Y) : new(32, 16);

    private static MonitorInfo? GetMonitor(nint monitor)
    {
        if (monitor == 0) return null;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? info : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? Device;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(nint monitor, nint context, nint rectangle, nint data);

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect rectangle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint context, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
