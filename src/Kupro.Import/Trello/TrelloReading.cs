using System.Globalization;
using System.Text;
using System.Text.Json;
using Kupro.Import.Snapshot;
using static Kupro.Import.Trello.TrelloConnector;

namespace Kupro.Import.Trello;

/// <summary>
/// Turns Trello cards and actions into snapshot records for one read. Keeps the state a read needs across cards: the
/// names of people seen so far (actions and cards name them, attachments and checklist items only give an ID), the
/// people already reported, and the newest activity for the cursor.
/// </summary>
internal sealed class TrelloReading
{
    private readonly Dictionary<string, TrelloCustomField> _fields;
    private readonly Dictionary<string, TrelloMember> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly List<SourcePerson> _pending = [];

    public TrelloReading(Catalog catalog)
    {
        _fields = catalog.Fields;
        Learn(catalog.Members.Values);
    }

    /// <summary>The newest card activity or action read: the next delta starts there.</summary>
    public DateTimeOffset? Newest { get; private set; }

    public void Saw(DateTimeOffset at) => Newest = Newest is { } newest && newest >= at ? newest : at;

    /// <summary>People first referred to since the last call, to stream before the records that refer to them.</summary>
    public List<SourcePerson> TakePeople()
    {
        var people = _pending.ToList();
        _pending.Clear();
        return people;
    }

    public void Learn(IEnumerable<TrelloMember> members)
    {
        foreach (var member in members)
        {
            Learn(member);
        }
    }

    private void Learn(TrelloMember? member)
    {
        // A later sighting with a full name fills in one that only had an ID or a short name.
        if (member is not null && (!_known.TryGetValue(member.Id, out var known) || (known.FullName is null && member.FullName is not null)))
        {
            _known[member.Id] = member;
        }
    }

    public SourceItem Item(BoardInfo board, TrelloCard card, CardFacts? facts)
    {
        Learn(card.Members);
        Learn(facts?.Creator);
        var members = card.IdMembers.Select(PersonId).OfType<string>().ToArray();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (card.Closed)
        {
            fields[ArchivedField] = FieldValue.Text("true");
        }

        if (card.DueComplete)
        {
            fields[DueCompleteField] = FieldValue.Text("true");
        }

        if (members.Length > 1)
        {
            fields[MembersField] = FieldValue.Options(members);
        }

        foreach (var value in card.CustomFieldItems)
        {
            if (_fields.TryGetValue(value.IdCustomField, out var field) && Value(field, value.Value, value.IdValue) is { } converted)
            {
                fields[field.Id] = converted;
            }
        }

        return new SourceItem
        {
            Id = card.Id,
            Key = card.IdShort.ToString(CultureInfo.InvariantCulture),
            Project = board.Id,
            Type = CardType,
            Status = card.IdList,
            Title = card.Name,
            Description = Description(card),
            DescriptionMarkup = MarkupKind.Markdown,
            Assignee = members.FirstOrDefault(),
            Creator = facts?.Creator is { } creator ? PersonId(creator.Id) : null,
            Labels = [.. card.IdLabels.Select(id => board.Labels.GetValueOrDefault(id)).OfType<TrelloLabel>().Select(Trello.LabelName).OfType<string>()],
            Fields = fields,
            Start = Trello.Day(card.Start),
            Due = Trello.Day(card.Due),
            Links = [.. card.Attachments.Where(a => !a.IsUpload && a.Url is not null).Select(a => new SourceLink
            {
                Url = a.Url!,
                Title = string.IsNullOrEmpty(a.Name) || a.Name == a.Url ? null : a.Name,
            })],
            Url = card.Url,
            Created = Trello.IdTime(card.Id),
            Updated = card.DateLastActivity,
            Resolved = card.Closed ? facts?.ArchivedAt ?? card.DateLastActivity : null,
        };
    }

