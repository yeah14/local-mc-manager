using System.Runtime.InteropServices;
using System.Text;

namespace McPanel;

internal static class Win32
{
    const int SW_RESTORE = 9;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    public static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll")]
    public static extern bool SetConsoleCP(uint wCodePageID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetConsoleTitle(string lpConsoleTitle);

    public static IntPtr FindVisibleWindow(int pid, string? titleContains)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindowsProc callback = (hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != (uint)pid || !IsWindowVisible(hWnd))
                return true;
            if (!string.IsNullOrEmpty(titleContains))
            {
                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (!sb.ToString().Contains(titleContains, StringComparison.Ordinal))
                    return true;
            }
            found = hWnd;
            return false;
        };
        EnumWindows(callback, IntPtr.Zero);
        return found;
    }

    public static void BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return;
        ShowWindow(hWnd, SW_RESTORE);
        var foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        uint thisThread = GetCurrentThreadId();
        AttachThreadInput(thisThread, foregroundThread, true);
        AttachThreadInput(thisThread, targetThread, true);
        SetForegroundWindow(hWnd);
        AttachThreadInput(thisThread, targetThread, false);
        AttachThreadInput(thisThread, foregroundThread, false);
    }
}
