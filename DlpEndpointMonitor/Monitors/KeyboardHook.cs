using System.Runtime.InteropServices;
using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Win32;

namespace DlpEndpointMonitor.Monitors;

// Detects Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+Z globally, and (Ctrl+V only) intercepts the
// keystroke before it reaches any application if the CURRENT clipboard content violates policy.
// Must be created on the thread that runs the message loop
sealed class KeyboardHook : IDisposable
{
    IntPtr _hHook = IntPtr.Zero;
    readonly HookProc _proc; // keep the delegate alive
    readonly ClipboardWhitelist _whitelist;
    readonly ClipboardBlacklist _blacklist;

    public KeyboardHook(ClipboardWhitelist whitelist, ClipboardBlacklist blacklist)
    {
        _whitelist = whitelist;
        _blacklist = blacklist;
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

                    // Only Ctrl+V is interceptable this way (right-click Paste, Shift+Insert,
                    // an app's own Paste button/API are not) - swallow the keystroke only on the
                    // explicit, deliberate "policy violated" verdict below.
                    if (action == "paste" && ShouldBlockPaste())
                    {
                        return (IntPtr)1; // non-zero, no CallNextHookEx — keystroke never reaches any application
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Live-evaluates whatever is CURRENTLY on the clipboard, in the hook callback, before the
    /// keystroke reaches any application. FAILS OPEN BY DESIGN: this is a global, system-wide
    /// WH_KEYBOARD_LL hook, unlike device blocking's fail-closed defaults - any exception here
    /// (malformed regex, a Win32 read failure, anything) must fall through to letting the
    /// keystroke through. A bug that swallows Ctrl+V without meaning to breaks paste for EVERY
    /// application on the machine, silently, until the process restarts - a far worse failure
    /// mode than occasionally letting through content that should have been blocked.
    /// </summary>
    bool ShouldBlockPaste()
    {
        try
        {
            var content = ClipboardActions.Read();
            if (content is null) return false;

            // Always report what is being pasted, regardless of verdict - this is what
            // satisfies "detect what is being pasted", same event shapes as copy/cut.
            IEvent changeEvent = content.Type switch
            {
                ClipboardContentType.Text  => new ClipboardTextEvent("paste", content.Text, EventEmitter.Ts()),
                ClipboardContentType.Files => new ClipboardFilesEvent("paste", content.Files, EventEmitter.Ts()),
                ClipboardContentType.Image => new ClipboardImageEvent(EventEmitter.Ts()),
                _                          => new ClipboardUnknownEvent(EventEmitter.Ts())
            };
            EventEmitter.Emit(changeEvent);

            ClipboardKind? kind = content.Type switch
            {
                ClipboardContentType.Text  => ClipboardKind.Text,
                ClipboardContentType.Files => ClipboardKind.Files,
                _                          => null
            };
            if (kind is null) return false; // Image/Unknown never evaluated against policy

            IReadOnlyList<string> candidates = kind == ClipboardKind.Text
                ? (content.Text is null ? Array.Empty<string>() : new[] { content.Text })
                : (content.Files ?? Array.Empty<string>());

            var (violates, reason, matchedPattern) = ClipboardMonitor.EvaluatePolicy(_whitelist, _blacklist, kind.Value, candidates);
            if (!violates) return false;

            EventEmitter.Emit(new ClipboardContentBlockedEvent("paste", kind.Value, reason!, matchedPattern, EventEmitter.Ts()));
            return true;
        }
        catch
        {
            return false; // fail OPEN — never swallow a keystroke on an unexpected failure
        }
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
