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
    readonly ClipboardOperationHint _cutHint;

    public KeyboardHook(ClipboardWhitelist whitelist, ClipboardBlacklist blacklist, ClipboardOperationHint cutHint)
    {
        _whitelist = whitelist;
        _blacklist = blacklist;
        _cutHint   = cutHint;
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
                var kb    = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool ctrl  = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_CONTROL) & 0x8000) != 0;
                bool shift = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_SHIFT) & 0x8000) != 0;

                // Shift+Insert is the classic alternate paste shortcut (pre-dates Ctrl+V in some
                // terminals/apps, still widely supported) - it is just as visible to this
                // low-level hook as Ctrl+V, so it gets the same detection/blocking. Right-click
                // Paste, an app's own Paste button/menu/API call are NOT keystrokes at all and
                // remain outside what any keyboard hook can see - see AGENTS.md section 10.
                string? action = ctrl switch
                {
                    true when kb.vkCode == NativeMethods.VK_C => "copy",
                    true when kb.vkCode == NativeMethods.VK_X => "cut",
                    true when kb.vkCode == NativeMethods.VK_V => "paste",
                    true when kb.vkCode == NativeMethods.VK_Z => "undo",
                    _ => shift && kb.vkCode == NativeMethods.VK_INSERT ? "paste" : null
                };

                if (action is not null)
                {
                    EventEmitter.Emit(new KeyboardShortcutEvent(action, EventEmitter.Ts()));

                    // Windows gives no clipboard-format signal distinguishing a plain-text cut
                    // from a copy (unlike Explorer's file-drag "Preferred DropEffect" convention,
                    // which ClipboardActions.ReadDropEffect already reads correctly for Files) -
                    // mark it here so ClipboardMonitor's next WM_CLIPBOARDUPDATE-driven read can
                    // report "cut" instead of ClipboardActions.TryReadText()'s hardcoded "copy".
                    if (action == "cut")
                    {
                        _cutHint.MarkCut();
                    }

                    // Ctrl+V and Shift+Insert are the only paste triggers interceptable this way -
                    // swallow the keystroke only on the explicit, deliberate "policy violated"
                    // verdict below.
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
            if (content is null)
            {
                EventEmitter.EmitError("clipboard_read_failed", "could not open clipboard after retries");
                return false;
            }

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

            EventEmitter.Emit(new ClipboardContentBlockedEvent("paste", kind.Value, reason!, matchedPattern, changeEvent.EventId, EventEmitter.Ts()));
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
