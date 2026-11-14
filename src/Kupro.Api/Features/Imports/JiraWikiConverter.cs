using System.Text;
using System.Text.RegularExpressions;
using Kupro.Import.Snapshot;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// Jira wiki markup (Data Center, Server, Jira's CSV export) to Markdown (task 024). Code and noformat blocks are kept
/// verbatim, quote and panel macros become quotes, and a macro it doesn't know is kept as a fenced block or a code span,
/// never dropped. Images and file links name the file; the connector points those it matched at the attachment's URL,
/// and the rest stay as the file's name.
/// </summary>
public sealed partial class JiraWikiConverter : IMarkupConverter
{
    private const int MaxLength = 1 << 20;
    private const int MaxDepth = 8;
    private const char Open = '';
    private const char Close = '';

    private static readonly HashSet<string> Panels = new(StringComparer.OrdinalIgnoreCase) { "panel", "info", "note", "warning", "tip" };

    public MarkupKind Kind => MarkupKind.JiraWiki;

    public string ToMarkdown(string source)
    {
        if (source.Length > MaxLength)
        {
            return MarkupConverters.Fence(source, "text");
        }

        try
        {
            return Convert(source.Replace(Open, ' ').Replace(Close, ' ').ReplaceLineEndings("\n"), 0, []);
        }
        catch (RegexMatchTimeoutException)
        {
            return MarkupConverters.Fence(source, "text");
        }
    }

