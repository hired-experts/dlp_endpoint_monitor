using System.Runtime.InteropServices;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Monitors;

// Detects Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+Z globally
// Must be created on the thread that runs the message loop
sealed class KeyboardHook : IDisposable
{
    IntPtr _hHook = IntPtr.Zero;
    readonly HookProc _proc; // keep the delegate alive

    public KeyboardHook()
    {
        _proc  = Callback;
        _hHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0); // 0 = system-wide

        if (_hHook == IntPtr.Zero)
            throw new Win32Exception("SetWindowsHookEx");
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == NativeMethods.HC_ACTION)
        {
            uint msg = (uint)wParam;

            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                var kb   = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool ctrl = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_CONTROL) & 0x8000) != 0;

                if (ctrl)
                {
                    string? action = kb.vkCode switch
                    {
                        NativeMethods.VK_C => "copy",
                        NativeMethods.VK_X => "cut",
                        NativeMethods.VK_V => "paste",
                        NativeMethods.VK_Z => "undo",
                        _                  => null
                    };

                    if (action is not null)
                    {
                        EventEmitter.Emit(new KeyboardShortcutEvent(action, EventEmitter.Ts()));
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hHook);
            _hHook = IntPtr.Zero;
        }
    }
}
