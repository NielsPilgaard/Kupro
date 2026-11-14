using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Kupro.Import.Snapshot;

namespace Kupro.Import.Trello;

/// <summary>
/// Trello (task 026, ADR-019). Reads boards, lists, labels, members, custom fields, cards with their checklists and
/// attachments, and the board's actions (comments and card changes) into the snapshot model. Mapping onto Kupro (lists
/// to states, label colours, archived cards, checklist items to Tasks, an epic source) is the pipeline's job; this
/// class only reads, and reports what it read in Trello's own terms.
/// </summary>
public sealed class TrelloConnector : ISourceConnector
{
    /// <summary>The most cards or actions Trello returns per request; both are paged with <c>before</c>.</summary>
    internal const int PageSize = 1000;

    /// <summary>Item type of a card.</summary>
    public const string CardType = "card";

    /// <summary>Item type of a checklist item streamed as its own item (it has a member or a due date).</summary>
    public const string CheckItemType = "checkItem";

    /// <summary>Field set to <c>"true"</c> on an archived card, so the owner can map archived cards to Canceled or skip them.</summary>
    public const string ArchivedField = "archived";

    /// <summary>Field set to <c>"true"</c> when the card's due date is ticked complete.</summary>
    public const string DueCompleteField = "dueComplete";

    /// <summary>Every member of a card with more than one, as person IDs; the first is also the assignee.</summary>
    public const string MembersField = "members";

    /// <summary>Change field for a ticked or unticked checklist item.</summary>
    public const string ChecklistField = "checklist";

    private static readonly string[] CheckItemStates = ["incomplete", "complete"];

    private const string CardQuery =
        "filter=all&fields=id,idShort,name,desc,idList,idLabels,idMembers,closed,due,start,dueComplete,dateLastActivity,url"
        + "&members=true&member_fields=fullName,username"
        + "&attachments=true&attachment_fields=id,name,url,bytes,mimeType,date,idMember,isUpload,fileName"
        + "&checklists=all&customFieldItems=true";

    private const string ActionQuery = "fields=id,type,date,data,idMemberCreator&memberCreator=true&memberCreator_fields=fullName,username";

    /// <summary>
    /// Read in full on every run, delta or not: who created each card (the actions that put a card on the board) and
    /// when it was archived.
    /// </summary>
    private const string FactActions = "createCard,copyCard,convertToCardFromCheckItem,emailCard,updateCard:closed";

    /// <summary>Read since the cursor: comments and the changes that become the changelog.</summary>
    private const string HistoryActions =
        "commentCard,updateCard,addMemberToCard,removeMemberFromCard,addLabelToCard,removeLabelFromCard,updateCustomFieldItem,updateCheckItemStateOnCard";

    private readonly TrelloClient _client;
    private readonly TimeProvider _time;
    private Catalog? _catalog;

    /// <param name="http">Sends the requests; the connector adds the key and token to each API request, not to the client.</param>
    /// <param name="key">Kupro's Trello API key.</param>
    /// <param name="token">The user's token from Trello's authorize link, read scope.</param>
    public TrelloConnector(HttpClient http, string key, string token, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _client = new TrelloClient(http, key, token, _time);
    }

    internal TrelloClient Client => _client;

    public async Task<SourceDescription> DescribeAsync(CancellationToken ct) => (await CatalogAsync(ct)).Description;

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        // The next delta starts when this read did: anything changed while it ran is read again, nothing is missed.
        var started = _time.GetUtcNow();
        var catalog = await CatalogAsync(ct);
        DateTimeOffset? since = cursor is null
            ? null
            : DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : throw new ArgumentException($"'{cursor}' is not a Trello cursor (a UTC time).", nameof(cursor));

        var boards = new List<BoardInfo>();
        if (scope.Projects.Count == 0)
        {
            boards.AddRange(catalog.Boards);
        }
        else
        {
            // A board in scope that Describe didn't list (archived) is still read when named.
            foreach (var id in scope.Projects)
            {
                boards.Add(catalog.Boards.FirstOrDefault(b => b.Id == id)
                    ?? await LoadBoardAsync(await GetAsync($"boards/{Uri.EscapeDataString(id)}?fields=id,name,url,closed", TrelloJson.Default.TrelloBoard, ct), ct));
            }
        }

