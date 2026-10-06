using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ServiceNowDesk.Mapping;

public static partial class HtmlText
{
    public const string BrowserImagePlaceholder = "Image — open the article in the browser to see it";

    public static string ToReadable(string? html) => ToReadable(html, null);

    public static string ToReadable(string? html, Uri? instance)
    {
        var blocks = ToBlocks(html, instance);
        if (blocks.Count == 0)
            return "";
        if (blocks.Count == 1)
            return blocks[0].PlainLine;

        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(block.PlainLine);
        }

        return builder.ToString();
    }

    public static IReadOnlyList<ArticleBlock> ToBlocks(string? html, Uri? instance)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var images = new List<ResolvedImage>();
        var token = Guid.NewGuid().ToString("N");
        var text = StripActiveContent(html);
        text = ImageTagPattern().Replace(text, match =>
        {
            var index = images.Count;
            images.Add(ResolveImage(ReadSrc(match.Value), instance));
            return "\n\u0001KIMG:" + token + ":" + index.ToString(CultureInfo.InvariantCulture) + "\u0001\n";
        });
        text = ListItemPattern().Replace(text, "\n• ");
        text = BreakPattern().Replace(text, "\n");
        text = BlockEndPattern().Replace(text, "\n");
        text = TagPattern().Replace(text, "");
        var readable = Collapse(Decode(text));
        if (readable.Length == 0)
            return [];

        var blocks = new List<ArticleBlock>();
        var textBuilder = new StringBuilder();
        foreach (var line in readable.Split('\n'))
        {
            if (TryReadMarker(line, token, out var index) && index >= 0 && index < images.Count)
            {
                FlushText(blocks, textBuilder);
                blocks.Add(images[index].ToBlock());
                continue;
            }

            if (line.StartsWith("\u0001KIMG:", StringComparison.Ordinal))
                continue;

            if (textBuilder.Length > 0)
                textBuilder.Append('\n');
            textBuilder.Append(line);
        }

        FlushText(blocks, textBuilder);
        return blocks;
    }

    private static void FlushText(List<ArticleBlock> blocks, StringBuilder textBuilder)
    {
        if (textBuilder.Length == 0)
            return;
        var value = textBuilder.ToString().Trim('\n');
        textBuilder.Clear();
        if (value.Length == 0)
            return;
        blocks.Add(ArticleBlock.Paragraph(value));
    }

    private static bool TryReadMarker(string line, string token, out int index)
    {
        index = -1;
        var prefix = "\u0001KIMG:" + token + ":";
        const string suffix = "\u0001";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var number = line[prefix.Length..^suffix.Length];
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static string StripActiveContent(string html)
    {
        var text = CommentPattern().Replace(html, " ");
        text = ScriptPattern().Replace(text, " ");
        text = UnclosedScriptPattern().Replace(text, " ");
        text = StylePattern().Replace(text, " ");
        text = UnclosedStylePattern().Replace(text, " ");
        return text;
    }

    private static string? ReadSrc(string tag)
    {
        var match = ImageSrcPattern().Match(tag);
        if (!match.Success)
            return null;
        if (match.Groups[1].Success)
            return match.Groups[1].Value;
        if (match.Groups[2].Success)
            return match.Groups[2].Value;
        return match.Groups[3].Value;
    }

    private static ResolvedImage ResolveImage(string? src, Uri? instance)
    {
        var raw = Decode(src ?? "").Trim();
        if (raw.Length == 0)
            return ResolvedImage.Hidden;

        if (raw.StartsWith("//", StringComparison.Ordinal))
            return ResolvedImage.Link("https:" + raw);

        if (IsHttpUrl(raw))
            return ResolvedImage.Link(raw);

        if (HasScheme(raw) || instance is null)
            return ResolvedImage.Hidden;

        var authority = instance.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var path = raw.StartsWith('/') ? raw : "/" + raw;
        return ResolvedImage.Link(authority + path);
    }

    private static bool IsHttpUrl(string value) =>
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0)
            return false;
        for (var i = 0; i < colon; i++)
        {
            var character = value[i];
            var ok = i == 0
                ? char.IsAsciiLetter(character)
                : char.IsAsciiLetterOrDigit(character) || character is '+' or '.' or '-';
            if (!ok)
                return false;
        }

        return true;
    }

    private readonly record struct ResolvedImage(string Label, string? Url)
    {
        public static ResolvedImage Hidden => new(BrowserImagePlaceholder, null);

        public static ResolvedImage Link(string url) => new("Image", url);

        public ArticleBlock ToBlock() =>
            string.IsNullOrEmpty(Url) ? ArticleBlock.Paragraph(Label) : ArticleBlock.Link(Label, Url);
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

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTagPattern();

    [GeneratedRegex(@"\bsrc\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageSrcPattern();

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

public sealed class ArticleBlock
{
    private ArticleBlock(string text, string? url, bool isLink)
    {
        Text = text;
        Url = url;
        IsLink = isLink;
    }

    public string Text { get; }

    public string? Url { get; }

    public bool IsLink { get; }

    public string PlainLine => IsLink && !string.IsNullOrEmpty(Url) ? Text + " " + Url : Text;

    public static ArticleBlock Paragraph(string text) => new(text, null, false);

    public static ArticleBlock Link(string label, string url) => new(label, url, true);
}
