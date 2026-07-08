using System.Text.Json;
using System.Text.RegularExpressions;

namespace DlpEndpointMonitor.Core;

public record ClipboardRuleEntry(string Pattern, ClipboardKind? Kind = null, string? Label = null);

internal sealed class ClipboardRuleListState
{
    public bool                     Enabled { get; set; } = false;
    public List<ClipboardRuleEntry> Entries { get; set; } = [];
}

// Pairs a persisted entry with its compiled Regex so a hot path (the keyboard hook's
// per-keystroke paste check) never re-parses a pattern string. Regex is null when the
// pattern failed to compile - the entry is kept (so Get/GetAll still reports it) but never
// matches anything.
sealed record CompiledClipboardRule(ClipboardRuleEntry Entry, Regex? Regex);

/// <summary>
/// Thread-safe, persistent list of clipboard content rules (regex patterns, optionally scoped
/// to a ClipboardKind). Structurally separate from <see cref="UsbDeviceList"/> - a different
/// entry shape and different matching semantics - but mirrors its ReaderWriterLockSlim +
/// atomic-temp-file-write persistence shape. Subclasses add their own allow/block decision.
/// </summary>
abstract class ClipboardRuleList
{
    // Clipboard content is evaluated synchronously inside the low-level keyboard hook on every
    // Ctrl+V - an operator-supplied catastrophic-backtracking pattern must not be able to stall
    // every keystroke on the machine, so every Regex gets a hard timeout.
    static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    readonly ReaderWriterLockSlim  _lock        = new();
    readonly string                _storageDir;
    readonly string                _storagePath;
    ClipboardRuleListState         _state       = new();
    List<CompiledClipboardRule>    _compiled    = [];

    /// <param name="storageDir">
    /// Directory to persist under. Defaults to <see cref="StorageLocation.Default"/>
    /// (%ProgramData%\DlpEndpointMonitor) when null - pass an explicit directory only to
    /// isolate storage, e.g. in tests.
    /// </param>
    protected ClipboardRuleList(string fileName, string? storageDir = null)
    {
        _storageDir  = storageDir ?? StorageLocation.Default;
        _storagePath = Path.Combine(_storageDir, fileName);
        Load();
    }

    // ── Enabled flag ──────────────────────────────────────────────────────────

    public bool IsEnabled
    {
        get
        {
            _lock.EnterReadLock();
            try   { return _state.Enabled; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void SetEnabled(bool enabled)
    {
        _lock.EnterWriteLock();
        try   { _state.Enabled = enabled; }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    // ── Matching ──────────────────────────────────────────────────────────────

    /// <summary>True if ANY entry (Kind is null OR Kind == kind) matches candidate.</summary>
    protected bool MatchesAny(ClipboardKind kind, string candidate) =>
        FindMatch(kind, candidate) is not null;

    /// <summary>Returns the Pattern of the first matching entry (Kind is null OR Kind == kind), or null.</summary>
    protected string? FindMatch(ClipboardKind kind, string candidate)
    {
        List<CompiledClipboardRule> snapshot;
        _lock.EnterReadLock();
        try   { snapshot = _compiled; } // reference to an immutable, wholesale-replaced list
        finally { _lock.ExitReadLock(); }

        foreach (var rule in snapshot)
        {
            if (rule.Regex is null) continue; // malformed pattern - treated as never-matching
            if (rule.Entry.Kind is not null && rule.Entry.Kind != kind) continue;

            try
            {
                if (rule.Regex.IsMatch(candidate)) return rule.Entry.Pattern;
            }
            catch (RegexMatchTimeoutException)
            {
                EventEmitter.EmitError($"{GetType().Name}_match_timeout", $"pattern timed out against live content: {rule.Entry.Pattern}");
            }
        }
        return null;
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    // Two entries are the SAME RULE when Pattern (ordinal - regex patterns are literal strings,
    // not case-insensitive text) and Kind both match. Label is cosmetic and intentionally ignored.
    static bool SameRule(ClipboardRuleEntry a, ClipboardRuleEntry b) =>
        string.Equals(a.Pattern, b.Pattern, StringComparison.Ordinal) && a.Kind == b.Kind;

    public void Add(ClipboardRuleEntry rule)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_state.Entries.Any(e => SameRule(e, rule))) _state.Entries.Add(rule);
            RebuildCompiled();
        }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    public void Remove(string pattern, ClipboardKind? kind = null)
    {
        _lock.EnterWriteLock();
        try
        {
            _state.Entries.RemoveAll(e =>
                string.Equals(e.Pattern, pattern, StringComparison.Ordinal) &&
                (kind is null || e.Kind == kind));
            RebuildCompiled();
        }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    /// <summary>Replace the entire list atomically, dropping duplicate rules.</summary>
    public void Set(IEnumerable<ClipboardRuleEntry> rules)
    {
        _lock.EnterWriteLock();
        try
        {
            _state.Entries.Clear();
            foreach (var r in rules)
                if (!_state.Entries.Any(e => SameRule(e, r))) _state.Entries.Add(r);
            RebuildCompiled();
        }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    public void Clear()
    {
        _lock.EnterWriteLock();
        try   { _state.Entries.Clear(); RebuildCompiled(); }
        finally { _lock.ExitWriteLock(); }
        Save();
    }

    public IReadOnlyList<ClipboardRuleEntry> GetAll()
    {
        _lock.EnterReadLock();
        try   { return _state.Entries.ToArray(); }
        finally { _lock.ExitReadLock(); }
    }

    // Recompiles every entry's Regex in one pass and publishes a brand-new list wholesale, so
    // readers taking the read lock only ever see a fully-built, immutable snapshot - never a
    // partially-rebuilt cache. Caller must hold the write lock.
    void RebuildCompiled()
    {
        var compiled = new List<CompiledClipboardRule>(_state.Entries.Count);
        foreach (var entry in _state.Entries)
        {
            Regex? regex = null;
            try
            {
                regex = new Regex(entry.Pattern, RegexOptions.None, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                // A malformed pattern must not crash Add/Load - keep the entry (so Get still
                // reports it) but it never matches anything.
                EventEmitter.EmitError($"{GetType().Name}_bad_pattern", $"'{entry.Pattern}': {ex.Message}");
            }
            compiled.Add(new CompiledClipboardRule(entry, regex));
        }
        _compiled = compiled;
    }

    // ── Disk I/O ──────────────────────────────────────────────────────────────

    void Save()
    {
        try
        {
            Directory.CreateDirectory(_storageDir);

            ClipboardRuleListState snapshot;
            _lock.EnterReadLock();
            try
            {
                snapshot = new ClipboardRuleListState
                {
                    Enabled = _state.Enabled,
                    Entries = _state.Entries.ToList(),
                };
            }
            finally { _lock.ExitReadLock(); }

            string tmp = _storagePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, AppJsonContext.Default.ClipboardRuleListState));
            File.Move(tmp, _storagePath, overwrite: true);
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError($"{GetType().Name}_save", ex.Message);
        }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;

            string json = File.ReadAllText(_storagePath);
            var loaded  = JsonSerializer.Deserialize(json, AppJsonContext.Default.ClipboardRuleListState);
            if (loaded is null) return;

            _lock.EnterWriteLock();
            try   { _state = loaded; RebuildCompiled(); }
            finally { _lock.ExitWriteLock(); }

            EventEmitter.EmitInfo(
                $"{GetType().Name} loaded — enabled={_state.Enabled}, {_state.Entries.Count} rule(s)");
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError($"{GetType().Name}_load", $"{ex.Message} — starting empty");
        }
    }
}
