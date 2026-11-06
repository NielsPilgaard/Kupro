using System.Runtime.CompilerServices;
using System.Text;
using Kupro.Import;
using Kupro.Import.Snapshot;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// An in-memory source for import tests (ADR-019 IMP-003): returns the records it was given, filtered by project, and
/// serves attachment bytes from a dictionary. Records which cursors it was read with, so delta runs can be asserted.
/// </summary>
internal sealed class FakeConnector(SourceDescription description) : ISourceConnector
{
    public List<ISnapshotRecord> Records { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<string?> Cursors { get; } = [];
    public string? NextCursor { get; set; } = "cursor-1";

    public Task<SourceDescription> DescribeAsync(CancellationToken ct) => Task.FromResult(description);

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        Cursors.Add(cursor);
        var items = Records.OfType<SourceItem>()
            .Where(i => scope.Projects.Count == 0 || scope.Projects.Contains(i.Project))
            .Select(i => i.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var record in Records)
        {
            await Task.Yield();
            var keep = record switch
            {
                SourceItem item => items.Contains(item.Id),
                SourceComment comment => items.Contains(comment.Item),
                SourceAttachment attachment => items.Contains(attachment.Item),
                SourceChange change => items.Contains(change.Item),
                SourceRelation relation => items.Contains(relation.From) && items.Contains(relation.To),
                _ => true,
            };
            if (keep)
            {
                yield return record;
            }
        }

        yield return new ReadCursor(NextCursor);
    }

    public Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(Files[attachment.Id]));

    /// <summary>A small source: two projects, an Epic with a Story and a Bug, history, a comment and a file.</summary>
    public static FakeConnector Sample()
    {
        var description = new SourceDescription
        {
            Source = new SnapshotSource { Kind = SourceKinds.Jira, BaseUrl = "https://acme.example" },
            Projects = [new SourceProject { Id = "10", Key = "WEB", Name = "Web" }, new SourceProject { Id = "20", Key = "OPS", Name = "Ops" }],
            People = [new SourcePerson { Id = "u1", Name = "Sofie Larsen" }],
            ItemTypes = [new SourceItemType { Id = "epic", Name = "Epic", Level = 1 }, new SourceItemType { Id = "story", Name = "Story", Level = 0 }],
            Statuses = [new SourceStatus { Id = "1", Name = "To Do", Category = "new" }, new SourceStatus { Id = "3", Name = "Done", Category = "done" }],
            Fields = [new SourceField { Id = "cf_team", Name = "Team", Kind = FieldKind.Select, Options = ["Blue", "Red"] }],
        };
        var created = new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var connector = new FakeConnector(description);
        connector.Records.AddRange(
        [
            new SourcePerson { Id = "u1", Name = "Sofie Larsen", Email = "sofie@acme.example" },
            new SourcePerson { Id = "u2", Name = "Jonas Holm" },
            new SourceSprint { Id = "s1", Name = "Sprint 1", Start = new DateOnly(2025, 3, 3), End = new DateOnly(2025, 3, 14) },
            new SourceVersion { Id = "v1", Project = "10", Name = "1.0", Date = new DateOnly(2025, 4, 1) },
            new SourceItem
            {
                Id = "100", Key = "WEB-1", Project = "10", Type = "epic", Status = "1", Title = "Checkout",
                Created = created, Updated = created,
            },
            new SourceItem
            {
                Id = "101", Key = "WEB-2", Project = "10", Type = "story", Status = "3", Title = "Pay with MobilePay",
                Description = "<p>Æble <b>først</b></p>", DescriptionMarkup = MarkupKind.Html, Parent = "100",
                Assignee = "u1", Creator = "u2", Priority = "High", Labels = ["payments"],
                Fields = new Dictionary<string, System.Text.Json.JsonElement> { ["cf_team"] = FieldValue.Text("Blue") },
                EstimateSeconds = 3600 * 6, Points = 3, Due = new DateOnly(2025, 3, 20), Sprint = "s1", Versions = ["v1"],
                Links = [new SourceLink { Url = "https://git.example/pr/7", Title = "PR 7" }],
                Created = created, Updated = created.AddDays(2), Resolved = created.AddDays(2),
            },
            new SourceItem
            {
                Id = "200", Key = "OPS-1", Project = "20", Type = "story", Status = "1", Title = "Rotate keys",
                Created = created, Updated = created,
            },
            new SourceRelation { From = "101", To = "100", Kind = RelationKinds.Relates, Name = "relates to" },
            new SourceComment { Id = "c1", Item = "101", Author = "u2", Body = "Looks good", Created = created.AddDays(1) },
            new SourceAttachment { Id = "a1", Item = "101", FileName = "flow.png", ContentType = "image/png", Size = 4, Created = created },
            new SourceChange
            {
                Id = "h1", Item = "101", Actor = "u1", At = created.AddDays(2), Field = "status",
                From = "1", To = "3", FromText = "To Do", ToText = "Done",
            },
        ]);
        connector.Files["a1"] = Encoding.UTF8.GetBytes("PNG!");
        return connector;
    }
}
