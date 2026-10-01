using System.Net;
using System.Text.RegularExpressions;

namespace FinSight.Infrastructure.Gmail;

/// <summary>
/// Turns an email body into readable plain text: drops scripts and styles, keeps line breaks at block boundaries, and
/// decodes HTML entities. Good enough to pull the item lines out of an order confirmation — not a full HTML renderer.
/// </summary>
internal static partial class HtmlToText
{
    public static string Decode(string body, bool alreadyText)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        if (alreadyText)
        {
            return Collapse(body);
        }

        var text = ScriptOrStyle().Replace(body, " ");
        text = BlockBreak().Replace(text, "\n");
        text = Tag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return Collapse(text);
    }

    private static string Collapse(string text)
    {
        text = InlineSpace().Replace(text, " ");
        text = BlankLines().Replace(text, "\n");
        return text.Trim();
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    // Tags that end a visual line, so items don't run together into one blob.
    [GeneratedRegex(@"</(p|div|tr|li|h[1-6]|table)>|<br\s*/?>|</td>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex InlineSpace();

    [GeneratedRegex(@"(?:\s*\n){2,}")]
    private static partial Regex BlankLines();
}
