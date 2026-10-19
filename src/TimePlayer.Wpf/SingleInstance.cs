using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace TimePlayer.Wpf;

/// <summary>
/// One TimePlayer at a time. Starting it again brings the open window to the front, and a link it was
/// started with (from a browser or another app) is handed to the open copy over a named pipe.
/// </summary>
public static class SingleInstance
{
    private const int SwRestore = 9;
    private const int AsfwAny = -1;
    private const int MaxMessageBytes = 8192;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Per Windows user, so another account's TimePlayer can't receive (or send) links.</summary>
    internal static string PipeName =>
        "TimePlayer-Links-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

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

    /// <summary>Hands <paramref name="link"/> to the open copy. False when it couldn't be reached.</summary>
    public static bool SendLinkToOtherCopy(string link, string? pipeName = null)
    {
        try
        {
            // This process was just started by the user's click, so it may give the open copy the right
            // to come to the front; Windows otherwise keeps focus where it is.
            AllowSetForegroundWindow(AsfwAny);
            using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(link);
            pipe.Write(bytes, 0, Math.Min(bytes.Length, MaxMessageBytes));
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Receives links from copies started later, until <paramref name="stop"/> is cancelled.</summary>
    public static async Task ListenForLinksAsync(Action<string> onLink, Action<Exception> onError, CancellationToken stop, string? pipeName = null)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName ?? PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var buffer = new MemoryStream();
                var chunk = new byte[1024];
                int read;
                while (buffer.Length < MaxMessageBytes && (read = await pipe.ReadAsync(chunk, stop)) > 0)
                    buffer.Write(chunk, 0, read);
                var link = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)Math.Min(buffer.Length, MaxMessageBytes));
                if (!string.IsNullOrWhiteSpace(link))
                    onLink(link);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onError(ex);
                try
                {
                    await Task.Delay(500, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
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
