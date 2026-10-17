using System.Runtime.InteropServices;

namespace TimePlayer.Core.Accessibility;

/// <summary>
/// Safe P/Invoke bridge to NVDA Controller Client for direct Braille output on Windows.
/// Safely falls back if NVDA is not running or if the DLL is absent.
/// </summary>
public static class NvdaBrailleBridge
{
    private static bool? _isAvailable;

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_testIfRunning", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvdaTestIfRunning();

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_brailleMessage", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvdaBrailleMessage(string message);

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
            return NvdaBrailleMessage(message) == 0;
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