        var reading = new TrelloReading(catalog);
        foreach (var board in boards)
        {
            reading.Learn(board.Members);
            var facts = new Dictionary<string, CardFacts>(StringComparer.Ordinal);
            await foreach (var action in ActionsAsync(board.Id, FactActions, null, ct))
            {
                if (Trello.CardId(action) is not { } cardId)
                {
                    continue;
                }

                var fact = facts.TryGetValue(cardId, out var known) ? known : facts[cardId] = new CardFacts();
                if (action.Type == "updateCard")
                {
                    // Newest first, so the first archive seen is the latest one.
                    if (fact.ArchivedAt is null && Trello.Bool(action.Data.Card, "closed") is true)
                    {
                        fact.ArchivedAt = action.Date;
                    }
                }
                else
                {
                    // Newest first, so overwriting keeps the oldest: whoever first put the card on the board.
                    fact.Creator = action.MemberCreator ?? (action.IdMemberCreator is { } creator ? new TrelloMember { Id = creator } : null);
                }
            }

            var history = new Dictionary<string, List<TrelloAction>>(StringComparer.Ordinal);
            await foreach (var action in ActionsAsync(board.Id, HistoryActions, since, ct))
            {
                if (Trello.CardId(action) is { } cardId)
                {
                    (history.TryGetValue(cardId, out var list) ? list : history[cardId] = []).Add(action);
                }
            }

            await foreach (var card in CardsAsync(board.Id, ct))
            {
                var actions = history.GetValueOrDefault(card.Id) ?? [];
                if (since is not null && card.DateLastActivity < since && actions.Count == 0)
                {
                    continue;
                }

                var item = reading.Item(board, card, facts.GetValueOrDefault(card.Id));
                if (scope.ResolvedAfter is not null && item.Resolved is not null && item.Resolved < scope.ResolvedAfter)
                {
                    continue;
                }

                var related = reading.Related(board, card, item).ToList();
                var changes = reading.History(board, item, actions).ToList();
                foreach (var person in reading.TakePeople())
                {
                    yield return person;
                }

                yield return item;
                foreach (var record in related)
                {
                    yield return record;
                }

                foreach (var record in changes)
                {
                    yield return record;
                }
            }
        }

