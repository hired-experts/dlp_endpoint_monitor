using System.Windows;
using System.Windows.Documents;
using Xunit;

namespace DlpEndpointMonitor.AlertHost.Tests;

// Covers RichTextParser's closed tag allowlist and its fail-safe behavior on malformed/unknown
// input - the only pure-logic piece of the rich-text pipeline (rendering itself is UI, out of
// scope per CRITERIA/AGENTS.md section 8.1's testability split).
public class RichTextParserTests
{
    // T-RTP-01/02: <strong> and <b> both produce a single bold Run.
    [Theory]
    [InlineData("<strong>bold</strong>")]
    [InlineData("<b>bold</b>")]
    public void Parse_BoldTagVariants_ProduceBoldRun(string message)
    {
        IReadOnlyList<Inline> inlines = RichTextParser.Parse(message);

        Run run = Assert.IsType<Run>(Assert.Single(inlines));
        Assert.Equal("bold", run.Text);
        Assert.Equal(FontWeights.Bold, run.FontWeight);
    }

    // T-RTP-03/04: <em> and <i> both produce a single italic Run.
    [Theory]
    [InlineData("<em>slanted</em>")]
    [InlineData("<i>slanted</i>")]
    public void Parse_ItalicTagVariants_ProduceItalicRun(string message)
    {
        IReadOnlyList<Inline> inlines = RichTextParser.Parse(message);

        Run run = Assert.IsType<Run>(Assert.Single(inlines));
        Assert.Equal("slanted", run.Text);
        Assert.Equal(FontStyles.Italic, run.FontStyle);
    }

    // T-RTP-05: <br> becomes a LineBreak, splitting the surrounding plain-text runs.
    [Fact]
    public void Parse_BrTag_ProducesLineBreakBetweenPlainRuns()
    {
        IReadOnlyList<Inline> inlines = RichTextParser.Parse("line1<br>line2");

        Assert.Equal(3, inlines.Count);
        Assert.Equal("line1", Assert.IsType<Run>(inlines[0]).Text);
        Assert.IsType<LineBreak>(inlines[1]);
        Assert.Equal("line2", Assert.IsType<Run>(inlines[2]).Text);
    }

    // T-RTP-06: tag matching is case-insensitive, per CRITERIA ("<STRONG> same as <strong>").
    [Fact]
    public void Parse_UppercaseTag_StillAppliesFormatting()
    {
        IReadOnlyList<Inline> inlines = RichTextParser.Parse("<STRONG>bold</STRONG>");

        Run run = Assert.IsType<Run>(Assert.Single(inlines));
        Assert.Equal("bold", run.Text);
        Assert.Equal(FontWeights.Bold, run.FontWeight);
    }

    // T-RTP-07: a tag outside the closed allowlist is stripped, never throws, and the
    // surrounding text still renders as plain text (fail-safe requirement from CRITERIA).
    [Fact]
    public void Parse_UnrecognizedTag_IsStrippedAndSurroundingTextStillRenders()
    {
        IReadOnlyList<Inline>? inlines = null;
        Exception? ex = Record.Exception(() => inlines = RichTextParser.Parse("<script>alert('x')</script>plain"));

        Assert.Null(ex);
        Assert.NotNull(inlines);
        string combined = string.Concat(inlines!.OfType<Run>().Select(r => r.Text));
        Assert.Equal("alert('x')plain", combined);
        Assert.All(inlines!, i => Assert.False(i is Run r && r.FontWeight == FontWeights.Bold));
    }

    // T-RTP-08: an unbalanced/unclosed tag never throws - the parser has no notion of "invalid",
    // it just applies formatting state through to the end of the string.
    [Fact]
    public void Parse_UnclosedTag_NeverThrows()
    {
        Exception? ex = Record.Exception(() => RichTextParser.Parse("<strong>bold with no closing tag"));

        Assert.Null(ex);
    }

    // T-RTP-09: null/empty input returns an empty, non-null inline list rather than throwing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_NullOrEmpty_ReturnsEmptyList(string? message)
    {
        IReadOnlyList<Inline> inlines = RichTextParser.Parse(message);

        Assert.Empty(inlines);
    }

    // RichTextParser.Apply itself (populating a live TextBlock) is deliberately not covered
    // here: constructing a TextBlock (a FrameworkElement) requires an STA thread, which is a
    // WPF/UI-hosting concern out of scope for this pure-logic suite - Parse(), which Apply
    // thinly wraps, is fully covered above (CRITERIA: "RichTextParser's tag conversion").
}
