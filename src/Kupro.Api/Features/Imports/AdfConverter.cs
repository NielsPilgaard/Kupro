using System.Globalization;
using System.Text;
using System.Text.Json;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// Atlassian Document Format (Jira Cloud) to Markdown (task 024). A node it doesn't know is kept as a fenced
/// <c>json</c> block with the raw node, never dropped. Mentions become mention tokens the import resolves; media the
/// connector matched to an attachment carries its URL in <c>attrs.url</c> and is re-hosted from there.
/// </summary>
public sealed class AdfConverter : IMarkupConverter
{
    private const int MaxLength = 4 << 20;

    public MarkupKind Kind => MarkupKind.Adf;

    public string ToMarkdown(string source)
    {
        if (source.Length > MaxLength)
        {
            return MarkupConverters.Fence(source, "json");
        }

        try
        {
            // The default depth limit (64) bounds the recursion below.
            using var doc = JsonDocument.Parse(source);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && Type(root) == "doc" ? Blocks(Content(root)) : Block(root);
        }
        catch (JsonException)
        {
            return MarkupConverters.Fence(source, "json");
        }
    }

    private static string Blocks(IEnumerable<JsonElement> nodes) =>
        string.Join("\n\n", nodes.Select(Block).Where(b => b.Length > 0));

    private static string Block(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return Unknown(node);
        }