    /// <summary>
    /// Uploaded files, and checklist items with their own member or due date as items under the card, so the owner can
    /// make them Tasks (task 026). They are also in the description's task list, which is what they become otherwise.
    /// </summary>
    public IEnumerable<ISnapshotRecord> Related(BoardInfo board, TrelloCard card, SourceItem item)
    {
        foreach (var checkItem in card.Checklists.OrderBy(c => c.Pos).SelectMany(c => c.CheckItems.OrderBy(i => i.Pos)))
        {
            if (checkItem.IdMember is null && checkItem.Due is null)
            {
                continue;
            }

            yield return new SourceItem
            {
                Id = checkItem.Id,
                Project = board.Id,
                Type = CheckItemType,
                Status = Trello.CheckItemStatus(checkItem.State),
                Title = checkItem.Name,
                DescriptionMarkup = MarkupKind.Markdown,
                Parent = card.Id,
                Assignee = PersonId(checkItem.IdMember),
                Due = Trello.Day(checkItem.Due),
                Url = card.Url,
                Created = Trello.IdTime(checkItem.Id),
                Updated = item.Updated,
            };
        }

        foreach (var attachment in card.Attachments.Where(a => a.IsUpload))
        {
            yield return new SourceAttachment
            {
                Id = attachment.Id,
                Item = item.Id,
                FileName = attachment.FileName ?? attachment.Name ?? attachment.Id,
                ContentType = attachment.MimeType,
                Size = attachment.Bytes,
                Author = PersonId(attachment.IdMember),
                Created = attachment.Date,
                Url = attachment.Url,
            };
        }
    }

    /// <summary>
    /// Comments and changes from the card's actions, oldest first. A comment's ID is its action ID; a change's is the
    /// action ID, plus the field for an <c>updateCard</c> (one action can change several).
    /// </summary>
    public IEnumerable<ISnapshotRecord> History(BoardInfo board, SourceItem item, IEnumerable<TrelloAction> actions)
    {
        foreach (var action in actions.OrderBy(a => a.Date).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            Learn(action.MemberCreator);
            Saw(action.Date);
            var actor = PersonId(action.IdMemberCreator ?? action.MemberCreator?.Id);
            var data = action.Data;
            SourceChange Change(string id, string field, string? from, string? to, string? fromText = null, string? toText = null) => new()
            {
                Id = id,
                Item = item.Id,
                Actor = actor,
                At = action.Date,
                Field = field,
                From = from,
                To = to,
                FromText = fromText,
                ToText = toText,
            };

            switch (action.Type)
            {
                case "commentCard":
                    yield return new SourceComment
                    {
                        Id = action.Id,
                        Item = item.Id,
                        Author = actor,
                        Body = data.Text ?? "",
                        Markup = MarkupKind.Markdown,
                        Created = action.Date,
                        Updated = data.DateLastEdited,
                    };
                    break;
                case "updateCard":
                    foreach (var name in data.Old.Keys)
                    {
                        if (CardChange(board, data, name) is { } change)
                        {
                            yield return Change($"{action.Id}:{name}", change.Field, change.From, change.To, change.FromText, change.ToText);
                        }
                    }

                    break;
                case "addMemberToCard" or "removeMemberFromCard" when data.Member is { } member:
                    Learn(new TrelloMember { Id = member.Id, FullName = member.Name });
                    var person = PersonId(member.Id);
                    yield return action.Type == "addMemberToCard"
                        ? Change(action.Id, MembersField, null, person, null, member.Name)
                        : Change(action.Id, MembersField, person, null, member.Name, null);
                    break;
                case "addLabelToCard" or "removeLabelFromCard" when data.Label is { } label && Trello.LabelName(label) is { } labelName:
                    yield return action.Type == "addLabelToCard"
                        ? Change(action.Id, "labels", null, labelName)
                        : Change(action.Id, "labels", labelName, null);
                    break;
                case "updateCustomFieldItem" when data.CustomField is { } customField:
                    var field = _fields.GetValueOrDefault(customField.Id);
                    var before = Text(field, Trello.FieldValueOf(data.Old.GetValueOrDefault("value")), Trello.String(data.Old, "idValue"));
                    var after = Text(field, data.CustomFieldItem?.Value, data.CustomFieldItem?.IdValue);
                    if (before != after)
                    {
                        yield return Change(action.Id, customField.Id, before, after);
                    }

                    break;
                case "updateCheckItemStateOnCard" when data.CheckItem is { } checkItem:
                    var from = checkItem.State == "complete" ? "incomplete" : "complete";
                    yield return Change(action.Id, ChecklistField, from, checkItem.State, $"{checkItem.Name}: {from}", $"{checkItem.Name}: {checkItem.State}");
                    break;
            }
        }
    }

