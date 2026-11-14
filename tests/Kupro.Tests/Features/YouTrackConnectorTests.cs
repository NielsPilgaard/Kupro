using System.Text.Json.Nodes;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.Snapshot;
using Kupro.Import.YouTrack;
using Kupro.Tests.Infrastructure;
using static Kupro.Tests.Infrastructure.FakeYouTrack;

namespace Kupro.Tests.Features;

/// <summary>
/// The YouTrack connector (task 027) against <see cref="FakeYouTrack"/>: Describe from per-project custom fields, the
/// read into snapshot records (subtask parents, links, estimation in hours, sprints, files), activities as changes,
/// deltas, and YouTrack's default mapping.
/// </summary>
public sealed class YouTrackConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("https://acme.youtrack.cloud", "https://acme.youtrack.cloud/api/")]
    [InlineData("https://acme.youtrack.cloud/api/", "https://acme.youtrack.cloud/api/")]
    [InlineData("https://yt.acme.example/youtrack/", "https://yt.acme.example/youtrack/api/")]
    public void The_api_is_under_the_address_people_open(string url, string api) =>
        new YouTrackConnector(new HttpClient(), url, Token).Client.Api.AbsoluteUri.ShouldBe(api);

    [Fact]
    public async Task Describe_reads_projects_types_states_and_fields_from_project_custom_fields()
    {
        var description = await Connect(Sample()).DescribeAsync(Ct);

        description.Source.ShouldBe(new SnapshotSource { Kind = SourceKinds.YouTrack, BaseUrl = "https://acme.youtrack.cloud" });
        description.Projects.Select(p => (p.Id, p.Key, p.Name)).ShouldBe([("0-1", "WEB", "Web"), ("0-2", "OLD", "Old stuff")]);
        description.ItemTypes.Select(t => t.Id).ShouldBe(["Epic", "Bug", "Task", "Issue"], "OLD has no Type field");
        description.Statuses.Select(s => (s.Id, s.Category)).ShouldBe(
            [("Submitted", null), ("In Progress", null), ("Fixed", "done"), ("Duplicate", "done"), ("Obsolete", "done")]);
        description.Fields.Select(f => (f.Id, f.Kind)).ShouldBe(
            [("Component", FieldKind.MultiSelect), ("Budget", FieldKind.Number), ("Reviewer", FieldKind.User), (YouTrackConnector.SpentTimeField, FieldKind.Number)]);
        description.Fields[0].Options.ShouldBe(["Api", "Web"]);
        description.People.Select(p => (p.Id, p.Name, p.Handle)).ShouldBe([("1-1", "Sofie Larsen", "sofie"), ("1-2", "Jonas Holm", "jonas")]);
    }

    [Fact]
    public async Task Read_maps_issues_with_parents_links_hours_sprints_files_and_activities()
    {
        var youtrack = Sample();
        var records = await Connect(youtrack).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        var items = records.OfType<SourceItem>().ToDictionary(i => i.Key!);

        youtrack.Requests.ShouldNotContain(r => r.Contains("{OLD}", StringComparison.Ordinal), "an archived project is read only when named");
        items.Keys.ShouldBe(["WEB-1", "WEB-2", "WEB-3"]);
        var bug = items["WEB-2"];
        bug.Id.ShouldBe("2-2");
        bug.Project.ShouldBe("0-1");
        bug.Type.ShouldBe("Bug");
        bug.Status.ShouldBe("Fixed");
        bug.Parent.ShouldBe("2-1");
        bug.Priority.ShouldBe("Critical");
        bug.Assignee.ShouldBe("1-1");
        bug.Creator.ShouldBe("1-2");
        bug.EstimateSeconds.ShouldBe(90 * 60);
        bug.Due.ShouldBe(new DateOnly(2025, 3, 20));
        bug.Labels.ShouldBe(["payments"]);
        bug.Sprint.ShouldBe("S2", "carried over: the later sprint");
        bug.Resolved.ShouldBe(T0.AddDays(2));
        bug.Url.ShouldBe("https://acme.youtrack.cloud/issue/WEB-2");
        bug.Description.ShouldBe("See ![](https://acme.youtrack.cloud/api/files/7-1?sign=ok)");
        bug.DescriptionMarkup.ShouldBe(MarkupKind.Markdown);
        bug.Fields["Component"].EnumerateArray().Select(e => e.GetString()).ShouldBe(["Api", "Web"]);
        bug.Fields["Budget"].GetDecimal().ShouldBe(1200);
        bug.Fields["Reviewer"].GetString().ShouldBe("1-2");
        bug.Fields[YouTrackConnector.SpentTimeField].GetDecimal().ShouldBe(0.5m);
        items["WEB-1"].Parent.ShouldBeNull();
        items["WEB-3"].Type.ShouldBe("Task");

        records.OfType<SourceSprint>().Select(s => (s.Id, s.Start, s.Closed)).ShouldBe([("S1", new DateOnly(2025, 3, 3), true), ("S2", new DateOnly(2025, 3, 17), false)]);
        records.OfType<SourceAttachment>().Single().ShouldSatisfyAllConditions(
            a => a.FileName.ShouldBe("shot.png"),
            a => a.Item.ShouldBe("2-2"),
            a => a.Url.ShouldBe("https://acme.youtrack.cloud/api/files/7-1?sign=ok"));
        records.OfType<SourceComment>().Select(c => (c.Id, c.Author, c.Body)).ShouldBe([("4-1", "1-1", "Reproduced")], "a deleted comment is left out");
        records.OfType<SourceRelation>().Select(r => (r.From, r.To, r.Kind)).ShouldBe(
            [("2-3", "2-2", RelationKinds.Blocks), ("2-2", "2-3", RelationKinds.Relates)], ignoreOrder: true, "each link once; the subtask link is the parent");

        var changes = records.OfType<SourceChange>().Where(c => c.Item == "2-2").ToList();
        changes.Select(c => (c.Field, c.From ?? c.FromText, c.To ?? c.ToText)).ShouldBe(
        [
            ("status", "Submitted", "Fixed"),
            ("assignee", null, "1-1"),
            ("title", "Old", "Pay twice"),
            ("estimateSeconds", "3600", "5400"),
            ("Component", null, "Api"),
        ], "paged in from activitiesPage");
        changes[1].ToText.ShouldBe("sofie");
        records.OfType<ReadCursor>().Single().Cursor.ShouldBe(T0.AddDays(3).UtcDateTime.ToString("O"));

        await using var file = await Connect(youtrack).FetchAttachmentAsync(records.OfType<SourceAttachment>().Single(), Ct);
        new StreamReader(file).ReadToEnd().ShouldBe("PNG!");
    }

    [Fact]
    public async Task A_delta_asks_for_a_day_early_and_keeps_what_changed_since_the_cursor()
    {
        var youtrack = Sample();

        var records = await Connect(youtrack).ReadAsync(new ReadScope { Projects = ["0-1"] }, T0.AddDays(2).AddHours(12).ToString("O"), Ct).ToListAsync(Ct);

        youtrack.Requests.ShouldContain(r => r.Contains("updated: 2025-03-02 .. Today", StringComparison.Ordinal));
        records.OfType<SourceItem>().Select(i => i.Key).ShouldBe(["WEB-2"]);
        records.OfType<SourceChange>().Select(c => c.Field).ShouldBe(["Component"], "only activities since the cursor");
    }

    [Fact]
    public async Task A_rejected_token_says_where_to_get_one()
    {
        var bad = new YouTrackConnector(new HttpClient(Sample()), "https://acme.youtrack.cloud", "perm:nope");

        (await Should.ThrowAsync<SourceAccessException>(() => bad.DescribeAsync(Ct))).Message.ShouldContain("permanent token");
    }

    [Fact]
    public async Task The_default_mapping_reads_resolved_states_by_name()
    {
        var stream = new MemoryStream();
        await Connect(Sample()).WriteSnapshotAsync(stream, new ReadScope(), null, includeFiles: false, TimeProvider.System, Ct);
        stream.Position = 0;
        await using var snapshot = await SnapshotReader.OpenAsync(stream, Ct);

        var mapping = ImportMappingDefaults.Propose(snapshot.Header, []);

        mapping.States.ShouldBe(new Dictionary<string, IssueState>
        {
            ["Submitted"] = IssueState.Todo,
            ["In Progress"] = IssueState.InProgress,
            ["Fixed"] = IssueState.Done,
            ["Duplicate"] = IssueState.Canceled,
            ["Obsolete"] = IssueState.Canceled,
        }, ignoreOrder: true);
        mapping.StatusesToReview.ShouldBeEmpty();
        mapping.Types["Bug"].ShouldBe(new TypeTarget(IssueType.Task, "bug"));
        mapping.Types["Epic"].Type.ShouldBe(IssueType.Epic);
        mapping.Projects["0-1"].Key.ShouldBe("WEB");
        new MappedSource(snapshot.Header, mapping, T0).Priority("Critical").ShouldBe(IssuePriority.Urgent);
    }

    private static YouTrackConnector Connect(FakeYouTrack youtrack)
    {
        var connector = new YouTrackConnector(new HttpClient(youtrack), youtrack.Site, Token, new FixedClock());
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        return connector;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => T0.AddDays(20);
    }

    /// <summary>
    /// WEB: WEB-1 (an Epic, parent of WEB-2), WEB-2 (a fixed Bug with every field, a file, comments, links and paged
    /// activities, in sprints S1 then S2), WEB-3 (an obsolete Task that WEB-2 depends on). OLD is archived.
    /// </summary>
    internal static FakeYouTrack Sample()
    {
        var yt = new FakeYouTrack();
        yt.Projects.Add(new JsonObject { ["id"] = "0-1", ["name"] = "Web", ["shortName"] = "WEB", ["archived"] = false });
        yt.Projects.Add(new JsonObject { ["id"] = "0-2", ["name"] = "Old stuff", ["shortName"] = "OLD", ["archived"] = true });
        yt.CustomFields["0-1"] =
        [
            ProjectField("Type", "enum[1]", false, ("Epic", null), ("Bug", null), ("Task", null)),
            ProjectField("State", "state[1]", false, ("Submitted", false), ("In Progress", false), ("Fixed", true), ("Duplicate", true), ("Obsolete", true)),
            ProjectField("Priority", "enum[1]", false, ("Critical", null), ("Normal", null)),
            ProjectField("Assignee", "user[1]"),
            ProjectField("Estimation", "period"),
            ProjectField("Spent time", "period"),
            ProjectField("Due Date", "date"),
            ProjectField("Component", "enum[*]", true, ("Api", null), ("Web", null)),
            ProjectField("Budget", "integer"),
            ProjectField("Reviewer", "user[1]"),
        ];
        yt.CustomFields["0-2"] = [ProjectField("State", "state[1]", false, ("Submitted", false))];
        yt.Users.Add(User("1-1", "sofie", "Sofie Larsen", "sofie@acme.example"));
        yt.Users.Add(User("1-2", "jonas", "Jonas Holm"));
        yt.Agiles.Add(new JsonObject
        {
            ["id"] = "A1",
            ["projects"] = new JsonArray(new JsonObject { ["id"] = "0-1" }),
            ["sprints"] = new JsonArray(new JsonObject { ["id"] = "S1", ["name"] = "Sprint 1" }, new JsonObject { ["id"] = "S2", ["name"] = "Sprint 2" }),
        });
        yt.Sprints["S1"] = new JsonObject { ["id"] = "S1", ["name"] = "Sprint 1", ["start"] = Ms(T0.AddDays(2)), ["finish"] = Ms(T0.AddDays(13)), ["archived"] = false, ["issues"] = new JsonArray(new JsonObject { ["id"] = "2-2" }) };
        yt.Sprints["S2"] = new JsonObject { ["id"] = "S2", ["name"] = "Sprint 2", ["start"] = Ms(T0.AddDays(16)), ["finish"] = Ms(T0.AddDays(27)), ["archived"] = false, ["issues"] = new JsonArray(new JsonObject { ["id"] = "2-2" }) };

        JsonObject Link(string direction, string name, bool directed, bool aggregation, string to) => new()
        {
            ["direction"] = direction,
            ["linkType"] = new JsonObject { ["name"] = name, ["directed"] = directed, ["aggregation"] = aggregation, ["sourceToTarget"] = name.ToLowerInvariant() },
            ["issues"] = new JsonArray(new JsonObject { ["id"] = to }),
        };

        JsonObject Issue(string id, int number, string summary, string type, string state, DateTimeOffset updated, params JsonObject[] fields) => new()
        {
            ["id"] = id,
            ["idReadable"] = $"WEB-{number}",
            ["summary"] = summary,
            ["created"] = Ms(T0),
            ["updated"] = Ms(updated),
            ["reporter"] = User("1-2", "jonas", "Jonas Holm"),
            ["project"] = new JsonObject { ["id"] = "0-1" },
            ["customFields"] = new JsonArray([Field("Type", Named(type)), Field("State", Named(state)), .. fields]),
            ["tags"] = new JsonArray(),
            ["links"] = new JsonArray(),
            ["comments"] = new JsonArray(),
            ["attachments"] = new JsonArray(),
        };

        var epic = Issue("2-1", 1, "Payments", "Epic", "In Progress", T0.AddDays(1));
        epic["links"] = new JsonArray(Link("OUTWARD", "Subtask", true, true, "2-2"));
        var bug = Issue("2-2", 2, "Pay twice", "Bug", "Fixed", T0.AddDays(3),
            Field("Priority", Named("Critical")),
            Field("Assignee", User("1-1", "sofie", "Sofie Larsen", "sofie@acme.example")),
            Field("Estimation", new JsonObject { ["minutes"] = 90 }),
            Field("Spent time", new JsonObject { ["minutes"] = 30 }),
            Field("Due Date", Ms(new DateTimeOffset(2025, 3, 20, 0, 0, 0, TimeSpan.Zero))),
            Field("Component", new JsonArray(Named("Api"), Named("Web"))),
            Field("Budget", 1200),
            Field("Reviewer", User("1-2", "jonas", "Jonas Holm")));
        bug["resolved"] = Ms(T0.AddDays(2));
        bug["description"] = "See ![](shot.png)";
        bug["tags"] = new JsonArray(Named("payments"));
        bug["links"] = new JsonArray(
            Link("INWARD", "Subtask", true, true, "2-1"),
            Link("OUTWARD", "Depend", true, false, "2-3"),
            Link("BOTH", "Relates", false, false, "2-3"));
        bug["comments"] = new JsonArray(
            new JsonObject { ["id"] = "4-1", ["text"] = "Reproduced", ["author"] = User("1-1", "sofie", "Sofie Larsen"), ["created"] = Ms(T0.AddDays(1)), ["deleted"] = false },
            new JsonObject { ["id"] = "4-2", ["text"] = "Oops", ["author"] = User("1-1", "sofie", "Sofie Larsen"), ["created"] = Ms(T0.AddDays(1)), ["deleted"] = true });
        bug["attachments"] = new JsonArray(new JsonObject
        {
            ["id"] = "7-1",
            ["name"] = "shot.png",
            ["mimeType"] = "image/png",
            ["size"] = 4,
            ["url"] = "/api/files/7-1?sign=ok",
            ["author"] = User("1-1", "sofie", "Sofie Larsen"),
            ["created"] = Ms(T0.AddHours(1)),
            ["removed"] = false,
        });
        yt.Files["7-1"] = "PNG!"u8.ToArray();
        var task = Issue("2-3", 3, "Pay by fax", "Task", "Obsolete", T0.AddDays(1));
        task["links"] = new JsonArray(Link("INWARD", "Depend", true, false, "2-2"), Link("BOTH", "Relates", false, false, "2-2"));
        yt.Issues.AddRange([("WEB", epic), ("WEB", bug), ("WEB", task)]);

        JsonObject Activity(string id, DateTimeOffset at, string? field, JsonNode? removed, JsonNode? added, string category = "CustomFieldCategory") => new()
        {
            ["id"] = id,
            ["timestamp"] = Ms(at),
            ["author"] = User("1-2", "jonas", "Jonas Holm"),
            ["field"] = field is null ? null : new JsonObject { ["presentation"] = field },
            ["category"] = new JsonObject { ["id"] = category },
            ["removed"] = removed,
            ["added"] = added,
        };

        yt.Activities["2-2"] =
        [
            Activity("9-1", T0.AddHours(2), "State", new JsonArray(Named("Submitted")), new JsonArray(Named("Fixed"))),
            Activity("9-2", T0.AddHours(3), "Assignee", new JsonArray(), new JsonArray(User("1-1", "sofie", "Sofie Larsen"))),
            Activity("9-3", T0.AddHours(4), "summary", "Old", "Pay twice", "SummaryCategory"),
            Activity("9-4", T0.AddHours(5), "Estimation", 60, 90),
            Activity("9-5", T0.AddDays(3), "Component", new JsonArray(), new JsonArray(Named("Api"))),
        ];
        return yt;
    }
}