        return Type(node) switch
        {
            "paragraph" => Paragraph(node),
            "heading" => Heading(node),
            "bulletList" => List(node, ordered: false),
            "orderedList" => List(node, ordered: true),
            "taskList" => TaskList(node),
            "decisionList" => string.Join("\n", Content(node).Select(i => "- " + OneLine(Inlines(Content(i))))),
            "codeBlock" => MarkupConverters.Fence(string.Concat(Content(node).Select(t => Str(t, "text"))), Attr(node, "language") ?? ""),
            "blockquote" => Quote(Blocks(Content(node))),
            "rule" => "---",
            "table" => Table(node),
            "mediaSingle" or "mediaGroup" => string.Join("\n", Content(node).Select(Media)),
            "panel" => Quote($"**{Label(Attr(node, "panelType"))}:** {Blocks(Content(node))}"),
            "expand" or "nestedExpand" =>
                $"<details>\n<summary>{MarkupConverters.EscapeText(Attr(node, "title") ?? "Details")}</summary>\n\n{Blocks(Content(node))}\n\n</details>",
            "blockCard" or "embedCard" => Attr(node, "url") ?? Unknown(node),
            _ => Inline(node) is { Length: > 0 } inline && IsInline(node) ? inline : Unknown(node),
        };
    }

    private static string Paragraph(JsonElement node)
    {
        var text = Inlines(Content(node)).Trim();
        return string.Join("  \n", text.Split("  \n").Select(MarkupConverters.EscapeLineStart));
    }

    private static string Heading(JsonElement node)
    {
        var level = Math.Clamp(node.TryGetProperty("attrs", out var a) && a.TryGetProperty("level", out var l) && l.TryGetInt32(out var n) ? n : 1, 1, 6);
        var text = OneLine(Inlines(Content(node)));
        return text.Length == 0 ? "" : $"{new string('#', level)} {text}";
    }

    private static string List(JsonElement node, bool ordered)
    {
        var number = node.TryGetProperty("attrs", out var a) && a.TryGetProperty("order", out var o) && o.TryGetInt32(out var start) ? start - 1 : 0;
        var lines = new List<string>();
        foreach (var item in Content(node))
        {
            var marker = ordered ? $"{++number}. " : "- ";
            Indent(lines, marker, Blocks(Content(item)));
        }

        return string.Join("\n", lines);
    }

    private static string TaskList(JsonElement node)
    {
        var lines = new List<string>();
        foreach (var item in Content(node))
        {
            if (Type(item) == "taskList")
            {
                Indent(lines, "  ", TaskList(item));
                continue;
            }

            var done = Attr(item, "state") == "DONE";
            lines.Add($"- [{(done ? "x" : " ")}] {OneLine(Inlines(Content(item)))}");
        }

        return string.Join("\n", lines);
    }

    private static void Indent(List<string> lines, string marker, string content)
    {
        var indent = new string(' ', marker.Length);
        var split = content.Split('\n');
        lines.Add(marker + split[0]);
        lines.AddRange(split.Skip(1).Select(l => l.Length == 0 ? "" : indent + l));
    }

    private static string Quote(string text) => string.Join("\n", text.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));

    private static string Table(JsonElement node)
    {
        var rows = Content(node)
            .Select(r => Content(r).Select(c => OneLine(Blocks(Content(c))).Replace("|", "\\|", StringComparison.Ordinal)).ToList())
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

    /// <summary>A file the connector matched carries <c>url</c>; an external image has its own; otherwise its name stays as text.</summary>
    private static string Media(JsonElement node)
    {
        if (Type(node) is not ("media" or "mediaInline"))
        {
            return Block(node);
        }

        var name = Attr(node, "alt") ?? Attr(node, "fileName") ?? "attachment";
        var label = MarkupConverters.EscapeText(name);
        if (Attr(node, "url") is { Length: > 0 } url)
        {
            // Without a type, a file is taken for an image: that is what Jira's editor inserts inline.
            var mime = Attr(node, "mimeType");
            var image = Attr(node, "type") == "external"
                || (Type(node) == "media" && (mime is null || mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)));
            return $"{(image ? "!" : "")}[{label}]({Destination(url)})";
        }

        return $"\\[{label}\\]";
    }

    /// <summary>Neighbouring text with the same marks is one run, so <c>**a****b**</c> reads <c>**ab**</c>.</summary>
    private static string Inlines(IEnumerable<JsonElement> nodes)
    {
        var output = new StringBuilder();
        var run = new StringBuilder();
        JsonElement? runNode = null;
        void Flush()
        {
            if (runNode is { } first)
            {
                output.Append(Marked(run.ToString(), first));
            }

            run.Clear();
            runNode = null;
        }

        foreach (var node in nodes)
        {
            if (Type(node) == "text")
            {
                if (runNode is { } first && Marks(first) != Marks(node))
                {
                    Flush();
                }

                runNode ??= node;
                run.Append(Str(node, "text"));
                continue;
            }

            Flush();
            output.Append(IsInline(node) ? Inline(node) : Unknown(node));
        }

        Flush();
        return output.ToString();
    }

    private static string Marks(JsonElement node) => node.TryGetProperty("marks", out var m) ? m.GetRawText() : "";

    private static bool IsInline(JsonElement node) =>
        Type(node) is "text" or "hardBreak" or "mention" or "emoji" or "date" or "status" or "inlineCard" or "mediaInline" or "placeholder";

    private static string Inline(JsonElement node) => Type(node) switch
    {
        "text" => Marked(Str(node, "text"), node),
        "hardBreak" => "  \n",
        "mention" => MarkupConverters.Mention(Attr(node, "id") ?? "", (Attr(node, "text") ?? "").TrimStart('@').Replace('|', ' ')),
        "emoji" => Attr(node, "text") ?? Attr(node, "shortName") ?? "",
        "date" => long.TryParse(Attr(node, "timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "",
        "status" => $"\\[{MarkupConverters.EscapeText(Attr(node, "text") ?? "")}\\]",
        "inlineCard" => Attr(node, "url") ?? "",
        "mediaInline" => Media(node),
        _ => "",
    };

    private static string Marked(string raw, JsonElement node)
    {
        if (raw.Length == 0)
        {
            return "";
        }

        var marks = node.TryGetProperty("marks", out var m) && m.ValueKind == JsonValueKind.Array ? m.EnumerateArray().ToList() : [];
        var names = marks.Select(Type).ToHashSet(StringComparer.Ordinal);
        var text = names.Contains("code") ? CodeSpan(raw) : MarkupConverters.EscapeText(raw);
        foreach (var (mark, marker) in new[] { ("strike", "~~"), ("em", "*"), ("strong", "**") })
        {
            if (names.Contains(mark))
            {
                text = Wrap(text, marker);
            }
        }

        var link = marks.FirstOrDefault(x => Type(x) == "link");
        return link.ValueKind == JsonValueKind.Object && Attr(link, "href") is { Length: > 0 } href ? $"[{text}]({Destination(href)})" : text;
    }

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
        return line.Length == 0 ? "" : line.Contains('`', StringComparison.Ordinal) ? $"`` {line} ``" : $"`{line}`";
    }

    private static string Destination(string url) =>
        url.Any(c => char.IsWhiteSpace(c) || c is '(' or ')')
            ? $"<{url.Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)}>"
            : url;

    private static string Label(string? panelType) => panelType switch
    {
        "note" => "Note",
        "warning" => "Warning",
        "error" => "Error",
        "success" => "Success",
        "tip" => "Tip",
        _ => "Info",
    };

    private static string Unknown(JsonElement node) => MarkupConverters.Fence(node.GetRawText(), "json");

    private static string OneLine(string text) => string.Join(" ", text.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();

    private static string? Type(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    private static string Str(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string? Attr(JsonElement node, string name) =>
        node.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static List<JsonElement> Content(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array ? [.. c.EnumerateArray()] : [];
}
