using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TimePlayer.Wpf;

/// <summary>Brings an already running TimePlayer to the front when it is started again.</summary>
internal static class SingleInstance
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    public static void BringOtherCopyToFront()
    {
        var current = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("TimePlayer"))
        {
            using (process)
            {
                if (process.Id == current || process.MainWindowHandle == IntPtr.Zero)
                    continue;
                if (IsIconic(process.MainWindowHandle))
                    ShowWindow(process.MainWindowHandle, SwRestore);
                SetForegroundWindow(process.MainWindowHandle);
                return;
            }
        }
    }

    /// <summary>Other TimePlayer processes (older versions allowed several), which would block an update.</summary>
    public static int OtherCopiesRunning()
    {
        var current = Environment.ProcessId;
        var count = 0;
        foreach (var process in Process.GetProcessesByName("TimePlayer"))
        {
            using (process)
            {
                if (process.Id != current)
                    count++;
            }
        }
        return count;
    }
}
