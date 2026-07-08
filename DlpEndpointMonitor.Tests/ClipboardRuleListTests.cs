using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// ai_agent_doc/TEST-PLAN.md section 2 (pure in-memory logic, no Win32 calls): ClipboardWhitelist/
/// ClipboardBlacklist matching, dedup, and the AND-combination formula the monitors use, via
/// the storage-dir constructor seam so every test uses its own throwaway directory instead of
/// the real %ProgramData%\DlpEndpointMonitor\.
/// </summary>
public class ClipboardRuleListTests
{
    // Never construct ClipboardWhitelist()/ClipboardBlacklist() with no arguments in a test -
    // that reads/writes the real %ProgramData%\DlpEndpointMonitor\ files. Always pass a
    // fresh temp directory.
    static void WithTempDir(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try   { test(dir); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // ── Disabled list is fail-open (whitelist) / fail-closed-off (blacklist) ──────────────

    [Fact]
    public void Whitelist_Disabled_AlwaysAllowsAnyContent()
    {
        WithTempDir(dir =>
        {
            var whitelist = new ClipboardWhitelist(dir);

            Assert.False(whitelist.IsEnabled);
            Assert.True(whitelist.IsAllowed(ClipboardKind.Text, "anything at all"));
            Assert.True(whitelist.IsAllowed(ClipboardKind.Files, @"C:\secret\path.txt"));
        });
    }

    [Fact]
    public void Blacklist_Disabled_NeverBlocksAnyContent()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);

            Assert.False(blacklist.IsEnabled);
            Assert.False(blacklist.IsBlocked(ClipboardKind.Text, "ssn 123-45-6789"));
            Assert.False(blacklist.IsBlocked(ClipboardKind.Files, @"C:\secret\path.txt"));
        });
    }

    // ── Enabled + empty list denies everything (whitelist), blocks nothing (blacklist) ────

    [Fact]
    public void Whitelist_EnabledEmpty_DeniesEverything()
    {
        WithTempDir(dir =>
        {
            var whitelist = new ClipboardWhitelist(dir);
            whitelist.SetEnabled(true);

            Assert.False(whitelist.IsAllowed(ClipboardKind.Text, "anything"));
        });
    }

    [Fact]
    public void Blacklist_EnabledEmpty_BlocksNothing()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.SetEnabled(true);

            Assert.False(blacklist.IsBlocked(ClipboardKind.Text, "anything"));
        });
    }

    // ── Pattern matching against Text ──────────────────────────────────────────────────────

    [Fact]
    public void Blacklist_TextPattern_BlocksMatchingText()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry(@"\d{3}-\d{2}-\d{4}", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            Assert.True(blacklist.IsBlocked(ClipboardKind.Text, "my ssn is 123-45-6789"));
            Assert.False(blacklist.IsBlocked(ClipboardKind.Text, "no sensitive content here"));
        });
    }

    [Fact]
    public void Whitelist_TextPattern_AllowsOnlyMatchingText()
    {
        WithTempDir(dir =>
        {
            var whitelist = new ClipboardWhitelist(dir);
            whitelist.Add(new ClipboardRuleEntry("^allowed-", ClipboardKind.Text));
            whitelist.SetEnabled(true);

            Assert.True(whitelist.IsAllowed(ClipboardKind.Text, "allowed-value"));
            Assert.False(whitelist.IsAllowed(ClipboardKind.Text, "disallowed-value"));
        });
    }

    // ── Pattern matching against Files: any-path-matches semantics ────────────────────────

    [Fact]
    public void Blacklist_FilesPattern_BlocksWhenAnySinglePathMatches()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry(@"\.secret$", ClipboardKind.Files));
            blacklist.SetEnabled(true);

            string[] paths = [@"C:\docs\readme.txt", @"C:\docs\keys.secret"];

            // Mirrors UsbMonitor.IsGroupCompliant's .Any() aggregation: one matching path is
            // enough to condemn the whole clipboard operation.
            bool anyBlocked = paths.Any(p => blacklist.IsBlocked(ClipboardKind.Files, p));
            Assert.True(anyBlocked);
        });
    }

    [Fact]
    public void Whitelist_FilesPattern_SatisfiedWhenAnySinglePathMatches()
    {
        WithTempDir(dir =>
        {
            var whitelist = new ClipboardWhitelist(dir);
            whitelist.Add(new ClipboardRuleEntry(@"\.approved$", ClipboardKind.Files));
            whitelist.SetEnabled(true);

            string[] paths = [@"C:\docs\readme.txt", @"C:\docs\report.approved"];

            bool anyAllowed = paths.Any(p => whitelist.IsAllowed(ClipboardKind.Files, p));
            Assert.True(anyAllowed);

            string[] noneMatch = [@"C:\docs\readme.txt", @"C:\docs\other.txt"];
            bool noneAllowed = noneMatch.Any(p => whitelist.IsAllowed(ClipboardKind.Files, p));
            Assert.False(noneAllowed);
        });
    }

    // ── Kind-scoped vs Kind-null (any-kind) entries ────────────────────────────────────────

    [Fact]
    public void Blacklist_KindScopedEntry_MatchesOnlyThatKind()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            Assert.True(blacklist.IsBlocked(ClipboardKind.Text, "this is secret"));
            Assert.False(blacklist.IsBlocked(ClipboardKind.Files, @"C:\secret\file.txt"));
        });
    }

    [Fact]
    public void Blacklist_KindNullEntry_MatchesAnyKind()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("secret", Kind: null));
            blacklist.SetEnabled(true);

            Assert.True(blacklist.IsBlocked(ClipboardKind.Text, "this is secret"));
            Assert.True(blacklist.IsBlocked(ClipboardKind.Files, @"C:\secret\file.txt"));
        });
    }

    // ── Dedup on Add/Set (same Pattern+Kind) ───────────────────────────────────────────────

    [Fact]
    public void Add_SameRuleTwiceWithDifferentLabel_IsNoOpOnSecondAdd()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Text, Label: "First"));
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Text, Label: "Second"));

            var all = blacklist.GetAll();
            Assert.Single(all);
            Assert.Equal("First", all[0].Label);
        });
    }

    [Fact]
    public void Add_SamePatternDifferentKind_BothPersist()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Text));
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Files));

            Assert.Equal(2, blacklist.GetAll().Count);
        });
    }

    [Fact]
    public void Set_WithDuplicateContainingInput_CollapsesToOne()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            var a1 = new ClipboardRuleEntry("foo", ClipboardKind.Text, Label: "A1");
            var a2 = new ClipboardRuleEntry("foo", ClipboardKind.Text, Label: "A2");
            var b  = new ClipboardRuleEntry("bar", ClipboardKind.Files);

            blacklist.Set([a1, a2, b]);

            var all = blacklist.GetAll();
            Assert.Equal(2, all.Count);
            Assert.Contains(all, e => e.Pattern == "foo" && e.Label == "A1");
            Assert.Contains(all, e => e.Pattern == "bar");
        });
    }

    // ── Remove with partial filters ─────────────────────────────────────────────────────────

    [Fact]
    public void Remove_WithKindFilter_RemovesOnlyMatchingPatternAndKind()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("foo", ClipboardKind.Text));
            blacklist.Add(new ClipboardRuleEntry("foo", ClipboardKind.Files));
            blacklist.Add(new ClipboardRuleEntry("bar", ClipboardKind.Text));

            blacklist.Remove("foo", ClipboardKind.Text);

            var all = blacklist.GetAll();
            Assert.Equal(2, all.Count);
            Assert.DoesNotContain(all, e => e.Pattern == "foo" && e.Kind == ClipboardKind.Text);
            Assert.Contains(all, e => e.Pattern == "foo" && e.Kind == ClipboardKind.Files);
        });
    }

    [Fact]
    public void Remove_WithNoKindFilter_RemovesEveryEntryMatchingPatternRegardlessOfKind()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("foo", ClipboardKind.Text));
            blacklist.Add(new ClipboardRuleEntry("foo", ClipboardKind.Files));
            blacklist.Add(new ClipboardRuleEntry("bar", ClipboardKind.Text));

            blacklist.Remove("foo");

            var all = blacklist.GetAll();
            Assert.Single(all);
            Assert.Equal("bar", all[0].Pattern);
        });
    }

    // ── Clear ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_ThenGetAll_IsEmpty()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("foo"));
            blacklist.Add(new ClipboardRuleEntry("bar", ClipboardKind.Files));

            blacklist.Clear();

            Assert.Empty(blacklist.GetAll());
        });
    }

    // ── Malformed regex is skipped, not a crash ─────────────────────────────────────────────

    [Fact]
    public void Add_MalformedPattern_DoesNotThrowAndNeverMatches()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);

            // Unbalanced group - fails Regex construction. Must not throw out of Add, must
            // still be reported by GetAll (kept, just inert), and must never match anything.
            var ex = Record.Exception(() => blacklist.Add(new ClipboardRuleEntry("(unclosed", ClipboardKind.Text)));
            Assert.Null(ex);

            blacklist.SetEnabled(true);

            Assert.Single(blacklist.GetAll());
            Assert.False(blacklist.IsBlocked(ClipboardKind.Text, "(unclosed"));
            Assert.False(blacklist.IsBlocked(ClipboardKind.Text, "anything"));
        });
    }

    [Fact]
    public void Load_PersistedMalformedPattern_DoesNotThrowOnReload()
    {
        WithTempDir(dir =>
        {
            var first = new ClipboardBlacklist(dir);
            first.Add(new ClipboardRuleEntry("(unclosed", ClipboardKind.Text));
            first.Add(new ClipboardRuleEntry("valid", ClipboardKind.Text));
            first.SetEnabled(true);

            // Reload from disk - the malformed pattern must survive persistence without
            // crashing the constructor/Load path.
            var reloaded = new ClipboardBlacklist(dir);

            Assert.Equal(2, reloaded.GetAll().Count);
            Assert.True(reloaded.IsBlocked(ClipboardKind.Text, "this is valid content"));
            Assert.False(reloaded.IsBlocked(ClipboardKind.Text, "(unclosed"));
        });
    }

    // ── Independent enable/disable of whitelist vs blacklist; AND-combination formula ─────

    [Fact]
    public void WhitelistAndBlacklist_CanBothBeEnabledIndependently()
    {
        WithTempDir(wDir => WithTempDir(bDir =>
        {
            var whitelist = new ClipboardWhitelist(wDir);
            var blacklist = new ClipboardBlacklist(bDir);

            whitelist.SetEnabled(true);
            blacklist.SetEnabled(true);

            // Both on at once is a valid, intended state for clipboard policy (unlike device
            // policy) - no conflict, no forced mutual exclusion.
            Assert.True(whitelist.IsEnabled);
            Assert.True(blacklist.IsEnabled);
        }));
    }

    [Theory]
    // allowed = (whitelist disabled OR content matches a whitelist pattern)
    //           AND (blacklist disabled OR content does NOT match a blacklist pattern)
    [InlineData("neither matches", false)]     // whitelist gate fails (nothing matches) -> blocked
    [InlineData("allowed-and-clean", true)]    // matches whitelist, does not match blacklist -> allowed
    [InlineData("allowed-and-secret", false)]  // matches whitelist AND blacklist -> blocked
    public void AndFormula_CombinesWhitelistGateAndBlacklistMatch(string content, bool expectedAllowed)
    {
        WithTempDir(wDir => WithTempDir(bDir =>
        {
            var whitelist = new ClipboardWhitelist(wDir);
            var blacklist = new ClipboardBlacklist(bDir);

            whitelist.Add(new ClipboardRuleEntry("^allowed-", ClipboardKind.Text));
            whitelist.SetEnabled(true);
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            bool allowed = whitelist.IsAllowed(ClipboardKind.Text, content)
                        && !blacklist.IsBlocked(ClipboardKind.Text, content);

            Assert.Equal(expectedAllowed, allowed);
        }));
    }

    [Fact]
    public void AndFormula_OnlyBlacklistEnabled_BlockedContentBlockedCleanContentAllowed()
    {
        WithTempDir(wDir => WithTempDir(bDir =>
        {
            var whitelist = new ClipboardWhitelist(wDir); // disabled -> always true
            var blacklist = new ClipboardBlacklist(bDir);
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            bool AllowedFor(string content) =>
                whitelist.IsAllowed(ClipboardKind.Text, content) && !blacklist.IsBlocked(ClipboardKind.Text, content);

            Assert.False(AllowedFor("this is secret"));
            Assert.True(AllowedFor("this is fine"));
        }));
    }

    [Fact]
    public void AndFormula_OnlyWhitelistEnabled_NonMatchingBlockedMatchingAllowed()
    {
        WithTempDir(wDir => WithTempDir(bDir =>
        {
            var whitelist = new ClipboardWhitelist(wDir);
            whitelist.Add(new ClipboardRuleEntry("^allowed-", ClipboardKind.Text));
            whitelist.SetEnabled(true);
            var blacklist = new ClipboardBlacklist(bDir); // disabled -> never blocks

            bool AllowedFor(string content) =>
                whitelist.IsAllowed(ClipboardKind.Text, content) && !blacklist.IsBlocked(ClipboardKind.Text, content);

            Assert.True(AllowedFor("allowed-value"));
            Assert.False(AllowedFor("disallowed-value"));
        }));
    }

    // ── FindMatchedPattern reports the specific matching pattern for blocked events ────────

    [Fact]
    public void Blacklist_FindMatchedPattern_ReturnsMatchingPatternOrNull()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry(@"\d{3}-\d{2}-\d{4}", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            Assert.Equal(@"\d{3}-\d{2}-\d{4}", blacklist.FindMatchedPattern(ClipboardKind.Text, "ssn 123-45-6789"));
            Assert.Null(blacklist.FindMatchedPattern(ClipboardKind.Text, "clean content"));
        });
    }

    [Fact]
    public void Blacklist_FindMatchedPattern_NullWhenDisabled()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Text));
            // Not enabled.

            Assert.Null(blacklist.FindMatchedPattern(ClipboardKind.Text, "this is secret"));
        });
    }

    // ── Adversarial edge cases (ai_agent_doc/TEST-PLAN.md section 2.9.1) ──────────────────────────

    // T-CLIP-EDGE-01: FindMatch runs synchronously inside the live keyboard hook on every
    // Ctrl+V - an operator-supplied catastrophic-backtracking pattern must not be able to hang
    // that thread. This proves RegexTimeout (250ms, ClipboardRuleList) actually fires at
    // runtime and is swallowed as a non-match, not just that malformed *construction* is caught
    // (that is the separate, already-covered Add_MalformedPattern_* case above).
    [Fact]
    public void Blacklist_CatastrophicBacktrackingPattern_TimesOutInsteadOfHanging()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("^(a+)+$", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            // 40 a's with no terminating match forces exponential backtracking under .NET's
            // default backtracking regex engine.
            string evilInput = new string('a', 40) + "!";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool blocked = blacklist.IsBlocked(ClipboardKind.Text, evilInput);
            sw.Stop();

            Assert.False(blocked); // a timed-out match is treated as never-matched, not a crash
            // Generously above the 250ms RegexTimeout (avoid CI flakiness) but tight enough to
            // prove the timeout actually fired rather than the call coincidentally returning fast.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
                $"expected the 250ms RegexTimeout to bound this call, took {sw.Elapsed}");
        });
    }

    // T-CLIP-EDGE-02: mirrors the real production race between the stdin command thread
    // mutating the list (Add) and the message-pump thread's keyboard hook reading it live
    // (IsBlocked) - ReaderWriterLockSlim must hold under concurrent writers+readers with no
    // exception and a deterministic final count (every writer's Add must be reflected exactly
    // once, none lost, none duplicated).
    [Fact]
    public void Blacklist_ConcurrentAddAndRead_NoExceptionAndDeterministicFinalCount()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.SetEnabled(true);
            blacklist.Add(new ClipboardRuleEntry("seed", ClipboardKind.Text));

            const int writerCount = 20;
            var writers = Enumerable.Range(0, writerCount)
                .Select(i => Task.Run(() => blacklist.Add(new ClipboardRuleEntry($"pattern-{i}", ClipboardKind.Text))));
            var readers = Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() =>
                {
                    for (int j = 0; j < 50; j++)
                        blacklist.IsBlocked(ClipboardKind.Text, "pattern-5 in some content");
                }));

            var ex = Record.Exception(() => Task.WaitAll(writers.Concat(readers).ToArray()));
            Assert.Null(ex);

            // Seed + one distinct pattern per writer - deterministic regardless of interleaving.
            Assert.Equal(writerCount + 1, blacklist.GetAll().Count);
        });
    }

    // T-CLIP-EDGE-03: RebuildCompiled always constructs with RegexOptions.None - proves no
    // implicit case-insensitivity is silently applied anywhere in the matching pipeline; an
    // operator relying on case sensitivity to scope a rule narrowly must be able to trust it.
    [Fact]
    public void Blacklist_PatternMatching_IsCaseSensitiveByDefault()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("Secret", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            Assert.True(blacklist.IsBlocked(ClipboardKind.Text, "this is Secret"));
            Assert.False(blacklist.IsBlocked(ClipboardKind.Text, "this is secret"));
        });
    }

    // T-CLIP-EDGE-04: an empty-string pattern is a technically-valid regex that matches every
    // candidate - an operator could enter this by mistake (e.g. submitting an empty rule field);
    // confirm it behaves predictably (matches everything) instead of being silently rejected or
    // throwing during compilation.
    [Fact]
    public void Blacklist_EmptyStringPattern_MatchesEveryCandidate()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            Assert.True(blacklist.IsBlocked(ClipboardKind.Text, "anything at all"));
            Assert.True(blacklist.IsBlocked(ClipboardKind.Text, ""));
        });
    }

    // T-CLIP-EDGE-05: clipboard content is real-world, unrestricted-charset text/file-path
    // data - a Cyrillic or CJK pattern/candidate must match exactly like ASCII, proving no
    // accidental ASCII-only assumption anywhere in the matching pipeline.
    [Fact]
    public void Blacklist_UnicodePatternAndContent_MatchesCorrectly()
    {
        WithTempDir(dir =>
        {
            var blacklist = new ClipboardBlacklist(dir);
            blacklist.Add(new ClipboardRuleEntry("секрет", ClipboardKind.Text));  // Cyrillic "secret"
            blacklist.Add(new ClipboardRuleEntry("机密", ClipboardKind.Files));    // CJK "confidential"
            blacklist.SetEnabled(true);

            Assert.True(blacklist.IsBlocked(ClipboardKind.Text, "это секрет, не говори никому"));
            Assert.False(blacklist.IsBlocked(ClipboardKind.Text, "this is fine"));
            Assert.True(blacklist.IsBlocked(ClipboardKind.Files, @"C:\文件\机密.txt"));
        });
    }
}
