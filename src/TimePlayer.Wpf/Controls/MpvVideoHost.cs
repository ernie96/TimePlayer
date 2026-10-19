using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TimePlayer.Wpf.Controls;

/// <summary>
/// WPF HwndHost control providing a native Win32 HWND surface for mpv video output rendering.
/// Ensures clean embedding, automatic resizing with layout, and preservation of keyboard focus.
/// </summary>
public sealed class MpvVideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    private const int WmEraseBkgnd = 0x0014;

    private static readonly string ClassName = $"TimePlayerVideoHost_{Guid.NewGuid():N}";
    private static bool _classRegistered;

    private nint _hwndHost = IntPtr.Zero;

    public nint WindowHandle => _hwndHost;

    public event EventHandler? HandleCreated;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClassRegistered();

        var width = (int)ActualWidth > 0 ? (int)ActualWidth : 640;
        var height = (int)ActualHeight > 0 ? (int)ActualHeight : 360;

        _hwndHost = CreateWindowEx(
            0,
            ClassName,
            "TimePlayerVideoHost",
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            width,
            height,
            hwndParent.Handle,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwndHost == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to create native video host HWND. Win32 error: {error}");
        }

        HandleCreated?.Invoke(this, EventArgs.Empty);

        return new HandleRef(this, _hwndHost);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
            _hwndHost = IntPtr.Zero;
        }
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEraseBkgnd:
                // Suppress background erasing to avoid flicker during video playback
                handled = true;
                return 1;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, int uMsg, nint wParam, nint lParam);

    private static readonly WndProcDelegate DefaultWndProcHandler = DefWindowProc;

    private static void EnsureWindowClassRegistered()
    {
        if (_classRegistered) return;

        var wndClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = DefaultWndProcHandler,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero, // No background brush to prevent flickering
            lpszMenuName = null,
            lpszClassName = ClassName,
            hIconSm = IntPtr.Zero
        };

        var atom = RegisterClassEx(ref wndClass);
        if (atom == 0)
        {
            var error = Marshal.GetLastWin32Error();
            // 1410 = ERROR_CLASS_ALREADY_EXISTS
            if (error != 1410)
            {
                throw new InvalidOperationException($"Failed to register window class for MpvVideoHost. Win32 error: {error}");
            }
        }

        _classRegistered = true;
    }

    #region Win32 P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, int uMsg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    #endregion
}
