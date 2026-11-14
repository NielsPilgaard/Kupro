using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.Jira;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>
/// The Jira connector (task 024) against <see cref="FakeJira"/>, Cloud and Data Center: Describe, the read into snapshot
/// records, attachments pointed at from markup, Epic Link and sprint parsing, changelog and comment paging, delta
/// cursors, throttling, where the token may go, and Jira's default mapping.
/// </summary>
public sealed class JiraConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static JsonObject Sofie(bool cloud = true) => FakeJira.User(cloud ? "acc-sofie" : "JIRAUSER10001", "Sofie Larsen", cloud ? null : "sofie@acme.example", cloud);

    private static JsonObject Jonas(bool cloud = true) => FakeJira.User(cloud ? "acc-jonas" : "JIRAUSER10002", "Jonas Holm", "jonas@acme.example", cloud);

    [Fact]
    public async Task Describe_reports_projects_types_with_levels_statuses_and_fields()
    {
        var description = await Connect(Cloud()).DescribeAsync(Ct);

        description.Source.ShouldBe(new SnapshotSource { Kind = SourceKinds.Jira, BaseUrl = "https://acme.atlassian.net" });
        description.Projects.Select(p => (p.Key, p.Name, p.Process)).ShouldBe(
            [("WEB", "Web", "software"), ("MOB", "Mobile", "software"), ("HELP", "Help desk", "service_desk")]);
        description.Projects[0].Url.ShouldBe("https://acme.atlassian.net/browse/WEB");

        var types = description.ItemTypes.ToDictionary(t => t.Id);
        types["10005"].Level.ShouldBe(2, "Initiative sits above Epic");
        types["10001"].Level.ShouldBe(1);
        types["10003"].Level.ShouldBe(-1);
        types["10002"].Project.ShouldBe("10000");
        types["10100"].Project.ShouldBe("10001", "a team-managed project has its own types");

        description.Statuses.Single(s => s.Id == "4").ShouldBe(new SourceStatus { Id = "4", Name = "QA", Category = "indeterminate", Project = "10000" });
        description.Statuses.Single(s => s.Id == "2").Project.ShouldBeNull("To Do is used by every project");

        description.Fields.Select(f => (f.Id, f.Kind)).ShouldBe(
        [
            ("components", FieldKind.MultiSelect), ("resolution", FieldKind.Select), ("timespent", FieldKind.Number),
            ("customfield_10050", FieldKind.Select), ("customfield_10051", FieldKind.User), ("customfield_10052", FieldKind.Text),
            ("customfield_10053", FieldKind.Number), ("customfield_10054", FieldKind.Date),
        ], ignoreOrder: true);
        description.Fields.Single(f => f.Id == "resolution").Options.ShouldBe(["Done", "Won't Do", "Duplicate", "Cannot Reproduce"], "paged in from resolution/search");
    }

    [Fact]
    public async Task Cloud_read_maps_issues_with_hierarchy_effort_dates_sprints_and_markup()
    {
        var jira = Cloud();
        var records = await Connect(jira).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        var items = records.OfType<SourceItem>().ToDictionary(i => i.Key!);

        items.Keys.ShouldBe(["WEB-1", "WEB-2", "WEB-3", "WEB-4", "WEB-5", "WEB-6", "MOB-1", "HELP-1"], ignoreOrder: true);
        items["WEB-2"].Parent.ShouldBe("10010");
        items["WEB-4"].Parent.ShouldBe("10012");

        var story = items["WEB-3"];
        story.Id.ShouldBe("10012");
        story.Type.ShouldBe("10002");
        story.Status.ShouldBe("4");
        story.Parent.ShouldBe("10011");
        story.Assignee.ShouldBe("acc-sofie");
        story.Creator.ShouldBe("acc-jonas");
        story.Priority.ShouldBe("High");
        story.Labels.ShouldBe(["payments", "mobile"]);
        story.EstimateSeconds.ShouldBe(4 * 3600);
        story.Points.ShouldBe(5);
        story.Start.ShouldBe(new DateOnly(2025, 3, 3));
        story.Due.ShouldBe(new DateOnly(2025, 3, 20));
        story.Sprint.ShouldBe("12", "the active sprint, not the closed one it came from");
        story.Versions.ShouldBe(["20001"]);
        story.Url.ShouldBe("https://acme.atlassian.net/browse/WEB-3");
        story.Updated.ShouldBe(T0.AddDays(3));
        story.Fields["components"].EnumerateArray().Select(e => e.GetString()).ShouldBe(["Frontend"]);
        story.Fields["timespent"].GetDecimal().ShouldBe(1.5m);
        story.Fields["customfield_10050"].GetString().ShouldBe("Blue");
        story.Fields["customfield_10051"].GetString().ShouldBe("acc-jonas");
        story.Fields["customfield_10052"].GetString().ShouldBe("Ship it");
        story.Fields["customfield_10053"].GetDecimal().ShouldBe(1200.5m);
        story.Fields["customfield_10054"].GetString().ShouldBe("2025-05-01");
        items["WEB-5"].Fields["resolution"].GetString().ShouldBe("Won't Do");
        story.Links.ShouldBe(
        [
            new SourceLink { Url = "https://acme.atlassian.net/wiki/spaces/PAY/pages/123", Title = "Payments design" },
            new SourceLink { Url = "https://stripe.com/docs/testing" },
        ], "web and Confluence links; a script URL is dropped");
        items["WEB-4"].Links.ShouldBeEmpty();
        items["WEB-4"].Resolved.ShouldBe(T0.AddDays(5));

        // ADF stays ADF; the image it shows by name now carries the attachment's content URL, so the import re-hosts it.
        story.DescriptionMarkup.ShouldBe(MarkupKind.Adf);
        var media = JsonNode.Parse(story.Description!)!["content"]![1]!["content"]![0]!["attrs"]!;
        media["url"]!.GetValue<string>().ShouldBe("https://acme.atlassian.net/rest/api/3/attachment/content/30001");
        media["mimeType"]!.GetValue<string>().ShouldBe("image/png");
        MarkupConverters.ToMarkdown(story.Description, MarkupKind.Adf)!.ShouldContain("![flow.png](https://acme.atlassian.net/rest/api/3/attachment/content/30001)");

        records.OfType<SourceAttachment>().Single().ShouldBe(new SourceAttachment
        {
            Id = "30001",
            Item = "10012",
            FileName = "flow.png",
            ContentType = "image/png",
            Size = 4,
            Author = "acc-sofie",
            Created = T0.AddHours(2),
            Url = "https://acme.atlassian.net/rest/api/3/attachment/content/30001",
        });

        // Each link once, outward: Blocks and Duplicate keep their meaning, Relates, Cloners and custom types relate.
        records.OfType<SourceRelation>().Select(r => (r.From, r.To, r.Kind, r.Name)).ShouldBe(
        [
            ("10012", "10013", RelationKinds.Blocks, "blocks"),
            ("10014", "10012", RelationKinds.Relates, "relates to"),
            ("10012", "10014", RelationKinds.Relates, "clones"),
            ("10012", "10013", RelationKinds.Relates, "depends on"),
            ("10014", "10015", RelationKinds.Duplicates, "duplicates"),
        ], ignoreOrder: true);

        records.OfType<SourceSprint>().Select(s => (s.Id, s.Name, s.Start, s.End, s.Closed)).ShouldBe(
            [("12", "Sprint 2", new DateOnly(2025, 3, 17), new DateOnly(2025, 3, 28), false)]);
        records.OfType<SourceVersion>().Select(v => (v.Id, v.Project, v.Name, v.Date, v.Released)).ShouldBe(
            [("20001", "10000", "1.0", new DateOnly(2025, 4, 1), false), ("20002", "10000", "0.9", null, true)]);

        var people = records.OfType<SourcePerson>().ToDictionary(p => p.Id);
        people["acc-jonas"].Email.ShouldBe("jonas@acme.example");
        people["acc-sofie"].Email.ShouldBeNull("Cloud hides it; the People step matches by name");
        people["acc-bot"].Bot.ShouldBeTrue();
        records.IndexOf(people["acc-sofie"]).ShouldBeLessThan(records.IndexOf(story), "people come before the records that name them");
    }

    [Fact]
    public async Task Long_changelogs_and_comment_lists_are_paged_in_and_replayed_as_changes()
    {
        var jira = Cloud();
        var records = await Connect(jira).ReadAsync(new ReadScope { Projects = ["10000"] }, null, Ct).ToListAsync(Ct);

        jira.Requests.ShouldContain(r => r.StartsWith("GET rest/api/3/issue/10012/changelog?startAt=0", StringComparison.Ordinal));
        jira.Requests.ShouldContain(r => r.StartsWith("GET rest/api/3/issue/10012/comment?startAt=0", StringComparison.Ordinal));
        jira.Requests.ShouldNotContain(r => r.Contains("issue/10013/changelog", StringComparison.Ordinal) || r.Contains("issue/10013/comment", StringComparison.Ordinal), "a short changelog is complete in the search");

        var comments = records.OfType<SourceComment>().Where(c => c.Item == "10012").ToList();
        comments.Select(c => (c.Id, c.Author)).ShouldBe([("50001", "acc-jonas"), ("50002", "acc-sofie"), ("50003", "acc-sofie")]);
        comments[2].Markup.ShouldBe(MarkupKind.Adf);
        comments[2].Body.ShouldContain("content/30001", Case.Sensitive, "an image in a comment is re-hosted too");

        var changes = records.OfType<SourceChange>().Where(c => c.Item == "10012").ToList();
        changes.Select(c => (c.Id, c.Field, c.From, c.To)).ShouldBe(
        [
            ("70001:0", "status", "2", "3"),
            ("70001:1", "assignee", null, "acc-sofie"),
            ("70001:2", "sprint", null, "11"),
            ("70002:0", "priority", "Medium", "High"),
            ("70002:1", "sprint", "11", "12"),
            ("70002:2", "customfield_10050", null, "Blue"),
            ("70003:0", "status", "3", "4"),
            ("70004:0", "description", null, null),
            ("70004:1", "Fix Version", null, "20001"),
        ]);
        changes[0].Actor.ShouldBe("acc-jonas");
        changes[0].At.ShouldBe(T0.AddDays(1));
        changes[0].ToText.ShouldBe("In Progress");
        changes[8].ToText.ShouldBe("1.0");
    }

    [Fact]
    public async Task Data_center_reads_wiki_markup_epic_links_and_sprint_strings()
    {
        var jira = DataCenter();
        var records = await Connect(jira).ReadAsync(new ReadScope { Projects = ["10000"] }, null, Ct).ToListAsync(Ct);
        var items = records.OfType<SourceItem>().ToDictionary(i => i.Key!);

        items.Keys.ShouldBe(["OPS-1", "OPS-2", "OPS-3", "OPS-4"], ignoreOrder: true);
        items["OPS-2"].Parent.ShouldBe("10100", "the Epic Link names the epic by key");
        items["OPS-3"].Parent.ShouldBe("10101");
        items["OPS-4"].Parent.ShouldBe("10200", "an epic outside the read is looked up once");
        jira.Requests.Count(r => r.StartsWith("GET rest/api/2/issue/ARC-1", StringComparison.Ordinal)).ShouldBe(1);
        jira.Requests.ShouldNotContain(r => r.StartsWith("GET rest/api/2/issue/OPS-1", StringComparison.Ordinal));

        var task = items["OPS-2"];
        task.Points.ShouldBe(3);
        task.Assignee.ShouldBe("JIRAUSER10001");
        task.Sprint.ShouldBe("7");
        task.DescriptionMarkup.ShouldBe(MarkupKind.JiraWiki);
        task.Description.ShouldBe(
            "See !https://jira.acme.example/jira/secure/attachment/60001/screen%20shot.png|thumbnail! and "
            + "[log.txt|https://jira.acme.example/jira/secure/attachment/60002/log.txt]. Ping [~sofie].");

        // The old sprint string has no dates, so the Agile API fills them in.
        records.OfType<SourceSprint>().Single().ShouldBe(new SourceSprint
        {
            Id = "7",
            Name = "Sprint 7, the big one",
            Start = new DateOnly(2025, 3, 10),
            End = new DateOnly(2025, 3, 21),
            Closed = false,
        });

        var sofie = records.OfType<SourcePerson>().Single(p => p.Id == "JIRAUSER10001");
        sofie.Handle.ShouldBe("sofie", "wiki mentions use the user name");
        sofie.Email.ShouldBe("sofie@acme.example");

        // Older servers name changelog fields only; the full changelog comes from the issue itself.
        jira.Requests.ShouldContain(r => r.StartsWith("GET rest/api/2/issue/10101?", StringComparison.Ordinal) && r.Contains("expand=changelog", StringComparison.Ordinal));
        records.OfType<SourceChange>().Where(c => c.Item == "10101").Select(c => (c.Field, c.From, c.To)).ShouldBe(
            [("status", "1", "3"), ("parent", null, "10100"), ("points", "2", "3")]);
        jira.Requests.ShouldAllBe(r => !r.StartsWith("GET rest/api/3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delta_reads_what_changed_since_the_cursor_and_moves_it_on()
    {
        var jira = Cloud();
        jira.Now = T0.AddDays(5);
        var first = await Connect(jira).ReadAsync(new ReadScope { Projects = ["10000"] }, null, Ct).ToListAsync(Ct);
        var cursor = first.OfType<ReadCursor>().Single().Cursor;
        cursor.ShouldBe(T0.AddDays(5).UtcDateTime.ToString("O"), "when the read started");

        // An edit in Jira after the first run, timed inside the overlap window the pipeline subtracts.
        jira.Now = T0.AddDays(5).AddMinutes(30);
        jira.Issues.Single(i => i.Key == "WEB-6").Touch(T0.AddDays(5).AddMinutes(-4));
        var overlapped = ImportSources.WithOverlap(cursor);
        jira.Requests.Clear();
        var second = await Connect(jira).ReadAsync(new ReadScope { Projects = ["10000"] }, overlapped, Ct).ToListAsync(Ct);

        jira.Requests.ShouldContain(r => r.Contains("AND updated >= \"2025-03-06 09:49\"", StringComparison.Ordinal),
            "the overlapped cursor less a minute, in the user's zone (CET), fixed for every page");
        second.OfType<SourceItem>().Select(i => i.Key).ShouldBe(["WEB-4", "WEB-6"], ignoreOrder: true);
        second.OfType<ReadCursor>().Single().Cursor.ShouldBe(jira.Now.UtcDateTime.ToString("O"), "when this read started, not the overlapped cursor");
    }

    [Fact]
    public async Task A_429_with_retry_after_pauses_the_read_and_resumes_it()
    {
        var jira = Cloud();
        jira.Throttle = 2;
        var waits = new List<TimeSpan>();
        var connector = Connect(jira);
        connector.Client.Delay = (wait, _) =>
        {
            waits.Add(wait);
            jira.Now += wait;
            return Task.CompletedTask;
        };

        var description = await connector.DescribeAsync(Ct);

        description.Projects.Count.ShouldBe(3);
        waits.Count.ShouldBe(2);
        waits.ShouldAllBe(w => w > TimeSpan.FromSeconds(6) && w <= TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Cloud_without_an_email_and_a_rejected_token_ask_for_a_new_one()
    {
        var cloud = await Should.ThrowAsync<SourceAccessException>(() => Connect(Cloud(), token: "just-a-token").DescribeAsync(Ct));
        cloud.TokenRejected.ShouldBeTrue();
        cloud.Message.ShouldContain("email");

        var dc = await Should.ThrowAsync<SourceAccessException>(() => Connect(DataCenter(), token: "wrong-token").DescribeAsync(Ct));
        dc.TokenRejected.ShouldBeTrue();
        dc.Message.ShouldNotContain("wrong-token");
    }

    [Fact]
    public async Task Attachments_download_with_the_token_and_never_from_another_host()
    {
        var jira = Cloud();
        var connector = Connect(jira);
        var file = new SourceAttachment { Id = "30001", Item = "10012", FileName = "flow.png", Created = T0, Url = jira.ContentUrl("30001", "flow.png") };
        await using (var stream = await connector.FetchAttachmentAsync(file, Ct))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            copy.ToArray().ShouldBe("PNG!"u8.ToArray());
        }

        await Should.ThrowAsync<SourceAccessException>(() => connector.FetchAttachmentAsync(file with { Url = "https://evil.example/rest/api/3/attachment/content/1" }, Ct));
        jira.ForeignHosts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://acme.atlassian.net", "https://acme.atlassian.net")]
    [InlineData(" https://acme.atlassian.net/browse/WEB-3 ", "https://acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/WEB/boards/1", "https://acme.atlassian.net")]
    [InlineData("https://jira.acme.example/jira/browse/OPS-2", "https://jira.acme.example/jira")]
    [InlineData("https://jira.acme.example/secure/Dashboard.jspa", "https://jira.acme.example/")]
    [InlineData("http://jira:8080/", "http://jira:8080/")]
    public void Pasted_urls_normalize_to_the_site(string pasted, string site) =>
        JiraConnector.NormalizeUrl(pasted).AbsoluteUri.ShouldBe(new Uri(site).AbsoluteUri);

    [Fact]
    public async Task A_read_writes_a_snapshot_and_the_default_mapping_follows_jira()
    {
        var zip = new MemoryStream();
        var jira = Cloud();
        await Connect(jira).WriteSnapshotAsync(zip, new ReadScope(), null, includeFiles: true, new Clock(jira), Ct);
        zip.Position = 0;
        await using var snapshot = await SnapshotReader.OpenAsync(zip, Ct);

        snapshot.Header.Source.Kind.ShouldBe(SourceKinds.Jira);
        snapshot.Header.Counts.Items.ShouldBe(8);
        (await snapshot.ReadAttachmentsAsync(Ct).SingleAsync(Ct)).File.ShouldNotBeNull();

        var mapping = ImportMappingDefaults.Propose(snapshot.Header, [new MemberCandidate(Guid.NewGuid(), "Sofie Larsen", null)]);

        mapping.Types["10001"].ShouldBe(new TypeTarget(IssueType.Epic));
        mapping.Types["10002"].ShouldBe(new TypeTarget(IssueType.Task));
        mapping.Types["10003"].ShouldBe(new TypeTarget(IssueType.Task));
        mapping.Types["10004"].ShouldBe(new TypeTarget(IssueType.Task, "bug"));
        mapping.Types["10005"].ShouldBe(new TypeTarget(IssueType.Epic));
        mapping.Types["10006"].ShouldBe(new TypeTarget(IssueType.Task), "New Feature is a Task like Story");
        mapping.Fold.ShouldBe(FoldEnd.Top, "Initiatives fold, Epics stay Epics");
        mapping.Scope.SkipTypes.ShouldBe(["10200"], "Service Management request types are listed but unticked");
        mapping.States.ShouldBe(new Dictionary<string, IssueState>
        {
            ["1"] = IssueState.Backlog,
            ["2"] = IssueState.Todo,
            ["3"] = IssueState.InProgress,
            ["4"] = IssueState.InReview,
            ["5"] = IssueState.Done,
            ["6"] = IssueState.Canceled,
        }, ignoreOrder: true);
        mapping.LabelFields.ShouldBe(["components"]);
        mapping.CanceledValues["resolution"].ShouldBe(["Won't Do", "Duplicate"]);
        mapping.MilestoneVersions.ShouldBe(["20001"]);

        // Done is Done, unless the resolution says the work was dropped.
        var source = new MappedSource(snapshot.Header, mapping, jira.Now);
        SourceItem Resolved(string? resolution) => new()
        {
            Id = "1",
            Project = "10000",
            Type = "10002",
            Status = "5",
            Title = "Done",
            Created = T0,
            Updated = T0,
            Fields = resolution is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement> { ["resolution"] = FieldValue.Text(resolution) },
        };
        source.State(Resolved("Done")).ShouldBe(IssueState.Done);
        source.State(Resolved(null)).ShouldBe(IssueState.Done);
        source.State(Resolved("duplicate")).ShouldBe(IssueState.Canceled);
        source.State(Resolved("Won't Do") with { Status = "3" }).ShouldBe(IssueState.InProgress, "only a Done item cancels");
        mapping.People["acc-sofie"].Action.ShouldBe(PersonAction.Member, "no email: matched by name");
        mapping.People["acc-bot"].Action.ShouldBe(PersonAction.Skip);
    }

    private static JiraConnector Connect(FakeJira jira, string? token = null)
    {
        var connector = new JiraConnector(jira.Client(), jira.SiteUrl, token ?? (jira.Cloud ? FakeJira.CloudToken : FakeJira.DataCenterToken), new Clock(jira));
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        return connector;
    }

    private static void Statuses(FakeJira jira, string project, string type, string name, bool subtask, params string[] statuses)
    {
        string[] names = ["", "Backlog", "To Do", "In Progress", "QA", "Done", "Won't Do"];
        string[] categories = ["", "new", "new", "indeterminate", "indeterminate", "done", "done"];
        jira.Workflows[project].Add(new JsonObject
        {
            ["id"] = type,
            ["name"] = name,
            ["subtask"] = subtask,
            ["statuses"] = new JsonArray([.. statuses.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s,
                ["name"] = jira.Cloud ? names[int.Parse(s, CultureInfo.InvariantCulture)] : s switch { "1" => "Open", "3" => "In Progress", _ => "Closed" },
                ["statusCategory"] = new JsonObject { ["key"] = jira.Cloud ? categories[int.Parse(s, CultureInfo.InvariantCulture)] : s switch { "1" => "new", "3" => "indeterminate", _ => "done" } },
            })]),
        });
    }

    private static JsonObject Field(string id, string name, string type, string? custom = null, string? items = null) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["custom"] = id.StartsWith("customfield_", StringComparison.Ordinal),
        ["schema"] = new JsonObject { ["type"] = type, ["items"] = items, ["custom"] = custom },
    };

    private static JsonObject Doc(params JsonNode[] content) => new() { ["type"] = "doc", ["version"] = 1, ["content"] = new JsonArray(content) };

    private static JsonObject Paragraph(params JsonNode[] content) => new() { ["type"] = "paragraph", ["content"] = new JsonArray(content) };

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Media(string alt) => new()
    {
        ["type"] = "mediaSingle",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "media", ["attrs"] = new JsonObject { ["id"] = "9f1c-media", ["type"] = "file", ["collection"] = "", ["alt"] = alt } }),
    };

    /// <summary>
    /// Three projects: Web (company-managed) holds Initiative › Epic › Story › Sub-task, a bug, links of every kind, a
    /// file, sprints, versions, long changelog and comments; Mobile is team-managed with its own Story; Help desk is
    /// Service Management.
    /// </summary>
    internal static FakeJira Cloud()
    {
        var jira = new FakeJira(cloud: true);
        jira.AddProject("10000", "WEB", "Web");
        jira.AddProject("10001", "MOB", "Mobile");
        jira.AddProject("10002", "HELP", "Help desk", "service_desk");
        foreach (var (id, name, level) in new[] { ("10001", "Epic", 1), ("10002", "Story", 0), ("10003", "Sub-task", -1), ("10004", "Bug", 0), ("10005", "Initiative", 2), ("10006", "New Feature", 0), ("10100", "Story", 0), ("10200", "[System] Service request", 0) })
        {
            jira.IssueTypes.Add(new JsonObject { ["id"] = id, ["name"] = name, ["subtask"] = level < 0, ["hierarchyLevel"] = level });
        }

        foreach (var (id, name) in new[] { ("10001", "Epic"), ("10002", "Story"), ("10003", "Sub-task"), ("10004", "Bug"), ("10005", "Initiative"), ("10006", "New Feature") })
        {
            Statuses(jira, "10000", id, name, id == "10003", "1", "2", "3", "4", "5", "6");
        }

        Statuses(jira, "10001", "10100", "Story", false, "2", "3", "5");
        Statuses(jira, "10002", "10200", "[System] Service request", false, "2", "5");

        jira.Fields.AddRange(
        [
            Field("summary", "Summary", "string"),
            Field("customfield_10020", "Sprint", "array", JiraConnector.SprintSchema, "json"),
            Field("customfield_10016", "Story point estimate", "number", "com.pyxis.greenhopper.jira:jsw-story-points"),
            Field("customfield_10015", "Start date", "date", "com.atlassian.jira.plugin.system.customfieldtypes:datepicker"),
            Field("customfield_10019", "Rank", "any", "com.pyxis.greenhopper.jira:gh-lexo-rank"),
            Field("customfield_10050", "Team", "option", "com.atlassian.jira.plugin.system.customfieldtypes:select"),
            Field("customfield_10051", "Reviewer", "user", "com.atlassian.jira.plugin.system.customfieldtypes:userpicker"),
            Field("customfield_10052", "Notes", "string", "com.atlassian.jira.plugin.system.customfieldtypes:textarea"),
            Field("customfield_10053", "Budget", "number", "com.atlassian.jira.plugin.system.customfieldtypes:float"),
            Field("customfield_10054", "Go live", "date", "com.atlassian.jira.plugin.system.customfieldtypes:datepicker"),
            Field("customfield_10060", "Request participants", "sd-customerrequesttype", "com.atlassian.servicedesk:vp-origin"),
        ]);
        jira.Versions["10000"].Add(new JsonObject { ["id"] = "20001", ["name"] = "1.0", ["released"] = false, ["releaseDate"] = "2025-04-01" });
        jira.Versions["10000"].Add(new JsonObject { ["id"] = "20002", ["name"] = "0.9", ["released"] = true });

        jira.AddIssue("10000", "10010", "WEB-1", "10005", "3", "Payments", T0);
        jira.AddIssue("10000", "10011", "WEB-2", "10001", "2", "Checkout", T0).Set("parent", new JsonObject { ["id"] = "10010", ["key"] = "WEB-1" });

        var description = Doc(
            Paragraph(Text("Card first, ask "), new JsonObject { ["type"] = "mention", ["attrs"] = new JsonObject { ["id"] = "acc-sofie", ["text"] = "@Sofie Larsen" } }),
            Media("flow.png"),
            Paragraph(new JsonObject { ["type"] = "inlineCard", ["attrs"] = new JsonObject { ["url"] = "https://acme.atlassian.net/browse/WEB-5" } }));
        string Sprint(long id, string name, string state, int from, int to) =>
            new JsonObject { ["id"] = id, ["name"] = name, ["state"] = state, ["boardId"] = 1, ["startDate"] = $"2025-03-{from:00}T08:00:00.000Z", ["endDate"] = $"2025-03-{to:00}T16:00:00.000Z" }.ToJsonString();
        jira.AddIssue("10000", "10012", "WEB-3", "10002", "4", "Pay by card", T0.AddDays(3))
            .Set("parent", new JsonObject { ["id"] = "10011", ["key"] = "WEB-2" })
            .Set("description", description)
            .Set("priority", new JsonObject { ["id"] = "2", ["name"] = "High" })
            .Set("assignee", Sofie())
            .Set("reporter", Jonas())
            .Set("labels", new JsonArray("payments", "mobile"))
            .Set("components", new JsonArray(new JsonObject { ["id"] = "1", ["name"] = "Frontend" }))
            .Set("fixVersions", new JsonArray(new JsonObject { ["id"] = "20001", ["name"] = "1.0" }))
            .Set("duedate", "2025-03-20")
            .Set("timeoriginalestimate", 14400)
            .Set("timespent", 5400)
            .Set("customfield_10016", 5)
            .Set("customfield_10015", "2025-03-03")
            .Set("customfield_10019", "0|i0001:")
            .Set("customfield_10020", JsonNode.Parse($"[{Sprint(11, "Sprint 1", "closed", 3, 14)},{Sprint(12, "Sprint 2", "active", 17, 28)}]"))
            .Set("customfield_10050", new JsonObject { ["id"] = "1", ["value"] = "Blue" })
            .Set("customfield_10051", Jonas())
            .Set("customfield_10052", Doc(Paragraph(Text("Ship it"))))
            .Set("customfield_10053", 1200.5)
            .Set("customfield_10054", "2025-05-01")
            .Attach("30001", "flow.png", "image/png", "PNG!"u8.ToArray(), Sofie(), T0.AddHours(2))
            .Remote("https://acme.atlassian.net/wiki/spaces/PAY/pages/123", "Payments design", "com.atlassian.confluence")
            .Remote("https://stripe.com/docs/testing", " ")
            .Remote("javascript:alert(1)", "Click")
            .Link("40001", "Blocks", "blocks", "is blocked by", outwardIssue: "10013")
            .Link("40002", "Relates", "relates to", "relates to", inwardIssue: "10014")
            .Link("40003", "Cloners", "clones", "is cloned by", outwardIssue: "10014")
            .Link("40004", "Depends", "depends on", "is needed by", outwardIssue: "10013")
            .History("70001", Jonas(), T0.AddDays(1), ("status", "status", "2", "To Do", "3", "In Progress"), ("assignee", "assignee", null, null, "acc-sofie", "Sofie Larsen"), ("Sprint", "customfield_10020", "", "", "11", "Sprint 1"))
            .History("70002", Sofie(), T0.AddDays(2), ("priority", "priority", "3", "Medium", "2", "High"), ("Sprint", "customfield_10020", "11", "Sprint 1", "11, 12", "Sprint 1, Sprint 2"), ("Team", "customfield_10050", null, null, "1", "Blue"))
            .History("70003", Sofie(), T0.AddDays(3), ("status", "status", "3", "In Progress", "4", "QA"), ("Rank", "customfield_10019", null, null, null, "Ranked higher"))
            .History("70004", Jonas(), T0.AddDays(3), ("description", "description", null, "old", null, "new"), ("Fix Version", "fixVersions", null, null, "20001", "1.0"))
            .Comment("50001", Doc(Paragraph(Text("Looks good"))), Jonas(), T0.AddDays(1))
            .Comment("50002", Doc(Paragraph(Text("Thanks"))), Sofie(), T0.AddDays(2))
            .Comment("50003", Doc(Media("flow.png")), Sofie(), T0.AddDays(3));
        jira.AddIssue("10000", "10013", "WEB-4", "10003", "5", "Wire up gateway", T0.AddDays(5))
            .Set("parent", new JsonObject { ["id"] = "10012", ["key"] = "WEB-3" })
            .Set("resolutiondate", FakeJira.Time(T0.AddDays(5)))
            .Link("40001", "Blocks", "blocks", "is blocked by", inwardIssue: "10012")
            .Link("40004", "Depends", "depends on", "is needed by", inwardIssue: "10012");
        jira.AddIssue("10000", "10014", "WEB-5", "10004", "6", "Card declined twice", T0.AddDays(1))
            .Set("resolution", new JsonObject { ["name"] = "Won't Do" })
            .Link("40002", "Relates", "relates to", "relates to", outwardIssue: "10012")
            .Link("40003", "Cloners", "clones", "is cloned by", inwardIssue: "10012")
            .Link("40005", "Duplicate", "duplicates", "is duplicated by", outwardIssue: "10015");
        jira.AddIssue("10000", "10015", "WEB-6", "10006", "1", "Apple Pay", T0.AddDays(1))
            .Link("40005", "Duplicate", "duplicates", "is duplicated by", inwardIssue: "10014");
        jira.AddIssue("10001", "10020", "MOB-1", "10100", "2", "Login", T0).Set("assignee", FakeJira.User("acc-bot", "Automation for Jira", type: "app"));
        jira.AddIssue("10002", "10030", "HELP-1", "10200", "2", "VPN down", T0);
        return jira;
    }

    /// <summary>
    /// Data Center 9: Ops holds an epic, a task linked to it by Epic Link with a wiki description that shows files by
    /// name, a sub-task, and a task whose epic lives in Archive. Sprints come as the old string form, without dates.
    /// </summary>
    private static FakeJira DataCenter()
    {
        var jira = new FakeJira(cloud: false);
        jira.AddProject("10000", "OPS", "Ops");
        jira.AddProject("10001", "ARC", "Archive");
        jira.IssueTypes.Add(new JsonObject { ["id"] = "1", ["name"] = "Epic", ["subtask"] = false });
        jira.IssueTypes.Add(new JsonObject { ["id"] = "3", ["name"] = "Task", ["subtask"] = false });
        jira.IssueTypes.Add(new JsonObject { ["id"] = "5", ["name"] = "Sub-task", ["subtask"] = true });
        foreach (var project in new[] { "10000", "10001" })
        {
            Statuses(jira, project, "1", "Epic", false, "1", "3", "6");
            Statuses(jira, project, "3", "Task", false, "1", "3", "6");
            Statuses(jira, project, "5", "Sub-task", true, "1", "3", "6");
        }

        jira.Fields.AddRange(
        [
            Field("customfield_10100", "Epic Link", "any", JiraConnector.EpicLinkSchema),
            Field("customfield_10101", "Sprint", "array", JiraConnector.SprintSchema, "string"),
            Field("customfield_10102", "Story Points", "number", "com.atlassian.jira.plugin.system.customfieldtypes:float"),
        ]);
        jira.Sprints[7] = new JsonObject
        {
            ["id"] = 7,
            ["name"] = "Sprint 7, the big one",
            ["state"] = "active",
            ["startDate"] = "2025-03-10T09:00:00.000+01:00",
            ["endDate"] = "2025-03-21T17:00:00.000+01:00",
        };

        jira.AddIssue("10000", "10100", "OPS-1", "1", "3", "Backups", T0);
        jira.AddIssue("10000", "10101", "OPS-2", "3", "3", "Nightly restore test", T0.AddDays(1))
            .Set("customfield_10100", "OPS-1")
            .Set("customfield_10101", new JsonArray("com.atlassian.greenhopper.service.sprint.Sprint@1a2b[id=7,rapidViewId=1,state=ACTIVE,name=Sprint 7, the big one,startDate=<null>,endDate=<null>,completeDate=<null>,sequence=7,goal=]"))
            .Set("customfield_10102", 3)
            .Set("assignee", Sofie(cloud: false))
            .Set("description", "See !screen shot.png|thumbnail! and [^log.txt]. Ping [~sofie].")
            .Attach("60001", "screen shot.png", "image/png", "PNG!"u8.ToArray(), Sofie(cloud: false), T0)
            .Attach("60002", "log.txt", "text/plain", "ok"u8.ToArray(), Sofie(cloud: false), T0)
            .History("80001", Jonas(cloud: false), T0.AddHours(1), ("status", null, "1", "Open", "3", "In Progress"))
            .History("80002", Jonas(cloud: false), T0.AddHours(2), ("Epic Link", null, null, null, "10100", "OPS-1"))
            .History("80003", Jonas(cloud: false), T0.AddHours(3), ("Story Points", null, null, "2", null, "3"));
        jira.AddIssue("10000", "10102", "OPS-3", "5", "1", "Restore to staging", T0).Set("parent", new JsonObject { ["id"] = "10101", ["key"] = "OPS-2" });
        jira.AddIssue("10000", "10103", "OPS-4", "3", "1", "Old backups", T0).Set("customfield_10100", "ARC-1");
        jira.AddIssue("10001", "10200", "ARC-1", "1", "6", "Archive", T0);
        return jira;
    }

    private sealed class Clock(FakeJira jira) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => jira.Now;
    }
}