    /// <param name="blocks">Rendered blocks the placeholders point at, shared with nested calls: a code block inside a
    /// quote is held by the outer call.</param>
    private static string Convert(string text, int depth, List<string> blocks)
    {
        // Block macros first, so nothing inside a code block is read as markup. Each becomes a placeholder line.
        string Hold(string rendered)
        {
            blocks.Add(rendered);
            return $"\n{Open}{blocks.Count - 1}{Close}\n";
        }

        text = CodeMacro().Replace(text, m => Hold(MarkupConverters.Fence(m.Groups["body"].Value.Trim('\n'),
            m.Groups["name"].Value.Equals("code", StringComparison.OrdinalIgnoreCase) ? Language(m.Groups["params"].Value) : "")));
        text = PairedMacro().Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            var body = m.Groups["body"].Value;
            if (name.Equals("color", StringComparison.OrdinalIgnoreCase))
            {
                return body;
            }

            if (depth >= MaxDepth)
            {
                return Hold(MarkupConverters.Fence(m.Value, "text"));
            }

            if (name.Equals("quote", StringComparison.OrdinalIgnoreCase))
            {
                return Hold(Quote(Convert(body, depth + 1, blocks)));
            }

            if (Panels.Contains(name))
            {
                var title = Param(m.Groups["params"].Value, "title");
                var label = title is not null ? MarkupConverters.EscapeText(title) : name.Equals("panel", StringComparison.OrdinalIgnoreCase) ? null : Capital(name);
                var inner = Convert(body, depth + 1, blocks);
                return Hold(Quote(label is null ? inner : $"**{label}:** {inner}"));
            }

            return Hold(MarkupConverters.Fence(m.Value, "text"));
        });

        var parts = new List<string>();
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count > 0)
            {
                parts.Add(string.Join("  \n", paragraph));
                paragraph.Clear();
            }
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            if (Placeholder().Match(trimmed) is { Success: true } held)
            {
                Flush();
                parts.Add(blocks[int.Parse(held.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
                continue;
            }

            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                Flush();
                var title = OneLine(Inline(heading.Groups[2].Value));
                if (title.Length > 0)
                {
                    parts.Add($"{new string('#', heading.Groups[1].Value[0] - '0')} {title}");
                }

                continue;
            }

            if (Bq().Match(trimmed) is { Success: true } bq)
            {
                Flush();
                parts.Add(Quote(Inline(bq.Groups[1].Value)));
                continue;
            }

            if (Rule().IsMatch(trimmed))
            {
                Flush();
                parts.Add("---");
                continue;
            }

            if (ListItem().IsMatch(trimmed))
            {
                Flush();
                var items = new List<string>();
                for (; i < lines.Length && ListItem().IsMatch(lines[i].TrimStart()); i++)
                {
                    items.Add(lines[i].Trim());
                }

                i--;
                parts.Add(List(items));
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                Flush();
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                {
                    rows.Add(lines[i].Trim());
                }

                i--;
                parts.Add(Table(rows));
                continue;
            }

            paragraph.Add(MarkupConverters.EscapeLineStart(Inline(trimmed)));
        }

        Flush();
        return string.Join("\n\n", parts.Where(p => p.Length > 0));
    }

    private static string List(List<string> items)
    {
        var lines = new List<string>();
        var indents = new List<int>();
        var numbers = new List<int>();
        foreach (var item in items)
        {
            var match = ListItem().Match(item);
            var markers = match.Groups[1].Value;
            var level = markers.Length;
            var ordered = markers[^1] == '#';
            while (indents.Count > level)
            {
                indents.RemoveAt(indents.Count - 1);
                numbers.RemoveAt(numbers.Count - 1);
            }

            while (indents.Count < level)
            {
                indents.Add(indents.Count == 0 ? 0 : indents[^1]);
                numbers.Add(0);
            }

            var indent = level == 1 ? 0 : indents[level - 2];
            var marker = ordered ? $"{++numbers[level - 1]}. " : "- ";
            indents[level - 1] = indent + marker.Length;
            lines.Add(new string(' ', indent) + marker + Inline(match.Groups[2].Value));
        }

        return string.Join("\n", lines);
    }

    private static string Table(List<string> rows)
    {
        var cells = rows.Select(Cells).Where(r => r.Count > 0).ToList();
        if (cells.Count == 0)
        {
            return "";
        }

        var width = cells.Max(r => r.Count);
        string Row(IReadOnlyList<string> row) =>
            "| " + string.Join(" | ", Enumerable.Range(0, width).Select(n => n < row.Count ? row[n] : "")) + " |";

        var lines = new List<string> { Row(cells[0]), "|" + string.Concat(Enumerable.Repeat(" --- |", width)) };
        lines.AddRange(cells.Skip(1).Select(Row));
        return string.Join("\n", lines);
    }

    /// <summary>A row's cells: <c>||</c> separates header cells, <c>|</c> data cells; a pipe inside a link or <c>{{…}}</c> is content.</summary>
    private static List<string> Cells(string row)
    {
        var header = row.StartsWith("||", StringComparison.Ordinal);
        var cells = new List<string>();
        var cell = new StringBuilder();
        var (brackets, braces) = (0, 0);
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (c == '[') { brackets++; }
            else if (c == ']' && brackets > 0) { brackets--; }
            else if (c == '{' && i + 1 < row.Length && row[i + 1] == '{') { braces++; }
            else if (c == '}' && i + 1 < row.Length && row[i + 1] == '}' && braces > 0) { braces--; }

            if (c == '|' && brackets == 0 && braces == 0)
            {
                if (header && i + 1 < row.Length && row[i + 1] == '|')
                {
                    i++;
                }

                cells.Add(cell.ToString());
                cell.Clear();
                continue;
            }

            cell.Append(c);
        }

        cells.Add(cell.ToString());
        if (cells.Count > 0 && cells[0].Trim().Length == 0)
        {
            cells.RemoveAt(0);
        }

        if (cells.Count > 0 && cells[^1].Trim().Length == 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return [.. cells.Select(c => OneLine(Inline(c.Trim())).Replace("|", "\\|", StringComparison.Ordinal))];
    }

    private static string Inline(string text)
    {
        var output = new StringBuilder(text.Length + 8);
        var at = 0;
        foreach (Match m in InlineToken().Matches(text))
        {
            output.Append(MarkupConverters.EscapeText(text[at..m.Index]));
            output.Append(Token(m));
            at = m.Index + m.Length;
        }

        output.Append(MarkupConverters.EscapeText(text[at..]));
        return output.ToString();
    }

    private static string Token(Match m)
    {
        if (m.Groups["mono"].Success)
        {
            return CodeSpan(m.Groups["mono"].Value);
        }

        if (m.Groups["src"].Success)
        {
            var src = m.Groups["src"].Value.Trim();
            var name = FileName(src);
            return IsUrl(src) ? $"![{MarkupConverters.EscapeText(name)}]({Destination(src)})" : $"\\[{MarkupConverters.EscapeText(name)}\\]";
        }

        if (m.Groups["inner"].Success)
        {
            return Link(m.Groups["inner"].Value);
        }

        if (m.Groups["br"].Success)
        {
            return "  \n";
        }

        if (m.Groups["esc"].Success)
        {
            return MarkupConverters.EscapeText(m.Groups["esc"].Value);
        }

        if (m.Groups["url"].Success)
        {
            return m.Groups["url"].Value;
        }

        if (m.Groups["macro"].Success)
        {
            return m.Groups["mname"].Value.ToLowerInvariant() is "color" or "anchor" ? "" : CodeSpan(m.Groups["macro"].Value);
        }

        foreach (var (group, marker) in new[] { ("s", "**"), ("e", "*"), ("d", "~~"), ("c", "*") })
        {
            if (m.Groups[group].Success)
            {
                return marker + Inline(m.Groups[group].Value) + marker;
            }
        }

        // Underline, superscript and subscript have no Markdown; the text stays.
        return Inline(m.Groups["plain"].Value);
    }

    /// <summary><c>[text|url]</c>, <c>[url]</c>, <c>[~user]</c>, <c>[~accountid:…]</c>, <c>[^file]</c>, <c>[#anchor]</c>.</summary>
    private static string Link(string inner)
    {
        var bar = inner.IndexOf('|', StringComparison.Ordinal);
        var label = bar < 0 ? null : inner[..bar];
        var target = (bar < 0 ? inner : inner[(bar + 1)..].Split('|')[0]).Trim();

        if (target.StartsWith('~'))
        {
            var user = target[1..];
            if (user.StartsWith("accountid:", StringComparison.OrdinalIgnoreCase))
            {
                return MarkupConverters.Mention(user["accountid:".Length..].Trim(), label?.Trim().TrimStart('@'));
            }

            return Handle().IsMatch(user) ? "@" + user : MarkupConverters.Mention(user, label?.Trim().TrimStart('@') ?? user);
        }

        if (target.StartsWith('^'))
        {
            var file = target[1..].Trim();
            return $"\\[{MarkupConverters.EscapeText(label ?? file)}\\]";
        }

        if (IsUrl(target) || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return label is null ? target : $"[{Inline(label.Trim())}]({Destination(target)})";
        }

        if (target.StartsWith('#'))
        {
            return Inline(label ?? target[1..]);
        }

        return "\\[" + Inline(inner) + "\\]";
    }

    private static string Quote(string text) => string.Join("\n", text.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));

    private static string CodeSpan(string text)
    {
        var line = OneLine(text);
        return line.Length == 0 ? "" : line.Contains('`', StringComparison.Ordinal) ? $"`` {line} ``" : $"`{line}`";
    }

    private static string Language(string parameters) =>
        parameters.Split('|').Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0 && !p.Contains('=', StringComparison.Ordinal))?.ToLowerInvariant() ?? "";

    private static string? Param(string parameters, string name) =>
        parameters.Split('|').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))?[1].Trim();

    private static string Capital(string name) => char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();

    private static bool IsUrl(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string FileName(string src)
    {
        var path = src.Split('?')[0].TrimEnd('/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        return Uri.UnescapeDataString(name.Length == 0 ? src : name);
    }

    private static string Destination(string url) =>
        url.Any(c => char.IsWhiteSpace(c) || c is '(' or ')') ? $"<{url.Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)}>" : url;

    private static string OneLine(string text) => string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();

    [GeneratedRegex(@"\{(?<name>code|noformat)(?::(?<params>[^}\n]*))?\}(?<body>.*?)\{\k<name>\}", RegexOptions.Singleline | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex CodeMacro();

    [GeneratedRegex(@"\{(?<name>[a-zA-Z][\w-]*)(?::(?<params>[^}\n]*))?\}(?<body>.*?)\{\k<name>\}", RegexOptions.Singleline | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex PairedMacro();

    [GeneratedRegex("^(\\d+)$")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^h([1-6])\.\s*(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^bq\.\s*(.*)$")]
    private static partial Regex Bq();

    [GeneratedRegex(@"^-{4,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^([*#]+|-)\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex Handle();

    // Leftmost match wins, so a URL is taken whole before any _ or - inside it reads as emphasis.
    [GeneratedRegex(
        @"\{\{(?<mono>.+?)\}\}"
        + @"|!(?<src>[^!\s|][^!|\n]*?)(?:\|[^!\n]*)?!"
        + @"|\[(?<inner>[^\]\n]+)\]"
        + @"|(?<br>\\\\)"
        + @"|\\(?<esc>[*_\-+^~?{}\[\]!|#])"
        + @"|(?<url>https?://[^\s\[\]|]+)"
        + @"|(?<macro>\{(?<mname>[a-zA-Z][\w-]*)(?::[^}\n]*)?\})"
        + @"|(?<![\w*])\*(?=\S)(?<s>[^*\n]+?)(?<=\S)\*(?![\w*])"
        + @"|(?<![\w_])_(?=\S)(?<e>[^_\n]+?)(?<=\S)_(?![\w_])"
        + @"|(?<![\w-])-(?=\S)(?<d>[^-\n]+?)(?<=\S)-(?![\w-])"
        + @"|\?\?(?<c>[^?\n]+?)\?\?"
        + @"|(?<![\w+])\+(?=\S)(?<plain>[^+\n]+?)(?<=\S)\+(?![\w+])"
        + @"|(?<![\w^])\^(?=\S)(?<plain>[^\^\n]+?)(?<=\S)\^(?![\w^])"
        + @"|(?<![\w~])~(?=\S)(?<plain>[^~\n]+?)(?<=\S)~(?![\w~])",
        RegexOptions.None, 2000)]
    private static partial Regex InlineToken();
}
