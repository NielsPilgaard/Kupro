using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>Turns one source markup into Markdown (ADR-019 Markup).</summary>
public interface IMarkupConverter
{
    MarkupKind Kind { get; }

    string ToMarkdown(string source);
}

/// <summary>Picks the converter. A markup with none yet is kept as a fenced block, not dropped.</summary>
public static partial class MarkupConverters
{
    private static readonly Dictionary<MarkupKind, IMarkupConverter> All = new IMarkupConverter[]
    {
        new PlainTextConverter(),
        new HtmlConverter(),
        new AdfConverter(),
        new JiraWikiConverter(),
    }.ToDictionary(c => c.Kind);

    public static string? ToMarkdown(string? source, MarkupKind kind)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var markdown = kind == MarkupKind.Markdown ? source
            : All.TryGetValue(kind, out var converter) ? converter.ToMarkdown(source)
            : Fence(source, "text");
        return string.IsNullOrWhiteSpace(markdown) ? null : markdown.Trim();
    }

    /// <summary>
    /// A mention of a source person by ID, for converters whose markup names people that way (Jira ADF, Azure DevOps).
    /// The import rewrites it to the person's Kupro name (<see cref="ImportReferences"/>).
    /// </summary>
    /// <summary>A mention the rewriter resolves to the person's Kupro name; <paramref name="shown"/> is used when the person isn't known.</summary>
    public static string Mention(string personId, string? shown = null) =>
        $"@{ImportReferences.MentionOpen}{personId}{(shown is { Length: > 0 } ? "|" + shown : "")}{ImportReferences.MentionClose}";

    /// <summary>A fence longer than any run of backticks inside, so the content can't close it.</summary>
    internal static string Fence(string text, string language)
    {
        var longest = BacktickRuns().Matches(text).Select(m => m.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{text.TrimEnd()}\n{fence}";
    }

    /// <summary>Source text must read as written: <c>*</c> or <c>[x]</c> in it is not Markdown. URLs stay whole so they still link.</summary>
    internal static string EscapeText(string text)
    {
        var output = new StringBuilder(text.Length + 8);
        var at = 0;
        foreach (Match url in Urls().Matches(text))
        {
            EscapeInto(output, text.AsSpan(at, url.Index - at));
            output.Append(url.Value);
            at = url.Index + url.Length;
        }

        EscapeInto(output, text.AsSpan(at));
        return Entities().Replace(output.ToString(), m => "\\" + m.Value);
    }

    /// <summary>What would start a block at the beginning of a line: a heading, list, quote or rule.</summary>
    internal static string EscapeLineStart(string line) => LineStart().IsMatch(line) ? "\\" + line : line;

    private static void EscapeInto(StringBuilder output, ReadOnlySpan<char> text)
    {
        for (var n = 0; n < text.Length; n++)
        {
            var c = text[n];
            // foo_bar is not emphasis in Markdown, and the escape would only make the stored text noisier.
            var wordJoin = c == '_' && n > 0 && n < text.Length - 1 && char.IsLetterOrDigit(text[n - 1]) && char.IsLetterOrDigit(text[n + 1]);
            if (c is '\\' or '`' or '*' or '[' or ']' or '<' || (c == '_' && !wordJoin))
            {
                output.Append('\\');
            }

            output.Append(c);
        }
    }

    [GeneratedRegex("`+")]
    private static partial Regex BacktickRuns();

    [GeneratedRegex(@"https?://[^\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Urls();

    [GeneratedRegex("&(?=#?[A-Za-z0-9]+;)")]
    private static partial Regex Entities();

    [GeneratedRegex(@"^(#{1,6}\s|[-+]\s|>|\d+[.)]\s|-{3,}\s*$)")]
    private static partial Regex LineStart();
}

/// <summary>Line breaks stay line breaks; nothing in the text is taken for Markdown.</summary>
public sealed class PlainTextConverter : IMarkupConverter
{
    public MarkupKind Kind => MarkupKind.Plain;

    public string ToMarkdown(string source)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n')
            .Select(l => MarkupConverters.EscapeLineStart(MarkupConverters.EscapeText(l.Trim())));
        return string.Join("  \n", lines).Replace("  \n  \n", "\n\n", StringComparison.Ordinal);
    }
}

/// <summary>
/// HTML (Azure DevOps, pasted rich text) to Markdown. An element it doesn't know, such as a video or an embed, is kept as
/// a fenced block. Scripts and styles are dropped: they never showed as content. The input comes from customers and
/// scripts, so anything too big or too deep to walk safely is kept whole as a fenced block.
/// </summary>
public sealed partial class HtmlConverter : IMarkupConverter
{
    private const int MaxLength = 1 << 20;
    private const int MaxDepth = 200;

    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "p", "div", "section", "article", "main", "header", "footer", "aside", "nav", "figure", "figcaption", "center",
        "details", "summary", "address", "dl", "dt", "dd", "form", "fieldset",
    };

    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal) { "script", "style", "head", "meta", "link", "title", "noscript", "template" };

    private static readonly HashSet<string> Inlines = new(StringComparer.Ordinal)
    {
        "a", "b", "strong", "i", "em", "code", "tt", "kbd", "samp", "var", "s", "del", "strike", "u", "ins", "span", "font", "small", "big",
        "sub", "sup", "mark", "abbr", "cite", "q", "dfn", "time", "label", "br", "img", "input", "wbr",
    };

    public MarkupKind Kind => MarkupKind.Html;

    public string ToMarkdown(string source)
    {
        if (source.Length > MaxLength)
        {
            return MarkupConverters.Fence(source, "html");
        }

        var body = new HtmlParser().ParseDocument(source).Body;
        return body is null ? "" : TooDeep(body) ? MarkupConverters.Fence(source, "html") : Blocks(body.ChildNodes);
    }

    /// <summary>Walks with its own stack: a recursive walk over a hostile document would overflow and take the process down.</summary>
    private static bool TooDeep(INode root)
    {
        var stack = new Stack<(INode Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.TryPop(out var top))
        {
            if (top.Depth > MaxDepth)
            {
                return true;
            }

            foreach (var child in top.Node.ChildNodes)
            {
                stack.Push((child, top.Depth + 1));
            }
        }

        return false;
    }

    private static string Blocks(IEnumerable<INode> nodes)
    {
        var parts = new List<string>();
        var run = new StringBuilder();
        void Flush()
        {
            var text = run.ToString().Trim();
            if (text.Length > 0)
            {
                parts.Add(MarkupConverters.EscapeLineStart(text));
            }

            run.Clear();
        }

        foreach (var node in nodes)
        {
            if (node is IElement element && !Inlines.Contains(element.LocalName))
            {
                Flush();
                var block = Block(element);
                if (block.Length > 0)
                {
                    parts.Add(block);
                }
            }
            else
            {
                run.Append(Inline(node));
            }
        }

        Flush();
        return string.Join("\n\n", parts);
    }

    private static string Block(IElement e)
    {
        var name = e.LocalName;
        if (Dropped.Contains(name))
        {
            return "";
        }

        if (Containers.Contains(name))
        {
            return Blocks(e.ChildNodes);
        }

        return name switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => Heading(e),
            "ul" or "ol" => List(e),
            "pre" => MarkupConverters.Fence(e.TextContent, ""),
            "blockquote" => string.Join("\n", Blocks(e.ChildNodes).Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l)),
            "hr" => "---",
            "table" => Table(e),
            _ => string.IsNullOrWhiteSpace(e.TextContent) && e.Children.Length == 0 && !HasSource(e)
                ? ""
                : MarkupConverters.Fence(e.OuterHtml, "html"),
        };
    }

    /// <summary>Office pastes are full of empty tags like <c>o:p</c>; an empty <c>iframe</c> or <c>video</c> still points at something.</summary>
    private static bool HasSource(IElement e) => e.HasAttribute("src") || e.HasAttribute("data");

    private static string Heading(IElement e)
    {
        var text = OneLine(InlineAll(e));
        return text.Length == 0 ? "" : $"{new string('#', e.LocalName[1] - '0')} {text}";
    }

    private static string List(IElement list)
    {
        var ordered = list.LocalName == "ol";
        var lines = new List<string>();
        var number = 0;
        foreach (var item in list.Children)
        {
            var marker = ordered ? $"{++number}. " : "- ";
            var indent = new string(' ', marker.Length);
            var content = Blocks(item.LocalName == "li" ? item.ChildNodes : [item]).Split('\n');
            lines.Add(marker + content[0]);
            lines.AddRange(content.Skip(1).Select(l => l.Length == 0 ? "" : indent + l));
        }

        return string.Join("\n", lines);
    }

    private static string Table(IElement table)
    {
        var rows = table.QuerySelectorAll("tr")
            .Select(tr => tr.Children.Where(c => c.LocalName is "td" or "th").Select(Cell).ToList())
            .Where(r => r.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return "";
        }

        var width = rows.Max(r => r.Count);
        string Row(IReadOnlyList<string> cells) =>
            "| " + string.Join(" | ", Enumerable.Range(0, width).Select(n => n < cells.Count ? cells[n] : "")) + " |";

        var lines = new List<string> { Row(rows[0]), "|" + string.Concat(Enumerable.Repeat(" --- |", width)) };
        lines.AddRange(rows.Skip(1).Select(Row));
        return string.Join("\n", lines);
    }

    private static string Cell(IElement cell) => OneLine(InlineAll(cell)).Replace("|", "\\|", StringComparison.Ordinal);

    private static string OneLine(string text) => Whitespace().Replace(text, " ").Trim();

    private static string InlineAll(IElement e) => string.Concat(e.ChildNodes.Select(Inline));

    private static string Inline(INode node)
    {
        if (node is IText text)
        {
            return MarkupConverters.EscapeText(Whitespace().Replace(text.Data, " "));
        }

        if (node is not IElement e || Dropped.Contains(e.LocalName))
        {
            return "";
        }

        switch (e.LocalName)
        {
            case "br":
                return "  \n";
            case "b" or "strong":
                return Wrap(InlineAll(e), "**");
            case "i" or "em":
                return Wrap(InlineAll(e), "*");
            case "s" or "del" or "strike":
                return Wrap(InlineAll(e), "~~");
            case "code" or "tt" or "kbd" or "samp":
                return CodeSpan(e.TextContent);
            case "a":
                return Link(e);
            case "img":
                return Image(e);
            case "input":
                return e.GetAttribute("type") == "checkbox" ? (e.HasAttribute("checked") ? "[x] " : "[ ] ") : "";
            default:
                return InlineAll(e);
        }
    }

    /// <summary>Spaces stay outside the markers: <c>** bold **</c> is not bold in Markdown.</summary>
    private static string Wrap(string inner, string marker)
    {
        var trimmed = inner.Trim();
        if (trimmed.Length == 0)
        {
            return inner;
        }

        var lead = inner[..(inner.Length - inner.TrimStart().Length)];
        var trail = inner[inner.TrimEnd().Length..];
        return $"{lead}{marker}{trimmed}{marker}{trail}";
    }

    private static string CodeSpan(string text)
    {
        var line = OneLine(text);
        if (line.Length == 0)
        {
            return "";
        }

        return line.Contains('`', StringComparison.Ordinal) ? $"`` {line} ``" : $"`{line}`";
    }

    private static string Link(IElement a)
    {
        // Azure DevOps: <a data-vss-mention="version:2.0,{identity id}">@Name</a>.
        if (a.GetAttribute("data-vss-mention") is { } mention && mention.Split(',') is [_, var identity] && identity.Trim().Length > 0)
        {
            return MarkupConverters.Mention(identity.Trim(), OneLine(a.TextContent).TrimStart('@').Replace('|', ' ').Replace('\u27E7', ' '));
        }

        var label = OneLine(InlineAll(a));
        var href = a.GetAttribute("href")?.Trim();
        return string.IsNullOrEmpty(href) ? label : $"[{(label.Length > 0 ? label : MarkupConverters.EscapeText(href))}]({Destination(href)})";
    }

    private static string Image(IElement img)
    {
        var src = img.GetAttribute("src")?.Trim();
        return string.IsNullOrEmpty(src) ? "" : $"![{MarkupConverters.EscapeText(OneLine(img.GetAttribute("alt") ?? ""))}]({Destination(src)})";
    }

    /// <summary>The angle-bracket form takes spaces and parentheses, and a line break or bracket can't end it early.</summary>
    private static string Destination(string url) =>
        url.Any(c => char.IsWhiteSpace(c) || c is '(' or ')')
            ? $"<{url.Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)}>"
            : url;

    [GeneratedRegex(@"[ \t\r\n\f]+")]
    private static partial Regex Whitespace();
}
