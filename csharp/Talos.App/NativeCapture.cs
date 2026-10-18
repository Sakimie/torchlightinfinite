using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Talos.App;

internal sealed record CaptureTarget(nint Handle, string Name)
{
    // Recorded on enumeration; the two-argument manual-capture API remains valid.
    public uint ProcessId { get; init; }
    public override string ToString() => Name;
}

internal static class NativeCapture
{
    public const int HotkeyId = 0x5A10;
    public const int WmHotkey = 0x0312;
    private const uint GaRoot = 2, GaRootOwner = 3;
    private const uint PwClientOnly = 1, PwRenderFullContent = 2;
    private const uint SourceCopy = 0x00CC0020, Blackness = 0x00000042;

    public static bool Register(nint window) => RegisterHotKey(window, HotkeyId, 0x4000, 0x77);
    public static void Unregister(nint window) => UnregisterHotKey(window, HotkeyId);

    public static List<CaptureTarget> Sources()
    {
        var list = new List<CaptureTarget> { new(0, "当前显示器（全屏）") };
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsCloaked(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0 || processId == (uint)Environment.ProcessId) return true;
            var title = WindowTitle(window);
            if (IsGameTitle(title)) list.Add(new CaptureTarget(window, title) { ProcessId = processId });
            return true;
        }, 0);
        return list;
    }

    /// <summary>
    /// Automatic following is restricted to the explicitly selected, visible game
    /// window in the foreground. Call before hiding helper windows; CaptureGame
    /// repeats the check immediately around reading pixels to cover focus changes.
    /// </summary>
    public static bool CanAutoCapture(CaptureTarget target, out string reason)
    {
        if (!OperatingSystem.IsWindows()) { reason = "自动截图仅支持 Windows。"; return false; }
        if (target is null || target.Handle == 0)
        {
            reason = "自动跟随需要选定火炬之光游戏窗口，不能使用全屏来源。"; return false;
        }
        if (target.ProcessId == 0) { reason = "请刷新截图来源并重新选择游戏窗口，再开启自动跟随。"; return false; }
        if (!CheckGameWindow(target, out reason)) return false;
        if (!IsGameForeground(target.Handle))
        {
            reason = "游戏未处于前台，跟随已暂停；切回游戏后继续。"; return false;
        }
        reason = ""; return true;
    }

    /// <summary>Manual capture retains the existing selected-window/full-monitor API.</summary>
    public static BitmapSource Capture(CaptureTarget target, nint helperWindow) => CaptureCore(target, helperWindow, automatic: false);

    /// <summary>
    /// For automatic following only. The caller must hide its main/overlay windows
    /// temporarily without activating another application, then call on a worker.
    /// An invalid/covered/blank frame is rejected, never returned as a game reading.
    /// </summary>
    public static BitmapSource CaptureGame(CaptureTarget target, nint helperWindow) => CaptureCore(target, helperWindow, automatic: true);

    private static BitmapSource CaptureCore(CaptureTarget target, nint helperWindow, bool automatic)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本机截图仅支持 Windows。");
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (automatic && !CanAutoCapture(target, out var initialReason)) throw new InvalidOperationException(initialReason);
        nint oldDpi = SetThreadDpiAwarenessContext(new nint(-4));
        nint screen = 0, dc = 0, bitmap = 0, previous = 0;
        try
        {
            Rect rect;
            if (target.Handle != 0)
            {
                if (!CheckGameWindow(target, out var windowReason)) throw new InvalidOperationException(windowReason);
                if (!GetClientRect(target.Handle, out var client)) throw new InvalidOperationException("无法取得游戏窗口尺寸。");
                var point = new Point();
                if (!ClientToScreen(target.Handle, ref point)) throw new InvalidOperationException("无法定位游戏窗口。");
                rect = new Rect { Left = point.X, Top = point.Y, Right = checked(point.X + client.Right - client.Left), Bottom = checked(point.Y + client.Bottom - client.Top) };
            }
            else
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(helperWindow, 2), ref info)) throw new InvalidOperationException("无法读取当前显示器。");
                rect = info.Monitor;
            }
            long captureWidth = (long)rect.Right - rect.Left, captureHeight = (long)rect.Bottom - rect.Top;
            if (captureWidth < 10 || captureHeight < 10 || captureWidth * captureHeight > 24_000_000)
                throw new InvalidOperationException("截图尺寸无效或超过2400万像素，请调低游戏分辨率。");
            int width = (int)captureWidth, height = (int)captureHeight;
            screen = GetDC(0);
            if (screen == 0) throw new InvalidOperationException("无法读取当前桌面，请恢复游戏画面后重试。");
            dc = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height);
            if (dc == 0 || bitmap == 0) throw new InvalidOperationException("无法建立本机截图缓冲区。");
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == new nint(-1)) throw new InvalidOperationException("无法建立完整截图缓冲区。");

            byte[]? bytes = null;
            bool usedScreen = false;
            if (target.Handle != 0)
            {
                if (automatic && !CanAutoCapture(target, out var printReason)) throw new InvalidOperationException(printReason);
                // Clear the buffer so a partial/failed PrintWindow cannot expose old
                // bitmap memory as a successful capture. No game input is sent.
                bool cleared = PatBlt(dc, 0, 0, width, height, Blackness);
                if (cleared && PrintWindow(target.Handle, dc, PwClientOnly | PwRenderFullContent))
                {
                    var candidate = ReadBitmap(screen, dc, bitmap, previous, width, height);
                    if (HasUsefulPixels(candidate)) bytes = candidate;
                }
            }
            if (bytes is null)
            {
                if (target.Handle != 0)
                {
                    // DirectX games may not implement PrintWindow. Reading the
                    // screen is then allowed only for the same foreground game,
                    // entirely on the desktop and with no overlapping top window.
                    if (!IsGameForeground(target.Handle)) throw new InvalidOperationException("游戏未处于前台，未读取其他窗口。请切回游戏后重试。");
                    if (!CanReadUnobscuredClient(target.Handle, rect, out var coveringReason)) throw new InvalidOperationException(coveringReason);
                }
                usedScreen = true;
                if (!BitBlt(dc, 0, 0, width, height, screen, rect.Left, rect.Top, SourceCopy))
                    throw new InvalidOperationException("屏幕读取失败。可将游戏切换为无边框窗口，或导入截图。");
                bytes = ReadBitmap(screen, dc, bitmap, previous, width, height);
                if (!HasUsefulPixels(bytes)) throw new InvalidOperationException("未取得可用游戏画面，请等待加载结束，或切换无边框窗口后重试。");
            }
            if (target.Handle != 0)
            {
                if (!CheckGameWindow(target, out var finalReason)) throw new InvalidOperationException(finalReason);
                if (automatic && !CanAutoCapture(target, out var focusReason)) throw new InvalidOperationException(focusReason);
                if (!GetClientRect(target.Handle, out var finalClient)) throw new InvalidOperationException("截图期间游戏窗口已改变，请重试。");
                var finalOrigin = new Point();
                if (!ClientToScreen(target.Handle, ref finalOrigin) || finalOrigin.X != rect.Left || finalOrigin.Y != rect.Top ||
                    (long)finalClient.Right - finalClient.Left != width || (long)finalClient.Bottom - finalClient.Top != height)
                    throw new InvalidOperationException("截图期间游戏窗口位置或大小已改变，请等待稳定后重试。");
                if (usedScreen && (!IsGameForeground(target.Handle) || !CanReadUnobscuredClient(target.Handle, rect, out _)))
                    throw new InvalidOperationException("截图期间游戏被遮挡或切到了后台，本帧已丢弃。");
            }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bytes, width * 4);
            result.Freeze(); return result;
        }
        finally
        {
            if (previous != 0 && previous != new nint(-1) && dc != 0) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (dc != 0) DeleteDC(dc);
            if (screen != 0) ReleaseDC(0, screen);
            if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi);
        }
    }

    private static byte[] ReadBitmap(nint screen, nint dc, nint bitmap, nint previous, int width, int height)
    {
        var restored = SelectObject(dc, previous);
        if (restored == 0 || restored == new nint(-1)) throw new InvalidOperationException("截图缓冲区无法读取。");
        try
        {
            var header = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var bytes = new byte[checked(width * height * 4)];
            if (GetDIBits(screen, bitmap, 0, (uint)height, bytes, ref header, 0) != height)
                throw new InvalidOperationException("无法读取完整截图像素。");
            return bytes;
        }
        finally { SelectObject(dc, bitmap); }
    }

    private static bool CheckGameWindow(CaptureTarget target, out string reason)
    {
        var window = target.Handle;
        if (!IsWindow(window)) { reason = "游戏窗口已关闭或失效，请刷新截图来源。"; return false; }
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId || target.ProcessId != 0 && processId != target.ProcessId)
        { reason = "游戏窗口的所属进程已改变，请刷新并重新选择截图来源。"; return false; }
        if (IsIconic(window)) { reason = "游戏已最小化，跟随已暂停；恢复游戏后继续。"; return false; }
        if (!IsWindowVisible(window) || IsCloaked(window)) { reason = "游戏窗口当前不可见，跟随已暂停。"; return false; }
        if (!IsGameTitle(WindowTitle(window))) { reason = "所选窗口不再是火炬之光，请重新选择游戏窗口。"; return false; }
        if (IsHungAppWindow(window)) { reason = "游戏暂未响应，已暂停截图；恢复后重试。"; return false; }
        reason = ""; return true;
    }
    private static bool IsGameForeground(nint window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || !IsWindow(foreground)) return false;
        var selectedRoot = GetAncestor(window, GaRootOwner);
        var foregroundRoot = GetAncestor(foreground, GaRootOwner);
        return selectedRoot != 0 && foregroundRoot == selectedRoot;
    }
    private static bool CanReadUnobscuredClient(nint window, Rect client, out string reason)
    {
        reason = "游戏画面被其他窗口遮挡，已跳过截图。请移开遮挡窗口后重试。";
        long desktopLeft = GetSystemMetrics(76), desktopTop = GetSystemMetrics(77);
        long desktopRight = desktopLeft + GetSystemMetrics(78), desktopBottom = desktopTop + GetSystemMetrics(79);
        if (client.Left < desktopLeft || client.Top < desktopTop || client.Right > desktopRight || client.Bottom > desktopBottom)
        { reason = "游戏窗口未完整显示在屏幕内，请将它移回可见区域。"; return false; }
        var root = GetAncestor(window, GaRoot);
        bool found = false;
        int visited = 0;
        for (var candidate = GetTopWindow(0); candidate != 0 && visited++ < 10000; candidate = GetWindow(candidate, 2))
        {
            if (candidate == root) { found = true; break; }
            if (!IsWindowVisible(candidate) || IsIconic(candidate) || IsCloaked(candidate)) continue;
            if (GetWindowRect(candidate, out var bounds) && Intersects(bounds, client)) return false;
        }
        if (!found) { reason = "无法确认当前游戏窗口的显示层级，请恢复游戏后重试。"; return false; }
        // Also detect desktop gaps, child/owned popup overlays, and a focus change
        // at representative client points rather than silently accepting a crop.
        foreach (var x in new[] { .1, .5, .9 }) foreach (var y in new[] { .1, .5, .9 })
        {
            var point = new Point { X = client.Left + (int)((client.Right - (long)client.Left) * x), Y = client.Top + (int)((client.Bottom - (long)client.Top) * y) };
            var hit = WindowFromPoint(point);
            if (hit == 0 || GetAncestor(hit, GaRoot) != root) return false;
        }
        reason = ""; return true;
    }
    private static bool Intersects(Rect first, Rect second) => first.Right > first.Left && first.Bottom > first.Top &&
        (long)first.Left < second.Right && (long)first.Right > second.Left && (long)first.Top < second.Bottom && (long)first.Bottom > second.Top;
    private static bool HasUsefulPixels(byte[] pixels)
    {
        int samples = 0, bright = 0, minimum = 255, maximum = 0;
        double sum = 0, squares = 0;
        int step = Math.Max(1, pixels.Length / 4 / 12000);
        for (int pixel = 0; pixel < pixels.Length / 4; pixel += step)
        {
            int offset = pixel * 4;
            int value = Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
            minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
            if (value >= 25) bright++;
            samples++; sum += value; squares += value * value;
        }
        if (samples == 0) return false;
        double variance = squares / samples - Math.Pow(sum / samples, 2);
        return maximum - minimum >= 16 && maximum >= 30 && bright >= samples * .005 && variance >= 9;
    }
    private static string WindowTitle(nint window) { var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    private static bool IsGameTitle(string title) => title.Contains("Torchlight", StringComparison.OrdinalIgnoreCase) || title.Contains("火炬之光", StringComparison.Ordinal);
    private static bool IsCloaked(nint window) => DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important; }
    private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int metric);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint target, uint flags);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(nint dc, int x, int y, int width, int height, uint operation);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint flags);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint count, byte[] data, ref BitmapInfo info, uint usage);
}