    private sealed record Mapped(string Field, string? From, string? To, string? FromText = null, string? ToText = null);

    /// <summary>One field of an <c>updateCard</c>: the old value is in <c>data.old</c>, the new one on <c>data.card</c>.</summary>
    private static Mapped? CardChange(BoardInfo board, TrelloActionData data, string name)
    {
        switch (name)
        {
            case "idList":
                var from = Trello.String(data.Old, name);
                var to = data.ListAfter?.Id ?? Trello.String(data.Card, name);
                return from == to
                    ? null
                    : new Mapped(
                        "status", from, to,
                        data.ListBefore?.Name ?? (from is null ? null : board.ListsById.GetValueOrDefault(from)?.Name),
                        data.ListAfter?.Name ?? (to is null ? null : board.ListsById.GetValueOrDefault(to)?.Name));
            case "name":
                return Raw("title", Trello.String(data.Old, name), Trello.String(data.Card, name));
            case "desc":
                // ADR-019: description edits replay as a marker, without the text.
                return new Mapped("description", null, null);
            case "due":
                return Raw("due", DayText(data.Old, name), DayText(data.Card, name));
            case "start":
                return Raw("start", DayText(data.Old, name), DayText(data.Card, name));
            case "closed":
                return Raw(ArchivedField, Trello.String(data.Old, name), Trello.String(data.Card, name));
            case "dueComplete":
                return Raw(DueCompleteField, Trello.String(data.Old, name), Trello.String(data.Card, name));
            default:
                // Position, cover, reminders: bookkeeping.
                return null;
        }

        static Mapped? Raw(string field, string? from, string? to) => from == to ? null : new Mapped(field, from, to);

        static string? DayText(Dictionary<string, JsonElement> values, string name) =>
            Trello.Day(Trello.Time(values.GetValueOrDefault(name)))?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>A custom field value as <see cref="SourceItem.Fields"/> holds it.</summary>
    private static JsonElement? Value(TrelloCustomField field, TrelloFieldValue? value, string? idValue)
    {
        var text = Text(field, value, idValue);
        if (text is null)
        {
            return null;
        }

        return field.Type switch
        {
            "number" => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? FieldValue.Number(number) : FieldValue.Text(text),
            _ => FieldValue.Text(text),
        };
    }

    /// <summary>A custom field value as text: a list option's name, a date's day, a ticked checkbox as "true".</summary>
    private static string? Text(TrelloCustomField? field, TrelloFieldValue? value, string? idValue)
    {
        var text = field?.Type switch
        {
            "list" when field is not null => idValue is null ? null : field.Options.FirstOrDefault(o => o.Id == idValue)?.Value?.Text ?? idValue,
            "checkbox" => value?.Checked == "true" ? "true" : null,
            "date" => Trello.Day(Trello.Time(value?.Date))?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "number" => value?.Number,
            "text" => value?.Text,
            _ => value?.Text ?? value?.Number ?? value?.Date ?? value?.Checked ?? idValue,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// The card's description, then each checklist as a Markdown task list under its name. An item's member and due
    /// date follow it in brackets (<c>@username</c>, so mention rewriting finds it), so nothing is lost when it isn't
    /// made a Task.
    /// </summary>
    private string? Description(TrelloCard card)
    {
        var markdown = new StringBuilder(card.Desc?.TrimEnd() ?? "");
        foreach (var checklist in card.Checklists.OrderBy(c => c.Pos))
        {
            if (markdown.Length > 0)
            {
                markdown.Append("\n\n");
            }

            markdown.Append("## ").Append(checklist.Name).Append('\n');
            foreach (var checkItem in checklist.CheckItems.OrderBy(i => i.Pos))
            {
                markdown.Append('\n').Append(checkItem.State == "complete" ? "- [x] " : "- [ ] ").Append(checkItem.Name);
                var notes = new List<string>(2);
                if (checkItem.IdMember is { } member && _known.GetValueOrDefault(member) is { } known)
                {
                    notes.Add(known.Username is null ? known.FullName ?? known.Id : "@" + known.Username);
                }

                if (Trello.Day(checkItem.Due) is { } due)
                {
                    notes.Add(due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                if (notes.Count > 0)
                {
                    markdown.Append(" (").AppendJoin(", ", notes).Append(')');
                }
            }
        }

        return markdown.Length == 0 ? null : markdown.ToString();
    }

    /// <summary>The person's ID, reporting the person the first time. Null for an ID no response has named.</summary>
    private string? PersonId(string? id)
    {
        if (id is null || !_known.TryGetValue(id, out var member))
        {
            return null;
        }

        if (_reported.Add(id))
        {
            _pending.Add(Trello.Person(member));
        }

        return id;
    }
}

/// <summary>Reading Trello values: object IDs, loose action data, label names, dates.</summary>
internal static class Trello
{
    public static SourcePerson Person(TrelloMember member) => new()
    {
        Id = member.Id,
        Name = member.FullName ?? member.Username ?? member.Id,
        Email = member.Email,
        Handle = member.Username,
    };

    public static string CheckItemStatus(string state) => $"{TrelloConnector.CheckItemType}/{state}";

    /// <summary>A label's name, or its colour for a colour-only label (common on Trello); null for neither.</summary>
    public static string? LabelName(TrelloLabel label) =>
        !string.IsNullOrWhiteSpace(label.Name) ? label.Name : label.Color;

    /// <summary>The card an action is about.</summary>
    public static string? CardId(TrelloAction action) => String(action.Data.Card, "id");

    /// <summary>
    /// A Trello ID is a MongoDB ObjectId: its first four bytes are the creation time in Unix seconds. Cards have no
    /// creation date of their own, so this is it.
    /// </summary>
    public static DateTimeOffset IdTime(string id) =>
        id.Length >= 8 && long.TryParse(id.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UnixEpoch;

    /// <summary>
    /// The day of a Trello date. Trello keeps a due date with the time of day the user picked, not as a midnight, so
    /// the UTC date is the picked day unless that time was within the user's UTC offset of midnight.
    /// </summary>
    public static DateOnly? Day(DateTimeOffset? time) => time is { } t ? DateOnly.FromDateTime(t.UtcDateTime) : null;

    public static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    public static DateTimeOffset? Time(JsonElement value) => value.ValueKind == JsonValueKind.String ? Time(value.GetString()) : null;

    public static string? String(Dictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    public static bool? Bool(Dictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>A custom field value object (<c>{"text": …}</c>) from an action's loose <c>data.old</c>.</summary>
    public static TrelloFieldValue? FieldValueOf(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? new TrelloFieldValue { Text = Property(value, "text"), Number = Property(value, "number"), Date = Property(value, "date"), Checked = Property(value, "checked") }
        : null;

    public static FieldKind Kind(TrelloCustomField field) => field.Type switch
    {
        "number" => FieldKind.Number,
        "date" => FieldKind.Date,
        "list" or "checkbox" => FieldKind.Select,
        _ => FieldKind.Text,
    };

    private static string? Property(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
