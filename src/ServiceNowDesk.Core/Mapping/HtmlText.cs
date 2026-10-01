using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ServiceNowDesk.Mapping;

public static partial class HtmlText
{
    public static string ToReadable(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var text = CommentPattern().Replace(html, " ");
        text = ScriptPattern().Replace(text, " ");
        text = UnclosedScriptPattern().Replace(text, " ");
        text = StylePattern().Replace(text, " ");
        text = UnclosedStylePattern().Replace(text, " ");
        text = ListItemPattern().Replace(text, "\n• ");
        text = BreakPattern().Replace(text, "\n");
        text = BlockEndPattern().Replace(text, "\n");
        text = TagPattern().Replace(text, "");
        return Collapse(Decode(text));
    }

    private static string Decode(string value)
    {
        var decoded = value
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("&apos;", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("&#39;", "'", StringComparison.Ordinal);
        decoded = NumericEntityPattern().Replace(decoded, match =>
        {
            var token = match.Groups[1].Value;
            var hex = token.StartsWith('x') || token.StartsWith('X');
            var digits = hex ? token[1..] : token;
            var style = hex ? NumberStyles.HexNumber : NumberStyles.Integer;
            if (digits.Length == 0 || !int.TryParse(digits, style, CultureInfo.InvariantCulture, out var code))
                return match.Value;
            if (code is < 0 or > 0x10FFFF || code is < 32 and not (9 or 10 or 13))
                return "";
            return char.ConvertFromUtf32(code);
        });
        return decoded.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
    }

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var blank = false;
        foreach (var raw in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = CollapseSpaces(raw.Trim());
            if (line.Length == 0)
            {
                blank = builder.Length > 0;
                continue;
            }

            if (builder.Length > 0)
                builder.Append(blank ? "\n\n" : "\n");
            blank = false;
            builder.Append(line);
        }

        return builder.ToString().Trim();
    }

    private static string CollapseSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousSpace = false;
        foreach (var character in value)
        {
            var isSpace = character == ' ';
            if (isSpace && previousSpace)
                continue;
            builder.Append(character);
            previousSpace = isSpace;
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptPattern();

    [GeneratedRegex(@"<script\b[^>]*>.*", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex UnclosedScriptPattern();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex StylePattern();

    [GeneratedRegex(@"<style\b[^>]*>.*", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex UnclosedStylePattern();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListItemPattern();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BreakPattern();

    [GeneratedRegex(@"</(p|div|h[1-6]|tr|table|blockquote|section|article|ul|ol|pre|li)\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockEndPattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"&#(x?[0-9a-fA-F]+);", RegexOptions.CultureInvariant)]
    private static partial Regex NumericEntityPattern();
}
