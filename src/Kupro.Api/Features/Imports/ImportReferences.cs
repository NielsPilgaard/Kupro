using System.Text;
using System.Text.RegularExpressions;

namespace Kupro.Api.Features.Imports;

/// <summary>A copied attachment as imported text links to it.</summary>
public sealed record AttachmentRef(Guid Id, string FileName, bool Image);

/// <summary>
/// Rewrites what imported Markdown points at (ADR-019 Markup): mentions to the person's Kupro name, inline images and
/// file links to the copied attachment (<c>attachment:ID</c>), and links to source issues to their Kupro key. The
/// source's links die with its subscription; these don't. Code blocks and code spans are left as written.
/// </summary>
public sealed partial class ImportReferences(
    IReadOnlyDictionary<string, string> names,
    IReadOnlyDictionary<string, string> handles,
    IReadOnlyDictionary<string, AttachmentRef> files,
    IReadOnlyDictionary<string, string> issues)
{
    internal const char MentionOpen = '⟦';
    internal const char MentionClose = '⟧';

    public static ImportReferences None { get; } = new(
        new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, AttachmentRef>(), new Dictionary<string, string>());

    public string? Rewrite(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || (!markdown.Contains('@', StringComparison.Ordinal) && !markdown.Contains("://", StringComparison.Ordinal)))
        {
            return markdown;
        }

        var output = new StringBuilder(markdown.Length);
        string? fence = null;
        foreach (var line in markdown.Split('\n'))
        {
            if (output.Length > 0)
            {
                output.Append('\n');
            }

            var trimmed = line.TrimStart();
            if (fence is not null)
            {
                output.Append(line);
                if (trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.TrimEnd().All(c => c == fence[0]))
                {
                    fence = null;
                }

                continue;
            }

            if (FenceOpen().Match(trimmed) is { Success: true } open)
            {
                fence = open.Value;
                output.Append(line);
                continue;
            }

            output.Append(Inline().Replace(line, Replace));
        }

        return output.ToString();
    }

    private string Replace(Match match)
    {
        if (match.Groups["code"].Success)
        {
            return match.Value;
        }

        if (match.Groups["label"].Success)
        {
            var bang = match.Groups["bang"].Value;
            var label = Mentions(match.Groups["label"].Value);
            var destination = match.Groups["dest"].Value.Trim('<', '>');
            if (files.TryGetValue(destination, out var file))
            {
                return $"{bang}[{label}](attachment:{file.Id})";
            }

            // A link named by its URL or by a key (PROJ-1, #4567) is a key reference: the Kupro key says it all.
            if (bang.Length == 0 && issues.TryGetValue(destination, out var key))
            {
                return label == destination || KeyLike().IsMatch(label) ? key : $"{label} ({key})";
            }

            return $"{bang}[{label}]({match.Groups["dest"].Value}{match.Groups["title"].Value})";
        }

        if (match.Groups["url"].Success)
        {
            // A sentence's full stop after a bare URL is not part of it; inside <...> everything is.
            var url = match.Groups["url"].Value;
            var trail = match.Value.StartsWith('<') ? 0 : url.Length - url.TrimEnd('.', ',', ';', ':', '!', '?').Length;
            var (bare, suffix) = (url[..^trail], url[^trail..]);
            if (files.TryGetValue(bare, out var file))
            {
                return $"{(file.Image ? "!" : "")}[{MarkupConverters.EscapeText(file.FileName)}](attachment:{file.Id}){suffix}";
            }

            return issues.TryGetValue(bare, out var key) ? key + suffix : match.Value;
        }

        if (match.Groups["id"].Success)
        {
            var id = match.Groups["id"].Value;
            return "@" + MarkupConverters.EscapeText(names.GetValueOrDefault(id, id));
        }

        var handle = match.Groups["handle"].Value;
        var cut = handle.TrimEnd('.', '_', '-');
        return handles.TryGetValue(cut, out var name) ? "@" + MarkupConverters.EscapeText(name) + handle[cut.Length..] : match.Value;
    }

    private string Mentions(string text) =>
        text.Contains(MentionOpen, StringComparison.Ordinal)
            ? MentionToken().Replace(text, m => "@" + MarkupConverters.EscapeText(names.GetValueOrDefault(m.Groups[1].Value, m.Groups[1].Value)))
            : text;

    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9_]*-\d+|[\w./-]*#\d+)$")]
    private static partial Regex KeyLike();

    [GeneratedRegex(@"^(`{3,}|~{3,})")]
    private static partial Regex FenceOpen();

    [GeneratedRegex(@"@⟦([^⟧\n]{1,200})⟧")]
    private static partial Regex MentionToken();

    // One pass, so a URL inside a link or code is never matched twice. Order matters: code, links, URLs, mentions.
    [GeneratedRegex(
        @"(?<code>`+[^`]*`+)"
        + @"|(?<bang>!?)\[(?<label>(?:\\.|[^\]\\])*)\]\((?<dest><[^>\n]*>|[^)\s]+)(?<title>\s+""[^""\n]*"")?\)"
        + @"|<(?<url>https?://[^\s<>]+)>"
        + @"|(?<url>https?://[^\s<>()\[\]]+)"
        + @"|@⟦(?<id>[^⟧\n]{1,200})⟧"
        + @"|(?<![\w@./])@(?<handle>[A-Za-z0-9][A-Za-z0-9._-]{0,63})",
        RegexOptions.IgnoreCase)]
    private static partial Regex Inline();
}
