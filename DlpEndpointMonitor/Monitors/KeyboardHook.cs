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
    readonly ScreenshotBlockPolicy _screenshotBlockPolicy;

    // True while a VK_SNAPSHOT keydown has already been reported for the CURRENT physical
    // keypress - lets the keyup edge (checked because some keyboards/drivers only deliver
    // WM_KEYUP for PrintScreen, never WM_KEYDOWN) tell whether it still needs to report, without
    // ever reporting the same keypress twice. Reset back to false on the keyup edge regardless.
    bool _snapshotKeyDown;

    public KeyboardHook(ClipboardWhitelist whitelist, ClipboardBlacklist blacklist, ClipboardOperationHint cutHint, ScreenshotBlockPolicy screenshotBlockPolicy)
    {
        _whitelist = whitelist;
        _blacklist = blacklist;
        _cutHint   = cutHint;
        _screenshotBlockPolicy = screenshotBlockPolicy;
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
                KeyboardShortcutAction? action = ctrl switch
                {
                    true when kb.vkCode == NativeMethods.VK_C => KeyboardShortcutAction.Copy,
                    true when kb.vkCode == NativeMethods.VK_X => KeyboardShortcutAction.Cut,
                    true when kb.vkCode == NativeMethods.VK_V => KeyboardShortcutAction.Paste,
                    true when kb.vkCode == NativeMethods.VK_Z => KeyboardShortcutAction.Undo,
                    _ => shift && kb.vkCode == NativeMethods.VK_INSERT ? KeyboardShortcutAction.Paste : null
                };

                if (action is not null)
                {
                    EventEmitter.Emit(new KeyboardShortcutEvent(action.Value, EventEmitter.Ts()));

                    // Windows gives no clipboard-format signal distinguishing a plain-text cut
                    // from a copy (unlike Explorer's file-drag "Preferred DropEffect" convention,
                    // which ClipboardActions.ReadDropEffect already reads correctly for Files) -
                    // mark it here so ClipboardMonitor's next WM_CLIPBOARDUPDATE-driven read can
                    // report "cut" instead of ClipboardActions.TryReadText()'s hardcoded "copy".
                    if (action == KeyboardShortcutAction.Cut)
                    {
                        _cutHint.MarkCut();
                    }

                    // Ctrl+V and Shift+Insert are the only paste triggers interceptable this way -
                    // swallow the keystroke only on the explicit, deliberate "policy violated"
                    // verdict below.
                    if (action == KeyboardShortcutAction.Paste && ShouldBlockPaste())
                    {
                        return (IntPtr)1; // non-zero, no CallNextHookEx — keystroke never reaches any application
                    }
                }
            }

            // Screenshot shortcuts - independent of the Ctrl+C/X/V/Z + Shift+Insert block above,
            // since PrintScreen needs to be checked on BOTH keydown and keyup (see
            // HandleScreenshotShortcut's doc comment).
            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN or NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (HandleScreenshotShortcut(msg, kb))
                {
                    return (IntPtr)1; // non-zero, no CallNextHookEx — keystroke never reaches any application
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Detects the four OS-native screenshot shortcuts (PrintScreen, Alt+PrintScreen, Win+Alt+
    /// PrintScreen, Win+Shift+S) and reports/swallows them. Returns true if this specific edge
    /// should be swallowed (block policy enabled).
    ///
    /// PrintScreen (VK_SNAPSHOT) is checked on BOTH keydown and keyup - it is well known to be
    /// unreliable about which message a WH_KEYBOARD_LL hook receives it on depending on
    /// keyboard/driver (some deliver only WM_KEYUP, not WM_KEYDOWN, for historical BIOS/SysRq
    /// reasons), so whichever edge actually arrives is the one evaluated and (when enabled)
    /// swallowed. <see cref="_snapshotKeyDown"/> dedupes so a keyboard that DOES send both edges
    /// still reports exactly one KeyboardShortcutEvent per physical keypress, not two. Win+Shift+S
    /// is detected on the keydown of its final key (S) only, same as Ctrl+V's own detection above
    /// - it needs no such dedup.
    ///
    /// Always reports (KeyboardShortcutEvent) regardless of whether the policy is enabled, same
    /// as copy/cut/paste/undo above; only emits ScreenshotBlockedEvent and swallows when enabled.
    ///
    /// This covers OS-NATIVE keystroke shortcuts only. A third-party screenshot tool, the
    /// Start-Menu-launched Snipping Tool, or any non-keystroke trigger are explicitly out of
    /// scope here (a separate, larger feature) - matching this file's own honesty about paste's
    /// "right-click Paste is not interceptable" gap above.
    /// </summary>
    bool HandleScreenshotShortcut(uint msg, KBDLLHOOKSTRUCT kb)
    {
        bool isKeyDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool alt   = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_MENU) & 0x8000) != 0;
        bool win   = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_LWIN) & 0x8000) != 0
                  || (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_RWIN) & 0x8000) != 0;
        bool shift = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_SHIFT) & 0x8000) != 0;

        string? shortcut = kb.vkCode switch
        {
            NativeMethods.VK_SNAPSHOT => (win, alt) switch
            {
                (true, true) => "win_alt_print_screen",
                (false, true) => "alt_print_screen",
                // Covers no-Win/no-Alt, and Win-alone (Win+PrintScreen has its own distinct OS
                // behavior but is not one of the four shortcuts in scope for this feature).
                _ => "print_screen",
            },
            NativeMethods.VK_S when isKeyDown && win && shift => "win_shift_s",
            _ => null,
        };

        if (shortcut is null) return false;

        if (kb.vkCode == NativeMethods.VK_SNAPSHOT)
        {
            if (isKeyDown)
            {
                if (!_snapshotKeyDown)
                {
                    _snapshotKeyDown = true;
                    ReportScreenshot(shortcut);
                }
            }
            else
            {
                // Report here only if the keydown edge never fired for this press (the
                // keyup-only keyboard/driver case); either way this press is now over.
                if (!_snapshotKeyDown) ReportScreenshot(shortcut);
                _snapshotKeyDown = false;
            }
        }
        else
        {
            ReportScreenshot(shortcut); // Win+Shift+S — single keydown edge, nothing to dedupe
        }

        return _screenshotBlockPolicy.IsEnabled;
    }

    void ReportScreenshot(string shortcut)
    {
        EventEmitter.Emit(new KeyboardShortcutEvent(KeyboardShortcutAction.Screenshot, EventEmitter.Ts()));
        if (_screenshotBlockPolicy.IsEnabled)
        {
            EventEmitter.Emit(new ScreenshotBlockedEvent(shortcut, EventEmitter.Ts()));
        }
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
