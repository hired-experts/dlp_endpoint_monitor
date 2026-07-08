using DlpEndpointMonitor.Core;
using DlpEndpointMonitor.Monitors;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// ai_agent_doc/TEST-PLAN.md section 2.11: ClipboardMonitor.EvaluatePolicy is the single shared
/// AND-formula both ClipboardMonitor and KeyboardHook call - kept "internal static" specifically
/// so there is exactly one copy of this logic. Previously it was only exercised indirectly via
/// a hand-rolled re-implementation of the formula in ClipboardRuleListTests.cs
/// (whitelist.IsAllowed(...) && !blacklist.IsBlocked(...)), so a future divergence in the real
/// method's implementation (evaluation order, aggregation) would not have been caught. These
/// tests call the real method directly (accessible via the assembly-level InternalsVisibleTo in
/// AppJsonContext.cs) instead of re-deriving the formula.
/// </summary>
public class ClipboardMonitorPolicyTests
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

    static void WithLists(Action<ClipboardWhitelist, ClipboardBlacklist> test) =>
        WithTempDir(wDir => WithTempDir(bDir => test(new ClipboardWhitelist(wDir), new ClipboardBlacklist(bDir))));

    // ── T-CLIP-POLICY-01: explicit early-return for zero candidates ───────────────────────
    // Read from the real method body before writing this: candidates.Count == 0 short-circuits
    // to (false, null, null) before either list is even consulted.

    [Fact]
    public void EmptyCandidates_NeverViolates_RegardlessOfListState()
    {
        WithLists((whitelist, blacklist) =>
        {
            whitelist.Add(new ClipboardRuleEntry("anything"));
            whitelist.SetEnabled(true);
            blacklist.Add(new ClipboardRuleEntry(".*"));
            blacklist.SetEnabled(true);

            var textResult  = ClipboardMonitor.EvaluatePolicy(whitelist, blacklist, ClipboardKind.Text, Array.Empty<string>());
            var filesResult = ClipboardMonitor.EvaluatePolicy(whitelist, blacklist, ClipboardKind.Files, Array.Empty<string>());

            Assert.False(textResult.Violates);
            Assert.False(filesResult.Violates);
        });
    }

    // ── T-CLIP-POLICY-02: a single candidate matching BOTH lists at once - blacklist is
    // checked before the whitelist gate in the real method, so it must win. ────────────────

    [Fact]
    public void CandidateMatchingBothLists_BlacklistWins()
    {
        WithLists((whitelist, blacklist) =>
        {
            whitelist.Add(new ClipboardRuleEntry("^allowed-", ClipboardKind.Text));
            whitelist.SetEnabled(true);
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            var result = ClipboardMonitor.EvaluatePolicy(whitelist, blacklist, ClipboardKind.Text, new[] { "allowed-secret" });

            Assert.True(result.Violates);
            Assert.Equal("blacklist_match", result.Reason);
        });
    }

    // ── T-CLIP-POLICY-03: 3 Files candidates, only the 2nd matches the blacklist - whole
    // operation blocked, MatchedPattern is the actual pattern that matched. ────────────────

    [Fact]
    public void FilesCandidates_OnlySecondMatchesBlacklist_WholeOperationBlocked()
    {
        WithLists((whitelist, blacklist) =>
        {
            blacklist.Add(new ClipboardRuleEntry(@"\.secret$", ClipboardKind.Files));
            blacklist.SetEnabled(true);

            string[] candidates = [@"C:\docs\a.txt", @"C:\docs\b.secret", @"C:\docs\c.txt"];
            var result = ClipboardMonitor.EvaluatePolicy(whitelist, blacklist, ClipboardKind.Files, candidates);

            Assert.True(result.Violates);
            Assert.Equal("blacklist_match", result.Reason);
            Assert.Equal(@"\.secret$", result.MatchedPattern);
        });
    }

    // ── T-CLIP-POLICY-04: 3 Files candidates, only the 2nd matches the whitelist, blacklist
    // disabled - proves "ANY candidate satisfies whitelist", not "ALL must". ───────────────

    [Fact]
    public void FilesCandidates_OnlySecondMatchesWhitelist_WholeOperationAllowed()
    {
        WithLists((whitelist, blacklist) =>
        {
            whitelist.Add(new ClipboardRuleEntry(@"\.approved$", ClipboardKind.Files));
            whitelist.SetEnabled(true);

            string[] candidates = [@"C:\docs\a.txt", @"C:\docs\b.approved", @"C:\docs\c.txt"];
            var result = ClipboardMonitor.EvaluatePolicy(whitelist, blacklist, ClipboardKind.Files, candidates);

            Assert.False(result.Violates);
        });
    }

    // ── T-CLIP-POLICY-05: 3 Files candidates, whitelist enabled and NONE match - blocked
    // with the whitelist_gate reason and no single pattern responsible. ───────────────────

    [Fact]
    public void FilesCandidates_WhitelistEnabledNoneMatch_BlockedWithGateReason()
    {
        WithLists((whitelist, blacklist) =>
        {
            whitelist.Add(new ClipboardRuleEntry(@"\.approved$", ClipboardKind.Files));
            whitelist.SetEnabled(true);

            string[] candidates = [@"C:\docs\a.txt", @"C:\docs\b.txt", @"C:\docs\c.txt"];
            var result = ClipboardMonitor.EvaluatePolicy(whitelist, blacklist, ClipboardKind.Files, candidates);

            Assert.True(result.Violates);
            Assert.Equal("whitelist_gate", result.Reason);
            Assert.Null(result.MatchedPattern);
        });
    }

    // ── T-CLIP-POLICY-06: both lists disabled - never violates regardless of content. ─────

    [Fact]
    public void BothListsDisabled_NeverViolates()
    {
        WithLists((whitelist, blacklist) =>
        {
            var result = ClipboardMonitor.EvaluatePolicy(whitelist, blacklist, ClipboardKind.Text, new[] { "anything at all, even secret" });

            Assert.False(result.Violates);
        });
    }

    // ── T-CLIP-POLICY-07: Kind isolation at the EvaluatePolicy level ───────────────────────
    // ClipboardRuleList's own Kind-scoping is already covered at the Core level (T-CLIP-LIST-09/
    // 10) - this proves the same isolation holds through EvaluatePolicy's own candidate-loop/
    // kind-forwarding, not just FindMatch's internal filter, so this is the EvaluatePolicy-level
    // version rather than a duplicate of the Core-level test.

    [Fact]
    public void KindIsolation_TextScopedRuleDoesNotAffectFilesEvaluation_AndViceVersa()
    {
        WithLists((whitelist, blacklist) =>
        {
            blacklist.Add(new ClipboardRuleEntry("secret", ClipboardKind.Text));
            blacklist.SetEnabled(true);

            var filesResult = ClipboardMonitor.EvaluatePolicy(whitelist, blacklist, ClipboardKind.Files, new[] { "this is secret" });
            Assert.False(filesResult.Violates);

            var textResult = ClipboardMonitor.EvaluatePolicy(whitelist, blacklist, ClipboardKind.Text, new[] { "this is secret" });
            Assert.True(textResult.Violates);
        });
    }
}
