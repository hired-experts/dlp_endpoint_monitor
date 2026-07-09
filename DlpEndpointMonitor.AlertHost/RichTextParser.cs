using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Documents;

namespace DlpEndpointMonitor.AlertHost;

/// <summary>
/// Parses AlertRequest.Message's small CLOSED allowlist of inline tags - &lt;strong&gt;/&lt;b&gt;
/// (bold), &lt;em&gt;/&lt;i&gt; (italic), &lt;br&gt; (line break) - into WPF Inline objects for a
/// TextBlock. This is deliberately NOT a general HTML parser: no XML/HTML parsing library, no
/// WPF WebBrowser control (CRITERIA). Any tag outside the allowlist, or malformed/unbalanced
/// markup, is stripped from the output and the surrounding text still renders as plain text -
/// this parser must NEVER throw, since a bad message string coming off the wire must not take
/// down the alert window with it.
/// </summary>
public static class RichTextParser
{
    // Matches <tag>, </tag>, <tag/> for a bare alphabetic tag name, case-insensitive. Anything
    // that doesn't close with '>' (a truncated/malformed tag) simply never matches and falls
    // through as plain text - that is the fail-safe behavior, not a bug to fix here.
    static readonly Regex TagPattern = new(@"<\s*(?<slash>/)?\s*(?<name>[a-zA-Z]+)\s*/?\s*>", RegexOptions.Compiled);

    static readonly HashSet<string> BoldTags = new(StringComparer.OrdinalIgnoreCase) { "strong", "b" };
    static readonly HashSet<string> ItalicTags = new(StringComparer.OrdinalIgnoreCase) { "em", "i" };

    /// <summary>Clears and repopulates a TextBlock's Inlines from a raw AlertRequest.Message.</summary>
    public static void Apply(TextBlock target, string? message)
    {
        target.Inlines.Clear();
        foreach (Inline inline in Parse(message))
            target.Inlines.Add(inline);
    }

    public static IReadOnlyList<Inline> Parse(string? message)
    {
        var result = new List<Inline>();
        if (string.IsNullOrEmpty(message))
            return result;

        try
        {
            bool bold = false;
            bool italic = false;
            int cursor = 0;

            foreach (Match match in TagPattern.Matches(message))
            {
                if (match.Index > cursor)
                    AppendRun(result, message[cursor..match.Index], bold, italic);

                string name = match.Groups["name"].Value;
                bool isClose = match.Groups["slash"].Success;

                if (string.Equals(name, "br", StringComparison.OrdinalIgnoreCase))
                    result.Add(new LineBreak());
                else if (BoldTags.Contains(name))
                    bold = !isClose;
                else if (ItalicTags.Contains(name))
                    italic = !isClose;
                // else: an unrecognized tag - stripped from the output, contributes nothing.

                cursor = match.Index + match.Length;
            }

            if (cursor < message.Length)
                AppendRun(result, message[cursor..], bold, italic);
        }
        catch (Exception)
        {
            // Defense in depth: even if something above misbehaves on a pathological input,
            // fall back to the raw message as one plain-text run rather than propagate.
            result.Clear();
            result.Add(new Run(message));
        }

        return result;
    }

    static void AppendRun(List<Inline> result, string text, bool bold, bool italic)
    {
        if (text.Length == 0) return;
        var run = new Run(text);
        if (bold) run.FontWeight = System.Windows.FontWeights.Bold;
        if (italic) run.FontStyle = System.Windows.FontStyles.Italic;
        result.Add(run);
    }
}