        yield return new ReadCursor(started.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    public Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) =>
        Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url)
            ? _client.DownloadAsync(url, ct)
            : throw new SourceAccessException($"Attachment {attachment.Id} ({attachment.FileName}) has no URL.");

    /// <summary>A board's cards, archived ones included, newest first in pages of <see cref="PageSize"/>.</summary>
    private IAsyncEnumerable<TrelloCard> CardsAsync(string board, CancellationToken ct) =>
        PagedAsync($"boards/{board}/cards?{CardQuery}", TrelloJson.Default.ListTrelloCard, c => c.Id, ct);

    /// <summary>A board's actions of the given types, newest first, only those after <paramref name="since"/> when set.</summary>
    private IAsyncEnumerable<TrelloAction> ActionsAsync(string board, string filter, DateTimeOffset? since, CancellationToken ct)
    {
        var path = $"boards/{board}/actions?filter={filter}&{ActionQuery}";
        if (since is { } time)
        {
            path += "&since=" + Uri.EscapeDataString(time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        }

        return PagedAsync(path, TrelloJson.Default.ListTrelloAction, a => a.Id, ct);
    }

    /// <summary>
    /// Pages a nested resource with <c>limit</c> and <c>before</c> (the oldest ID so far; Trello IDs grow with time).
    /// Stops on a short page, or on a page with nothing new in case Trello ignored the paging.
    /// </summary>
    private async IAsyncEnumerable<T> PagedAsync<T>(string path, JsonTypeInfo<List<T>> type, Func<T, string> id, [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? before = null;
        while (true)
        {
            var page = await GetAsync($"{path}&limit={PageSize}{(before is null ? "" : "&before=" + before)}", type, ct);
            var fresh = 0;
            foreach (var entry in page)
            {
                var key = id(entry);
                if (seen.Add(key))
                {
                    fresh++;
                    yield return entry;
                }

                if (before is null || string.CompareOrdinal(key, before) < 0)
                {
                    before = key;
                }
            }

            if (page.Count < PageSize || fresh == 0)
            {
                yield break;
            }
        }
    }

    private Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct) => _client.GetAsync(path, type, ct);

    private async Task<Catalog> CatalogAsync(CancellationToken ct) => _catalog ??= await LoadCatalogAsync(ct);

    private async Task<Catalog> LoadCatalogAsync(CancellationToken ct)
    {
        var me = await GetAsync("members/me?fields=id,fullName,username,email", TrelloJson.Default.TrelloMember, ct);
        var listed = await GetAsync("members/me/boards?filter=open&fields=id,name,url,closed", TrelloJson.Default.ListTrelloBoard, ct);
        var boards = new List<BoardInfo>(listed.Count);
        foreach (var board in listed)
        {
            boards.Add(await LoadBoardAsync(board, ct));
        }

        return new Catalog(me, boards);
    }

    private async Task<BoardInfo> LoadBoardAsync(TrelloBoard board, CancellationToken ct)
    {
        // Labels default to 50 per request; 1,000 is the most a board can have.
        var lists = await GetAsync($"boards/{board.Id}/lists?filter=all&fields=id,name,closed,pos", TrelloJson.Default.ListTrelloList, ct);
        var labels = await GetAsync($"boards/{board.Id}/labels?fields=id,name,color&limit={PageSize}", TrelloJson.Default.ListTrelloLabel, ct);
        var members = await GetAsync($"boards/{board.Id}/members?fields=id,fullName,username", TrelloJson.Default.ListTrelloMember, ct);
        var fields = await GetAsync($"boards/{board.Id}/customFields", TrelloJson.Default.ListTrelloCustomField, ct);
        return new BoardInfo(board, lists, labels, members, fields);
    }

    /// <summary>What the full action stream says about a card: its creator and when it was last archived.</summary>
    internal sealed class CardFacts
    {
        public TrelloMember? Creator { get; set; }
        public DateTimeOffset? ArchivedAt { get; set; }
    }

    internal sealed class BoardInfo(TrelloBoard board, List<TrelloList> lists, List<TrelloLabel> labels, List<TrelloMember> members, List<TrelloCustomField> fields)
    {
        public TrelloBoard Board { get; } = board;
        public string Id => Board.Id;
        public List<TrelloList> Lists { get; } = [.. lists.OrderBy(l => l.Pos)];
        public Dictionary<string, TrelloList> ListsById { get; } = lists.ToDictionary(l => l.Id, StringComparer.Ordinal);
        public Dictionary<string, TrelloLabel> Labels { get; } = labels.ToDictionary(l => l.Id, StringComparer.Ordinal);
        public List<TrelloMember> Members { get; } = members;
        public List<TrelloCustomField> Fields { get; } = [.. fields.OrderBy(f => f.Pos)];
    }

    /// <summary>What Describe found, kept for the read: boards, their custom fields, and the people they name.</summary>
    internal sealed class Catalog
    {
        public Catalog(TrelloMember me, List<BoardInfo> boards)
        {
            Boards = boards;
            foreach (var field in boards.SelectMany(b => b.Fields))
            {
                Fields.TryAdd(field.Id, field);
            }

            // The token's own member first: only it comes with an email.
            Members[me.Id] = me;
            foreach (var member in boards.SelectMany(b => b.Members))
            {
                Members.TryAdd(member.Id, member);
            }

            Description = new SourceDescription
            {
                Source = new SnapshotSource { Kind = SourceKinds.Trello, BaseUrl = "https://trello.com" },
                Projects = [.. boards.Select(b => new SourceProject { Id = b.Id, Name = b.Board.Name, Url = b.Board.Url })],
                People = [.. Members.Values.Select(Trello.Person)],
                ItemTypes =
                [
                    new SourceItemType { Id = CardType, Name = "Card", Level = 1 },
                    new SourceItemType { Id = CheckItemType, Name = "Checklist item", Level = 0 },
                ],
                Statuses =
                [
                    .. boards.SelectMany(b => b.Lists.Select(l => new SourceStatus
                    {
                        Id = l.Id,
                        Name = l.Name,
                        Category = l.Closed ? "archived" : null,
                        ItemType = CardType,
                        Project = b.Id,
                    })),
                    .. CheckItemStates.Select(state => new SourceStatus
                    {
                        Id = Trello.CheckItemStatus(state),
                        Name = state,
                        Category = state,
                        ItemType = CheckItemType,
                    }),
                ],
                Fields =
                [
                    new SourceField { Id = ArchivedField, Name = "Archived", Kind = FieldKind.Select, Options = ["true"] },
                    new SourceField { Id = DueCompleteField, Name = "Due complete", Kind = FieldKind.Select, Options = ["true"] },
                    new SourceField { Id = MembersField, Name = "Members", Kind = FieldKind.MultiSelect },
                    .. Fields.Values.Select(f => new SourceField
                    {
                        Id = f.Id,
                        Name = f.Name,
                        Kind = Trello.Kind(f),
                        Options = f.Type switch
                        {
                            "list" => [.. f.Options.OrderBy(o => o.Pos).Select(o => o.Value?.Text ?? o.Id)],
                            "checkbox" => ["true"],
                            _ => [],
                        },
                    }),
                ],
            };
        }

        public List<BoardInfo> Boards { get; }
        public Dictionary<string, TrelloMember> Members { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TrelloCustomField> Fields { get; } = new(StringComparer.Ordinal);
        public SourceDescription Description { get; }
    }
}
