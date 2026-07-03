using System.Runtime.InteropServices;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Core;

sealed class MessageWindow : IDisposable
{
    IntPtr _hWnd       = IntPtr.Zero;
    IntPtr _hDevNotify = IntPtr.Zero;

    // Delegates must be kept alive — the GC cannot collect them
    // while the unmanaged window proc reference is active
    readonly WndProc _wndProc;

    public event Action?              ClipboardChanged;
    public event Action<int, IntPtr>? DeviceChanged;    // (wParam, lParam)
    public event Action?              DisplayChanged;

    public IntPtr Handle => _hWnd;

    public MessageWindow()
    {
        _wndProc = WndProc; // pin before passing to Win32

        var wc = new WNDCLASSEX
        {
            cbSize        = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc   = _wndProc,
            hInstance     = NativeMethods.GetModuleHandle(null),
            lpszClassName = "CUMonitor_" + Guid.NewGuid().ToString("N")[..8]
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
            throw new Win32Exception("RegisterClassEx");

        // WS_POPUP off-screen: invisible, no taskbar entry, no focus stealing.
        // We do NOT use HWND_MESSAGE because message-only windows do not receive
        // broadcast messages such as WM_DISPLAYCHANGE.
        _hWnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
            wc.lpszClassName, "Monitor",
            NativeMethods.WS_POPUP,
            -1, -1, 1, 1,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hWnd == IntPtr.Zero)
            throw new Win32Exception("CreateWindowEx");

        // Event-driven clipboard — fires WM_CLIPBOARDUPDATE on every change
        NativeMethods.AddClipboardFormatListener(_hWnd);

        // Register for ALL device interface classes (USB, disk, etc.)
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size       = (uint)Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = NativeMethods.DBT_DEVTYP_DEVICEINTERFACE
        };

        IntPtr pFilter = Marshal.AllocHGlobal(Marshal.SizeOf(filter));
        try
        {
            Marshal.StructureToPtr(filter, pFilter, false);
            _hDevNotify = NativeMethods.RegisterDeviceNotification(
                _hWnd, pFilter,
                NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE |
                NativeMethods.DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
        }
        finally
        {
            Marshal.FreeHGlobal(pFilter);
        }
    }

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_CLIPBOARDUPDATE:
                ClipboardChanged?.Invoke();
                return IntPtr.Zero;

            case NativeMethods.WM_DEVICECHANGE:
                DeviceChanged?.Invoke((int)wParam, lParam);
                return IntPtr.Zero;

            case NativeMethods.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                NativeMethods.RemoveClipboardFormatListener(hWnd);
                if (_hDevNotify != IntPtr.Zero)
                    NativeMethods.UnregisterDeviceNotification(_hDevNotify);
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // Blocks the calling thread — run this on the dedicated STA thread
    public static void RunMessageLoop()
    {
        while (NativeMethods.GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    public void Stop() =>
        NativeMethods.PostMessage(_hWnd, NativeMethods.WM_DESTROY, IntPtr.Zero, IntPtr.Zero);

    public void Dispose()
    {
        if (_hWnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hWnd);
            _hWnd = IntPtr.Zero;
        }
    }
}

// Small helper so we don't import System.ComponentModel
class Win32Exception(string call) : Exception(
    $"{call} failed with error {Marshal.GetLastWin32Error()}");
