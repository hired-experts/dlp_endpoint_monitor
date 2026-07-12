using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Monitors;

sealed class ClipboardMonitor : IDisposable
{
    readonly MessageWindow      _window;
    readonly ClipboardWhitelist _whitelist;
    readonly ClipboardBlacklist _blacklist;
    readonly ClipboardOperationHint _cutHint;

    public ClipboardMonitor(MessageWindow window, ClipboardWhitelist whitelist, ClipboardBlacklist blacklist, ClipboardOperationHint cutHint)
    {
        _window    = window;
        _whitelist = whitelist;
        _blacklist = blacklist;
        _cutHint   = cutHint;
        _window.ClipboardChanged += OnClipboardChanged;
    }

    void OnClipboardChanged()
    {
        try
        {
            var content = ClipboardActions.Read();
            if (content is null)
            {
                EventEmitter.EmitError("clipboard_read_failed", "could not open clipboard after retries");
                return;
            }

            // Consumed unconditionally (regardless of content type) so a cut hint can never
            // bleed into a later, unrelated clipboard change - see ClipboardOperationHint's own
            // doc comment. Only Text needs the override: Files already correctly distinguishes
            // cut/copy via ClipboardActions.ReadDropEffect (Explorer's own drop-effect format).
            bool recentCut = _cutHint.ConsumeRecentCut();
            if (recentCut && content.Type == ClipboardContentType.Text)
                content = content with { Operation = "cut" };

            EvaluateAndEnforce(content);
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("clipboard_monitor", ex.Message);
        }
    }

    /// <summary>
    /// Re-checks whatever is CURRENTLY on the clipboard against the (possibly just-changed)
    /// policy and clears it if it now violates - called by the protection handler's reevaluate
    /// delegate after every whitelist/blacklist mutation, since clipboard content has no
    /// persisted "this was blocked" record to restore later (unlike device policy): a
    /// newly-tightened policy must clear already-present non-compliant content immediately.
    /// </summary>
    public void ApplyPolicy()
    {
        try
        {
            var content = ClipboardActions.Read();
            if (content is null) return;
            EvaluateAndEnforce(content);
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("clipboard_monitor_apply_policy", ex.Message);
        }
    }

    void EvaluateAndEnforce(ClipboardContent content)
    {
        // Report what was copied/cut regardless of policy verdict - emitted BEFORE the verdict
        // event so "what happened" and "what we did about it" appear in that order on the wire.
        IEvent changeEvent = content.Type switch
        {
            ClipboardContentType.Text  => new ClipboardTextEvent(content.Operation, content.Text, EventEmitter.Ts()),
            ClipboardContentType.Files => new ClipboardFilesEvent(content.Operation, content.Files, EventEmitter.Ts()),
            ClipboardContentType.Image => new ClipboardImageEvent(EventEmitter.Ts()),
            _                          => new ClipboardUnknownEvent(EventEmitter.Ts())
        };
        EventEmitter.Emit(changeEvent);

        // Only Text/Files carry evaluable content - Image/Unknown always pass through untouched.
        ClipboardKind? kind = content.Type switch
        {
            ClipboardContentType.Text  => ClipboardKind.Text,
            ClipboardContentType.Files => ClipboardKind.Files,
            _                          => null
        };
        if (kind is null) return;

        IReadOnlyList<string> candidates = kind == ClipboardKind.Text
            ? (content.Text is null ? Array.Empty<string>() : new[] { content.Text })
            : (content.Files ?? Array.Empty<string>());

        var (violates, reason, matchedPattern) = EvaluatePolicy(_whitelist, _blacklist, kind.Value, candidates);
        if (!violates) return;

        if (ClipboardActions.Clear())
            EventEmitter.Emit(new ClipboardContentBlockedEvent(content.Operation, kind.Value, reason!, matchedPattern, changeEvent.EventId, EventEmitter.Ts()));
        else
            EventEmitter.Emit(new ClipboardContentBlockFailedEvent(content.Operation, kind.Value, "failed to clear clipboard", changeEvent.EventId, EventEmitter.Ts()));
    }

    /// <summary>
    /// The AND-formula: allowed = (whitelist disabled OR ANY candidate matches whitelist) AND
    /// (blacklist disabled OR NO candidate matches blacklist) - aggregated with .Any(), mirroring
    /// UsbMonitor.IsGroupCompliant's "any sibling matches" style exactly. Internal (not private)
    /// so KeyboardHook's live paste-time check reuses the exact same formula instead of a second,
    /// possibly-diverging copy of it.
    /// </summary>
    internal static (bool Violates, string? Reason, string? MatchedPattern) EvaluatePolicy(
        ClipboardWhitelist whitelist, ClipboardBlacklist blacklist, ClipboardKind kind, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return (false, null, null);

        // Blacklist: ANY candidate matching blocks the WHOLE operation.
        foreach (var candidate in candidates)
        {
            var matched = blacklist.FindMatchedPattern(kind, candidate);
            if (matched is not null) return (true, "blacklist_match", matched);
        }

        // Whitelist gate: when enabled, at least ONE candidate must satisfy it for the whole
        // operation to pass (IsAllowed already returns true unconditionally when disabled).
        if (!candidates.Any(c => whitelist.IsAllowed(kind, c)))
            return (true, "whitelist_gate", null);

        return (false, null, null);
    }

    public void Dispose() =>
        _window.ClipboardChanged -= OnClipboardChanged;
}
