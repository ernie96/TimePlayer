using System.Runtime.InteropServices;

namespace TimePlayer.Core.Accessibility;

/// <summary>
/// Safe P/Invoke bridge to NVDA Controller Client for direct Braille output on Windows.
/// Safely falls back if NVDA is not running or if the DLL is absent.
/// </summary>
public static class NvdaBrailleBridge
{
    private static bool? _isAvailable;

    static NvdaBrailleBridge()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            NativeLibrary.SetDllImportResolver(typeof(NvdaBrailleBridge).Assembly, ResolveDll);
        }
    }

    private static IntPtr ResolveDll(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "nvdaControllerClient64.dll" or "nvdaControllerClient.dll" or "nvdaControllerClient")
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "nvdaControllerClient64.dll"),
                Path.Combine(baseDir, "nvdaControllerClient.dll"),
                Path.Combine(baseDir, "bin", "nvdaControllerClient64.dll"),
                Path.Combine(baseDir, "bin", "nvdaControllerClient.dll"),
                Path.Combine(baseDir, "lib", "x64", "nvdaControllerClient64.dll"),
                Path.Combine(baseDir, "lib", "x64", "nvdaControllerClient.dll")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                {
                    return handle;
                }
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_testIfRunning", CallingConvention = CallingConvention.StdCall)]
    private static extern int NvdaTestIfRunning();

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_brailleMessage", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    private static extern int NvdaBrailleMessage(string message);

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_speakText", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    private static extern int NvdaSpeakText(string text);

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_cancelSpeech", CallingConvention = CallingConvention.StdCall)]
    private static extern int NvdaCancelSpeech();

    private static long _nvdaCheckedAtTicks;
    private static bool _nvdaRunningCached;

    /// <summary>
    /// Stops whatever NVDA is saying and speaks <paramref name="text"/> right away.
    /// Returns false when NVDA is not running (or the controller DLL is missing), so the caller can fall back.
    /// </summary>
    public static bool SpeakInterrupting(string text)
    {
        if (_isAvailable == false || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        // Asking NVDA whether it runs is a cross-process call; don't do it for every caption.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _nvdaCheckedAtTicks) > 5000)
        {
            _nvdaRunningCached = IsNvdaRunning();
            Interlocked.Exchange(ref _nvdaCheckedAtTicks, now);
        }
        if (!_nvdaRunningCached)
            return false;

        try
        {
            NvdaCancelSpeech();
            return NvdaSpeakText(text) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _isAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Checks if NVDA is actively running with controller client support.
    /// </summary>
    public static bool IsNvdaRunning()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            return NvdaTestIfRunning() == 0;
        }
        catch (DllNotFoundException)
        {
            _isAvailable = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _isAvailable = false;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>File the TimePlayer NVDA add-on polls for the progress track state.</summary>
    public static readonly string ProgressStateFilePath =
        Path.Combine(Path.GetTempPath(), "timeplayer_braille_state.txt");

    private static string? _lastPublishedState;

    /// <summary>
    /// Publishes the progress track state for the NVDA add-on:
    /// "playing 0.4231", "paused 0.4231", "stopped" or "off".
    /// The add-on renders it at the display's real width and handles blinking itself.
    /// </summary>
    public static void PublishProgressState(string state)
    {
        if (state == _lastPublishedState)
            return;

        try
        {
            // Write-then-replace so the add-on never reads a half-written line.
            var tempPath = ProgressStateFilePath + ".tmp";
            File.WriteAllText(tempPath, state);
            File.Move(tempPath, ProgressStateFilePath, overwrite: true);
            _lastPublishedState = state;
        }
        catch
        {
            // Retried on the next position update.
        }
    }

    public static void ClearProgressState()
    {
        _lastPublishedState = null;
        try { File.Delete(ProgressStateFilePath); } catch { }
    }

    /// <summary>
    /// Sends a Braille message string directly to the NVDA Braille display.
    /// Returns true if successfully delivered; false otherwise.
    /// </summary>
    public static bool SendBrailleMessage(string message)
    {
        if (_isAvailable == false || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            return NvdaBrailleMessage(message ?? string.Empty) == 0;
        }
        catch (DllNotFoundException)
        {
            _isAvailable = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _isAvailable = false;
            return false;
        }
        catch
        {
            return false;
        }
    }
}
