using System.Text;

namespace Kupro.Api.Features.Issues;

/// <summary>
/// Search syntax shared by web, CLI and MCP (task 009): free text plus <c>key:value</c> filters, e.g.
/// <c>drag epics type:feature state:todo,inProgress assignee:me sprint:current</c>. Values with spaces are quoted
/// (<c>label:"needs review"</c>); list filters take commas or repeat. Unknown keys stay free text.
/// </summary>
public sealed record IssueSearch(IssueQuery Filters, string? Text)
{
    private static readonly string[] Keys = ["project", "type", "state", "assignee", "label", "sprint", "parent"];

    public static IssueResult<IssueSearch> Parse(string? q)
    {
        var projects = new List<string>();
        var types = new List<IssueType>();
        var states = new List<IssueState>();
        var single = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = new List<string>();

        foreach (var token in Tokenize(q ?? ""))
        {
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            var key = colon > 0 ? token[..colon].ToLowerInvariant() : null;
            if (key is null || !Keys.Contains(key))
            {
                text.Add(token);
                continue;
            }

            var value = Unquote(token[(colon + 1)..]);
            if (value.Length == 0)
            {
                return Invalid($"'{key}:' needs a value.");
            }

            var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (key)
            {
                case "project":
                    projects.AddRange(values);
                    break;
                case "type":
                    foreach (var v in values)
                    {
                        if (!Enum.TryParse<IssueType>(v, ignoreCase: true, out var t) || !Enum.IsDefined(t))
                        {
                            return Invalid($"Unknown type '{v}'. Use epic, feature or task.");
                        }

                        types.Add(t);
                    }

                    break;
                case "state":
                    foreach (var v in values)
                    {
                        if (!Enum.TryParse<IssueState>(v.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal),
                                ignoreCase: true, out var s) || !Enum.IsDefined(s))
                        {
                            return Invalid($"Unknown state '{v}'. Use backlog, todo, inProgress, inReview, done or canceled.");
                        }

                        states.Add(s);
                    }

                    break;
                default:
                    if (!single.TryAdd(key, value))
                    {
                        return Invalid($"'{key}:' can only be given once.");
                    }

                    break;
            }
        }

        // Member and sprint ids are GUIDs, so lowercasing only normalises me/none/active.
        var sprint = single.GetValueOrDefault("sprint")?.ToLowerInvariant();
        var filters = new IssueQuery
        {
            Project = projects.Count > 0 ? [.. projects] : null,
            Type = types.Count > 0 ? [.. types] : null,
            State = states.Count > 0 ? [.. states] : null,
            Assignee = single.GetValueOrDefault("assignee")?.ToLowerInvariant(),
            Label = single.GetValueOrDefault("label"),
            Sprint = sprint == "current" ? IssueService.ActiveSprint : sprint,
            Parent = single.GetValueOrDefault("parent"),
        };
        return new IssueSearch(filters, text.Count > 0 ? string.Join(' ', text) : null);
    }

    private static IssueError Invalid(string message) => new("invalid_query", message);

    /// <summary>Splits on whitespace; double quotes group words (and are kept, so free-text phrases stay phrases).</summary>
    private static IEnumerable<string> Tokenize(string q)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var ch in q)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                current.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string Unquote(string value) => value.Replace("\"", "", StringComparison.Ordinal).Trim();
}
