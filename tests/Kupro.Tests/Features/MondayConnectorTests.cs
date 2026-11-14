using System.Text.Json.Nodes;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.Monday;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>
/// The Monday connector (task 027) against <see cref="FakeMonday"/>: Describe with the columns each board's items are
/// read through, groups, items and subitems, updates and files, the activity log as changes, deltas, complexity
/// limits, and Monday's default mapping.
/// </summary>
public sealed class MondayConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Describe_reports_boards_status_labels_and_the_columns_left_as_fields()
    {
        var description = await Connect(Sample()).DescribeAsync(Ct);

        description.Source.ShouldBe(new SnapshotSource { Kind = SourceKinds.Monday, BaseUrl = "https://acme.monday.com" });
        description.Projects.Select(p => (p.Id, p.Name)).ShouldBe([("10", "Website"), ("11", "Old")], "a subitems board is not a project");
        description.Statuses.Select(s => (s.Id, s.Category)).ShouldBe(
            [(MondayConnector.NoStatus, null), ("Working on it", null), ("Done", "done"), ("Stuck", null), ("Open", null), ("Closed", "done")]);
        description.Fields.Select(f => (f.Id, f.Kind)).ShouldBe(
            [(MondayConnector.GroupField, FieldKind.Select), ("budget:number", FieldKind.Number), ("notes:text", FieldKind.Text)]);
        description.People.Select(p => (p.Id, p.Name)).ShouldBe([("5", "Sofie Larsen"), ("6", "Jonas Holm")]);
    }

    [Fact]
    public async Task Read_maps_groups_items_subitems_updates_files_and_the_activity_log()
    {
        var monday = Sample();
        monday.ComplexityOnce = true;
        var waits = new List<TimeSpan>();
        var clock = new Clock();
        var connector = new MondayConnector(new HttpClient(monday), FakeMonday.Token, clock);
        connector.Client.Delay = (wait, _) =>
        {
            waits.Add(wait);
            clock.Now += wait;
            return Task.CompletedTask;
        };

        var records = await connector.ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        var items = records.OfType<SourceItem>().ToDictionary(i => i.Id);

        waits.ShouldBe([TimeSpan.FromSeconds(7)], "the complexity budget's retry_in_seconds");
        monday.Queries.ShouldContain(q => q.Contains("next_items_page", StringComparison.Ordinal));
        items.Keys.ShouldBe(["10:topics", "10:later", "100", "101", "102"], ignoreOrder: true, "the archived board is read only when named");
        items["10:topics"].ShouldSatisfyAllConditions(g => g.Type.ShouldBe(MondayConnector.GroupType), g => g.Title.ShouldBe("This week"));

        var page = items["100"];
        page.Key.ShouldBeNull("Kupro numbers Monday items; the URL is the alias");
        page.Url.ShouldBe("https://acme.monday.com/boards/10/pulses/100");
        page.Type.ShouldBe(MondayConnector.ItemType);
        page.Parent.ShouldBe("10:topics");
        page.Status.ShouldBe("Working on it");
        page.Assignee.ShouldBe("5");
        page.Creator.ShouldBe("6");
        page.Start.ShouldBe(new DateOnly(2025, 3, 3));
        page.Due.ShouldBe(new DateOnly(2025, 3, 10));
        page.EstimateSeconds.ShouldBe(4 * 3600);
        page.Labels.ShouldBe(["web", "seo"]);
        page.Fields[MondayConnector.GroupField].GetString().ShouldBe("This week");
        page.Fields["budget:number"].GetDecimal().ShouldBe(1200);
        page.Fields["notes:text"].GetString().ShouldBe("Ask Jonas");
        page.Resolved.ShouldBeNull();

        var sub = items["101"];
        sub.ShouldSatisfyAllConditions(
            s => s.Type.ShouldBe(MondayConnector.SubitemType),
            s => s.Parent.ShouldBe("100"),
            s => s.Project.ShouldBe("10"),
            s => s.Status.ShouldBe("Closed", "from the subitems board's own status column"));
        items["102"].Resolved.ShouldBe(T0.AddDays(1));
        items["102"].Due.ShouldBe(new DateOnly(2025, 3, 31), "no timeline: the date column");

        records.OfType<SourceComment>().Select(c => (c.Id, c.Item, c.Author, c.Markup)).ShouldBe(
            [("u1", "100", "5", MarkupKind.Html), ("r1", "100", "6", MarkupKind.Html)]);
        records.OfType<SourceAttachment>().Single().ShouldSatisfyAllConditions(a => a.Id.ShouldBe("a1"), a => a.FileName.ShouldBe("mock.png"), a => a.Item.ShouldBe("100"));
        records.OfType<SourceChange>().Select(c => (c.Item, c.Field, c.From, c.To)).ShouldBe(
            [("100", "status", "Done", "Working on it"), ("102", "title", "Price", "Pricing")], ignoreOrder: true, "an item not read is left out");

        await using var file = await connector.FetchAttachmentAsync(records.OfType<SourceAttachment>().Single(), Ct);
        new StreamReader(file).ReadToEnd().ShouldBe("PNG!");
    }

    [Fact]
    public async Task A_delta_keeps_what_changed_since_the_cursor()
    {
        var records = await Connect(Sample()).ReadAsync(new ReadScope { Projects = ["10"] }, T0.AddDays(2).ToString("O"), Ct).ToListAsync(Ct);

        records.OfType<SourceItem>().Select(i => i.Id).ShouldBe(["100", "101"], ignoreOrder: true, "groups and #102 didn't change");
        records.OfType<SourceChange>().Select(c => c.Item).ShouldBe(["100"]);
    }

    [Fact]
    public async Task A_rejected_token_says_where_to_get_one()
    {
        var bad = new MondayConnector(new HttpClient(Sample()), "nope");

        (await Should.ThrowAsync<SourceAccessException>(() => bad.DescribeAsync(Ct))).Message.ShouldContain("personal API token");
    }

    [Fact]
    public async Task The_default_mapping_makes_groups_epics_subitems_checklists_and_reads_done_labels()
    {
        var stream = new MemoryStream();
        await Connect(Sample()).WriteSnapshotAsync(stream, new ReadScope(), null, includeFiles: false, TimeProvider.System, Ct);
        stream.Position = 0;
        await using var snapshot = await SnapshotReader.OpenAsync(stream, Ct);

        var mapping = ImportMappingDefaults.Propose(snapshot.Header, []);

        mapping.Types[MondayConnector.GroupType].Type.ShouldBe(IssueType.Epic);
        mapping.Types[MondayConnector.SubitemType].Checklist.ShouldBeTrue();
        mapping.Types[MondayConnector.ItemType].Type.ShouldBe(IssueType.Task);
        mapping.States["Done"].ShouldBe(IssueState.Done);
        mapping.States["Closed"].ShouldBe(IssueState.Done);
        mapping.States["Working on it"].ShouldBe(IssueState.InProgress);
        mapping.States["Stuck"].ShouldBe(IssueState.InProgress);
        mapping.States[MondayConnector.NoStatus].ShouldBe(IssueState.Todo);
        mapping.StatusesToReview.ShouldNotContain("Done");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0.AddDays(10);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static MondayConnector Connect(FakeMonday monday)
    {
        var connector = new MondayConnector(new HttpClient(monday), FakeMonday.Token);
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        return connector;
    }

    private static JsonObject Column(string id, string title, string type, string? settings = null) =>
        new() { ["id"] = id, ["title"] = title, ["type"] = type, ["settings_str"] = settings };

    private static JsonObject Value(string id, string type, string? text, string? value = null) =>
        new() { ["id"] = id, ["type"] = type, ["text"] = text, ["value"] = value };

    private static string At(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Website (10): groups "This week" and "Later"; #100 in progress with every mapped column, an update with a reply,
    /// a file and subitem #101; #102 done, renamed. Old (11) is archived; 12 is Website's subitems board.
    /// </summary>
    internal static FakeMonday Sample()
    {
        var monday = new FakeMonday();
        monday.Boards.Add(new JsonObject
        {
            ["id"] = "10",
            ["name"] = "Website",
            ["state"] = "active",
            ["type"] = "board",
            ["columns"] = new JsonArray(
                Column("name", "Name", "name"),
                Column("status", "Status", "status", """{"labels":{"0":"Working on it","1":"Done","2":"Stuck","5":""},"done_colors":[1]}"""),
                Column("person", "Owner", "people"),
                Column("timeline", "Timeline", "timeline"),
                Column("date4", "Deadline", "date"),
                Column("estimate", "Estimate (h)", "numbers"),
                Column("numbers2", "Budget", "numbers"),
                Column("tags", "Tags", "tags"),
                Column("text", "Notes", "text"),
                Column("subitems", "Subitems", "subtasks")),
            ["groups"] = new JsonArray(new JsonObject { ["id"] = "topics", ["title"] = "This week" }, new JsonObject { ["id"] = "later", ["title"] = "Later" }),
        });
        monday.Boards.Add(new JsonObject { ["id"] = "11", ["name"] = "Old", ["state"] = "archived", ["type"] = "board", ["columns"] = new JsonArray(), ["groups"] = new JsonArray() });
        monday.Boards.Add(new JsonObject
        {
            ["id"] = "12",
            ["name"] = "Subitems of Website",
            ["state"] = "active",
            ["type"] = "sub_items_board",
            ["columns"] = new JsonArray(Column("name", "Name", "name"), Column("status", "Status", "status", """{"labels":{"0":"Open","1":"Closed"},"done_colors":[1]}""")),
            ["groups"] = new JsonArray(),
        });
        monday.Boards.Add(new JsonObject { ["id"] = "13", ["name"] = "Gone", ["state"] = "deleted", ["type"] = "board", ["columns"] = new JsonArray(), ["groups"] = new JsonArray() });
        monday.Users.Add(new JsonObject { ["id"] = 5, ["name"] = "Sofie Larsen", ["email"] = "sofie@acme.example" });
        monday.Users.Add(new JsonObject { ["id"] = 6, ["name"] = "Jonas Holm", ["email"] = "jonas@acme.example" });

        monday.Items["10"] =
        [
            new JsonObject
            {
                ["id"] = "100",
                ["name"] = "Landing page",
                ["created_at"] = At(T0),
                ["updated_at"] = At(T0.AddDays(3)),
                ["state"] = "active",
                ["creator_id"] = "6",
                ["group"] = new JsonObject { ["id"] = "topics" },
                ["column_values"] = new JsonArray(
                    Value("status", "status", "Working on it", """{"index":0}"""),
                    Value("person", "people", "Sofie Larsen", """{"personsAndTeams":[{"id":5,"kind":"person"}]}"""),
                    Value("timeline", "timeline", "2025-03-03 - 2025-03-10", """{"from":"2025-03-03","to":"2025-03-10"}"""),
                    Value("estimate", "numbers", "4", "\"4\""),
                    Value("numbers2", "numbers", "1200", "\"1200\""),
                    Value("tags", "tags", "web, seo"),
                    Value("text", "text", "Ask Jonas", "\"Ask Jonas\"")),
                ["subitems"] = new JsonArray(new JsonObject
                {
                    ["id"] = "101",
                    ["name"] = "Hero",
                    ["created_at"] = At(T0.AddDays(1)),
                    ["updated_at"] = At(T0.AddDays(2).AddHours(1)),
                    ["creator_id"] = "5",
                    ["board"] = new JsonObject { ["id"] = "12" },
                    ["column_values"] = new JsonArray(Value("status", "status", "Closed", """{"index":1}""")),
                }),
                ["updates"] = new JsonArray(new JsonObject
                {
                    ["id"] = "u1",
                    ["body"] = "<p>Started</p>",
                    ["created_at"] = At(T0.AddHours(2)),
                    ["creator_id"] = 5,
                    ["replies"] = new JsonArray(new JsonObject { ["id"] = "r1", ["body"] = "<p>Nice</p>", ["created_at"] = At(T0.AddHours(3)), ["creator_id"] = 6 }),
                }),
                ["assets"] = new JsonArray(new JsonObject { ["id"] = "a1", ["name"] = "mock.png", ["file_size"] = 4, ["created_at"] = At(T0.AddHours(1)), ["uploaded_by"] = new JsonObject { ["id"] = 5 } }),
            },
            new JsonObject
            {
                ["id"] = "102",
                ["name"] = "Pricing",
                ["created_at"] = At(T0),
                ["updated_at"] = At(T0.AddDays(1)),
                ["creator_id"] = "5",
                ["group"] = new JsonObject { ["id"] = "later" },
                ["column_values"] = new JsonArray(Value("status", "status", "Done", """{"index":1}"""), Value("date4", "date", "2025-03-31", """{"date":"2025-03-31"}""")),
            },
        ];
        monday.Files["a1"] = "PNG!"u8.ToArray();

        static string Ticks(DateTimeOffset at) => ((at - DateTimeOffset.UnixEpoch).Ticks).ToString(System.Globalization.CultureInfo.InvariantCulture);
        monday.Logs["10"] =
        [
            new JsonObject
            {
                ["id"] = "l1",
                ["event"] = "update_column_value",
                ["user_id"] = "5",
                ["created_at"] = Ticks(T0.AddDays(2).AddHours(2)),
                ["data"] = """{"pulse_id":100,"column_id":"status","previous_value":{"label":{"text":"Done"}},"value":{"label":{"text":"Working on it"}}}""",
            },
            new JsonObject
            {
                ["id"] = "l2",
                ["event"] = "update_name",
                ["user_id"] = "6",
                ["created_at"] = Ticks(T0.AddHours(5)),
                ["data"] = """{"pulse_id":102,"previous_value":"Price","value":"Pricing"}""",
            },
            new JsonObject
            {
                ["id"] = "l3",
                ["event"] = "update_name",
                ["user_id"] = "6",
                ["created_at"] = Ticks(T0.AddHours(6)),
                ["data"] = """{"pulse_id":999,"previous_value":"A","value":"B"}""",
            },
        ];
        return monday;
    }
}
