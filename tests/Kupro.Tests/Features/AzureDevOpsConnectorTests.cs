using System.Text.Json;
using System.Text.Json.Nodes;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.AzureDevOps;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;
using static Kupro.Tests.Infrastructure.FakeWorkItem;

namespace Kupro.Tests.Features;

/// <summary>
/// The Azure DevOps connector (task 025) against <see cref="FakeAzureDevOps"/>: Describe per process, the read into
/// snapshot records, revisions as changes, ID-range reads past WIQL's 20,000 cap, delta cursors, the Server version
/// fallback, throttling, and where the PAT may go.
/// </summary>
public sealed class AzureDevOpsConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2025, 3, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Attachment = "9f1c2b7e-0d4e-4a8b-9a51-3c2d1e0f9a77";

    private static readonly string Agile = FakeProcess.Agile.TypeId;

    private static JsonObject Sofie() => FakeAzureDevOps.Identity("u-sofie", "Sofie Larsen", "sofie@acme.example");

    private static JsonObject Jonas() => FakeAzureDevOps.Identity("u-jonas", "Jonas Holm", "jonas@acme.example");

    private static DateTimeOffset Utc(int month, int day) => new(2025, month, day, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Describe_reports_projects_with_their_process_types_levels_states_and_fields()
    {
        var description = await Connect(Sample()).DescribeAsync(Ct);

        description.Source.ShouldBe(new SnapshotSource { Kind = SourceKinds.AzureDevOps, BaseUrl = "https://dev.azure.com/acme" });
        description.Projects.Select(p => (p.Name, p.Process)).ShouldBe(
            [("Web", "Agile"), ("Mobile", "Scrum"), ("Ops", "Basic"), ("Finance", "CMMI")]);
        description.Projects[0].Url.ShouldBe("https://dev.azure.com/acme/Web");

        // Levels come from the backlog configuration; types off every backlog (Test Case) have none.
        var web = description.Projects[0].Id;
        var levels = description.ItemTypes.Where(t => t.Id.StartsWith(Agile, StringComparison.Ordinal)).ToDictionary(t => t.Name, t => t.Level);
        levels.ShouldBe(new Dictionary<string, int?>
        {
            ["Epic"] = 3,
            ["Feature"] = 2,
            ["User Story"] = 1,
            ["Bug"] = 1,
            ["Task"] = 0,
            ["Issue"] = null,
            ["Test Case"] = null,
        }, ignoreOrder: true);
        description.ItemTypes.First(t => t.Name == "User Story").Project.ShouldBe(web);

        // The inherited process keeps its own types, custom one included.
        description.ItemTypes.ShouldContain(t => t.Name == "Audit Finding" && t.Id.StartsWith("3c1f0a52", StringComparison.Ordinal));

        var resolved = description.Statuses.Single(s => s.Id == $"{Agile}/User Story/Resolved");
        resolved.Category.ShouldBe("Resolved");
        resolved.ItemType.ShouldBe($"{Agile}/User Story");
        description.Statuses.Single(s => s.Id == $"{FakeProcess.Scrum.TypeId}/Product Backlog Item/Approved").Category.ShouldBe("Proposed");

        // Fields: what the details block or labels can use; not system, bookkeeping or fields items already carry.
        var fields = description.Fields.ToDictionary(f => f.Id);
        fields.Keys.ShouldBe(
            ["System.AreaPath", "System.IterationPath", "Microsoft.VSTS.Common.Severity", "Microsoft.VSTS.Common.ValueArea",
             "Custom.Team", "Custom.Reviewer", "Custom.GoLive", "Custom.Budget", "Custom.Notes"], ignoreOrder: true);
        fields["System.AreaPath"].Kind.ShouldBe(FieldKind.Select);
        fields["System.AreaPath"].Options.ShouldContain("Web\\Checkout");
        fields["System.IterationPath"].Options.ShouldContain("Web\\Later");
        fields["Custom.Team"].Kind.ShouldBe(FieldKind.Select);
        fields["Custom.Reviewer"].Kind.ShouldBe(FieldKind.User);
        fields["Custom.GoLive"].Kind.ShouldBe(FieldKind.Date);
        fields["Custom.Budget"].Kind.ShouldBe(FieldKind.Number);
        fields["Custom.Notes"].Kind.ShouldBe(FieldKind.Text);
    }

    [Fact]
    public async Task The_default_mapping_makes_critical_bugs_urgent_and_new_items_outside_a_sprint_backlog()
    {
        var stream = new MemoryStream();
        await Connect(Sample()).WriteSnapshotAsync(stream, new ReadScope(), null, includeFiles: false, TimeProvider.System, Ct);
        stream.Position = 0;
        await using var snapshot = await SnapshotReader.OpenAsync(stream, Ct);
        var mapping = ImportMappingDefaults.Propose(snapshot.Header, []);

        mapping.UrgentValues["Microsoft.VSTS.Common.Severity"].ShouldBe(["1 - Critical"]);
        mapping.BacklogWithoutSprint.ShouldContain($"{Agile}/Bug/New");
        mapping.BacklogWithoutSprint.ShouldNotContain($"{Agile}/Bug/Active");

        var source = new MappedSource(snapshot.Header, mapping, Now);
        SourceItem Bug(string state, string? sprint = null, string? severity = null) => new()
        {
            Id = "1",
            Project = "p",
            Type = $"{Agile}/Bug",
            Status = $"{Agile}/Bug/{state}",
            Title = "Bug",
            Priority = "3",
            Sprint = sprint,
            Created = T0,
            Updated = T0,
            Fields = severity is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement> { ["Microsoft.VSTS.Common.Severity"] = FieldValue.Text(severity) },
        };
        source.State(Bug("New")).ShouldBe(IssueState.Backlog);
        source.State(Bug("New", sprint: "11")).ShouldBe(IssueState.Todo, "planned into a sprint");
        source.State(Bug("Active")).ShouldBe(IssueState.InProgress);
        source.Priority(Bug("New", severity: "1 - Critical")).ShouldBe(IssuePriority.Urgent);
        source.Priority(Bug("New", severity: "2 - High")).ShouldBe(IssuePriority.Low, "priority 3 stands");
        source.Priority(Bug("New")).ShouldBe(IssuePriority.Low);
    }

    [Fact]
    public async Task Read_maps_work_items_with_hierarchy_effort_dates_sprints_and_links()
    {
        var ado = Sample();
        var records = await Connect(ado).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        var items = records.OfType<SourceItem>().ToDictionary(i => i.Id);

        items.Keys.ShouldBe(["100", "101", "102", "103", "104", "105", "106", "200", "201", "300", "301", "400", "401"], ignoreOrder: true);

        var story = items["102"];
        story.Key.ShouldBe("102");
        story.Type.ShouldBe($"{Agile}/User Story");
        story.Status.ShouldBe($"{Agile}/User Story/Resolved");
        story.Parent.ShouldBe("101");
        story.Assignee.ShouldBe("u-sofie");
        story.Creator.ShouldBe("u-jonas");
        story.Priority.ShouldBe("2");
        story.Labels.ShouldBe(["payments", "mobile"]);
        story.Points.ShouldBe(5);
        story.EstimateSeconds.ShouldBe(5 * 3600, "no original estimate: remaining + completed");
        story.Start.ShouldBe(new DateOnly(2025, 3, 3));
        story.Due.ShouldBe(new DateOnly(2025, 3, 20), "a Danish 20 March is stored as 19 March 23:00 UTC");
        story.Sprint.ShouldBe("11");
        story.Url.ShouldBe("https://dev.azure.com/acme/Web/_workitems/edit/102");
        story.Resolved.ShouldBeNull("Resolved is not a done category");
        story.DescriptionMarkup.ShouldBe(MarkupKind.Html);
        story.Description.ShouldBe(
            "<div>Card first, then <a href=\"#\" data-vss-mention=\"version:2.0,u-sofie\">@Sofie Larsen</a>. See #104.</div>"
            + "<h2>Acceptance criteria</h2><ul><li>Visa works</li></ul>");
        story.Fields["System.AreaPath"].GetString().ShouldBe("Web\\Checkout");
        story.Fields["Custom.Team"].GetString().ShouldBe("Blue");
        story.Fields["Custom.Reviewer"].GetString().ShouldBe("u-jonas");
        story.Fields["Custom.GoLive"].GetString().ShouldBe("2025-05-01");
        story.Fields["Custom.Budget"].GetDecimal().ShouldBe(1200.5m);
        story.Fields.ShouldNotContainKey("Microsoft.VSTS.Common.StackRank");
        story.Links.Select(l => (l.Url, l.Title)).ShouldBe(
        [
            ($"https://dev.azure.com/acme/{ado.Projects[0].Id}/_git/repo-1/commit/abc123", "Fixed in Commit"),
            ("https://wiki.acme.example/cards", "Spec"),
        ]);

        var task = items["103"];
        task.EstimateSeconds.ShouldBe(4 * 3600, "original estimate wins");
        task.Sprint.ShouldBeNull("an undated iteration is not a sprint");
        task.Fields["System.IterationPath"].GetString().ShouldBe("Web\\Later");
        task.Resolved.ShouldBe(new DateTimeOffset(2025, 3, 6, 12, 0, 0, TimeSpan.Zero));
        items["105"].Resolved.ShouldBe(new DateTimeOffset(2025, 3, 7, 8, 0, 0, TimeSpan.Zero), "removed, no closed date: state change date");
        items["104"].Description.ShouldBe("<h2>Repro steps</h2><ol><li>Pay twice</li></ol>");
        items["104"].Fields["Microsoft.VSTS.Common.Severity"].GetString().ShouldBe("1 - Critical");
        items["401"].Type.ShouldBe("3c1f0a52-7d55-4e5b-9d6e-1b2b8f0e4a11/Audit Finding");

        // Each link once: successor → blocks, "duplicate of" → duplicates, related once per pair; parents aren't relations.
        records.OfType<SourceRelation>().Select(r => (r.From, r.To, r.Kind)).ShouldBe(
            [("102", "103", RelationKinds.Blocks), ("102", "104", RelationKinds.Relates), ("104", "105", RelationKinds.Duplicates)],
            ignoreOrder: true);

        var sprints = records.OfType<SourceSprint>().ToList();
        sprints.Select(s => (s.Id, s.Name, s.Start, s.End, s.Closed)).ShouldBe(
        [
            ("11", "Sprint 1", new DateOnly(2025, 3, 3), new DateOnly(2025, 3, 14), true),
            ("12", "Sprint 2", new DateOnly(2025, 3, 17), new DateOnly(2025, 3, 28), false),
        ], ignoreOrder: true);
        sprints.ShouldAllBe(s => s.Project == ado.Projects[0].Id);

        var people = records.OfType<SourcePerson>().ToList();
        people.Select(p => p.Id).ShouldBe(["u-jonas", "u-sofie"], ignoreOrder: true);
        people.Single(p => p.Id == "u-sofie").Email.ShouldBe("sofie@acme.example");
        records.IndexOf(people.Single(p => p.Id == "u-sofie")).ShouldBeLessThan(records.IndexOf(items["102"]), "people come before records that use them");

        records[^1].ShouldBe(new ReadCursor(T0.AddDays(5).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task Comments_and_attachments_come_across_and_are_only_fetched_when_there_are_some()
    {
        var ado = Sample();
        var connector = Connect(ado);
        var records = await connector.ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);

        var comments = records.OfType<SourceComment>().ToList();
        comments.Select(c => (c.Id, c.Item, c.Author, c.Markup)).ShouldBe(
            [("102-1", "102", "u-jonas", MarkupKind.Html), ("102-2", "102", "u-sofie", MarkupKind.Markdown)]);
        comments[1].Body.ShouldBe("**Shipped** to staging");
        comments[0].Created.ShouldBe(T0.AddDays(1));
        ado.Requests.Count(r => r.Contains("/comments", StringComparison.Ordinal)).ShouldBe(1, "only 102 has comments");
        ado.Requests.Single(r => r.Contains("/comments", StringComparison.Ordinal)).ShouldContain("api-version=7.1-preview.4");
        ado.Requests.Count(r => r.Contains("/updates", StringComparison.Ordinal)).ShouldBe(1, "only 102 has revisions past its creation");

        var attachment = records.OfType<SourceAttachment>().Single();
        attachment.Id.ShouldBe(Attachment);
        attachment.Item.ShouldBe("102");
        attachment.FileName.ShouldBe("flow.png");
        attachment.Size.ShouldBe(4);
        attachment.Created.ShouldBe(new DateTimeOffset(2025, 3, 2, 10, 0, 0, TimeSpan.Zero));

        await using var file = await connector.FetchAttachmentAsync(attachment, Ct);
        (await new StreamReader(file).ReadToEndAsync(Ct)).ShouldBe("PNG!");
    }

    [Fact]
    public async Task Revisions_replay_as_changes_in_kupro_terms_skipping_bookkeeping()
    {
        var records = await Connect(Sample()).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        var changes = records.OfType<SourceChange>().Where(c => c.Item == "102").ToList();

        changes.Select(c => c.Id).ShouldBeUnique();
        changes.Select(c => c.Field).ShouldBe(
            ["status", "assignee", "sprint", "Custom.Team", "priority", "description", "parent", "status", "due"]);

        var start = changes[0];
        (start.From, start.To, start.FromText, start.ToText).ShouldBe(($"{Agile}/User Story/New", $"{Agile}/User Story/Active", "New", "Active"));
        start.Actor.ShouldBe("u-sofie");
        start.At.ShouldBe(T0.AddDays(2));
        start.Id.ShouldBe("2:System.State");

        (changes[1].From, changes[1].To, changes[1].ToText).ShouldBe((null, "u-sofie", "Sofie Larsen"));
        (changes[2].From, changes[2].To, changes[2].FromText, changes[2].ToText).ShouldBe((null, "11", "Web", "Web\\Sprint 1"));
        (changes[3].From, changes[3].To).ShouldBe((null, "Blue"));
        (changes[4].From, changes[4].To, changes[4].Actor).ShouldBe(("3", "2", "u-jonas"));
        (changes[5].From, changes[5].To).ShouldBe((null, null), "description edits are a marker, once per update");
        (changes[6].From, changes[6].To, changes[6].ToText).ShouldBe((null, "101", "#101"));
        changes[8].To.ShouldBe("2025-03-20");
        changes[8].At.ShouldBe(T0.AddDays(5));
    }

    [Fact]
    public async Task A_project_over_the_wiql_cap_is_read_completely_in_id_ranges()
    {
        var ado = new FakeAzureDevOps();
        ado.Fields.AddRange(FakeAzureDevOps.StandardFields());
        var ops = ado.AddProject("Ops", FakeProcess.Basic, 3);
        for (var id = 1; id <= 25_000; id++)
        {
            ado.AddItem(ops, id, "Task", "To Do", $"Task {id}", T0);
        }

        var items = await Connect(ado).ReadAsync(new ReadScope(), null, Ct).OfType<SourceItem>().ToListAsync(Ct);

        items.Count.ShouldBe(25_000);
        items.Select(i => i.Id).ShouldBeUnique();
        ado.Requests.Count(r => r.Contains("/wiql", StringComparison.Ordinal)).ShouldBe(2);
        ado.Requests.Count(r => r.Contains("workitemsbatch", StringComparison.Ordinal)).ShouldBe(125);
    }

    [Fact]
    public async Task A_delta_read_returns_only_items_changed_since_the_cursor()
    {
        var connector = Connect(Sample());

        var records = await connector.ReadAsync(new ReadScope(), T0.AddDays(4).ToString("O"), Ct).ToListAsync(Ct);

        records.OfType<SourceItem>().Select(i => i.Id).ShouldBe(["102"]);
        records[^1].ShouldBe(new ReadCursor(T0.AddDays(5).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

        var nothing = await connector.ReadAsync(new ReadScope(), "2026-01-01T00:00:00Z", Ct).ToListAsync(Ct);
        nothing.OfType<SourceItem>().ShouldBeEmpty();
        nothing[^1].ShouldBe(new ReadCursor("2026-01-01T00:00:00Z"), "no change: the cursor stays");
    }

    [Fact]
    public async Task Scope_limits_projects_and_skips_items_closed_before_the_cutoff_with_their_history()
    {
        var ado = Sample();
        var connector = Connect(ado);

        var mobile = await connector.ReadAsync(new ReadScope { Projects = [ado.Projects[1].Id] }, null, Ct).OfType<SourceItem>().ToListAsync(Ct);
        mobile.Select(i => i.Id).ShouldBe(["200", "201"]);

        var recent = await connector.ReadAsync(new ReadScope { ResolvedAfter = T0.AddDays(6.5) }, null, Ct).ToListAsync(Ct);
        recent.OfType<SourceItem>().Select(i => i.Id).ShouldNotContain("103");
        recent.OfType<SourceItem>().Select(i => i.Id).ShouldNotContain("105");
        recent.OfType<SourceItem>().Select(i => i.Id).ShouldContain("102");
    }

    [Fact]
    public async Task Server_falls_back_to_the_newest_api_version_it_supports()
    {
        var ado = new FakeAzureDevOps("https://tfs.acme.local/tfs/DefaultCollection") { MaxVersion = "6.0" };
        ado.Fields.AddRange(FakeAzureDevOps.StandardFields());
        var ops = ado.AddProject("Ops", FakeProcess.Basic, 3);
        // Server identities: DOMAIN\user, no email.
        ado.AddItem(ops, 7, "Issue", "Doing", "Rotate keys", T0)
            .Set("System.CreatedBy", FakeAzureDevOps.Identity("8d1e", "Mads Jensen", "ACME\\mads"))
            .Set("System.AssignedTo", FakeAzureDevOps.Identity("8d1e", "Mads Jensen", "ACME\\mads"))
            .Comment(1, "<p>On it</p>", FakeAzureDevOps.Identity("8d1e", "Mads Jensen", "ACME\\mads"), T0);

        var connector = Connect(ado, "https://tfs.acme.local/tfs/DefaultCollection/Ops/_workitems/edit/7");
        var records = await connector.ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);

        records.OfType<SourceItem>().Single().Url.ShouldBe("https://tfs.acme.local/tfs/DefaultCollection/Ops/_workitems/edit/7");
        records.OfType<SourcePerson>().Single().ShouldBe(new SourcePerson { Id = "8d1e", Name = "Mads Jensen" });
        records.OfType<SourceComment>().Single().Body.ShouldBe("<p>On it</p>");
        ado.Requests[0].ShouldContain("api-version=7.1");
        ado.Requests.Skip(1).ShouldAllBe(r => r.Contains("api-version=6.0", StringComparison.Ordinal));
        ado.Requests.Count(r => r.Contains("api-version=7.1", StringComparison.Ordinal)).ShouldBe(1, "the 400 names 6.0, so 7.0 is skipped");
        ado.Requests.Single(r => r.Contains("/comments", StringComparison.Ordinal)).ShouldContain("api-version=6.0-preview.3");
    }

    [Fact]
    public async Task Throttled_requests_wait_for_retry_after_and_then_succeed()
    {
        var ado = Sample();
        ado.Throttle = 2;
        var waits = new List<TimeSpan>();
        var clock = new TestClock(Now);
        var connector = Connect(ado, clock: clock);
        connector.Client.Delay = (wait, _) =>
        {
            waits.Add(wait);
            clock.Now += wait;
            return Task.CompletedTask;
        };

        (await connector.DescribeAsync(Ct)).Projects.Count.ShouldBe(4);

        waits.ShouldBe([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)]);
    }

    [Fact]
    public async Task A_rejected_token_fails_as_a_token_problem()
    {
        var error = await Should.ThrowAsync<SourceAccessException>(() => Connect(Sample(), token: "wrong-token").DescribeAsync(Ct));

        error.TokenRejected.ShouldBeTrue();
        error.Message.ShouldNotContain("wrong-token");
    }

    [Fact]
    public async Task The_token_is_never_sent_to_another_host()
    {
        var ado = Sample();
        var attachment = new SourceAttachment
        {
            Id = "x",
            Item = "102",
            FileName = "x.png",
            Created = T0,
            Url = "https://evil.example/_apis/wit/attachments/x",
        };

        await Should.ThrowAsync<SourceAccessException>(() => Connect(ado).FetchAttachmentAsync(attachment, Ct));

        ado.ForeignHosts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://dev.azure.com/acme", "https://dev.azure.com/acme")]
    [InlineData("https://dev.azure.com/acme/Web/_workitems/edit/102/", "https://dev.azure.com/acme")]
    [InlineData(" https://acme.visualstudio.com/DefaultCollection/Web ", "https://acme.visualstudio.com/DefaultCollection")]
    [InlineData("https://acme.visualstudio.com/Web/_boards", "https://acme.visualstudio.com/")]
    [InlineData("http://tfs:8080/tfs/DefaultCollection/", "http://tfs:8080/tfs/DefaultCollection")]
    [InlineData("https://tfs.acme.local/tfs/Main/Web/_workitems", "https://tfs.acme.local/tfs/Main")]
    [InlineData("https://tfs.acme.local/Main/Web/_workitems/edit/7", "https://tfs.acme.local/Main")]
    [InlineData("http://tfs:8080/tfs/DefaultCollection/_settings", "http://tfs:8080/tfs/DefaultCollection")]
    public void Pasted_urls_normalize_to_the_collection(string pasted, string collection) =>
        AzureDevOpsConnector.NormalizeUrl(pasted).AbsoluteUri.ShouldBe(new Uri(collection).AbsoluteUri);

    [Fact]
    public async Task A_read_writes_a_snapshot_with_every_record_and_file()
    {
        var zip = new MemoryStream();
        await Connect(Sample()).WriteSnapshotAsync(zip, new ReadScope(), null, includeFiles: true, new TestClock(Now), Ct);
        zip.Position = 0;

        await using var snapshot = await SnapshotReader.OpenAsync(zip, Ct);

        snapshot.Header.Source.Kind.ShouldBe(SourceKinds.AzureDevOps);
        snapshot.Header.Counts.ShouldBe(new SnapshotCounts { Items = 13, Relations = 3, Comments = 2, Attachments = 1, Changes = 9 });
        snapshot.Header.Sprints.Count.ShouldBe(2);
        snapshot.Header.People.Count.ShouldBe(2);
        (await snapshot.ReadAttachmentsAsync(Ct).SingleAsync(Ct)).File.ShouldNotBeNull();
    }

    private static AzureDevOpsConnector Connect(
        FakeAzureDevOps ado, string? url = null, string token = FakeAzureDevOps.Token, TimeProvider? clock = null)
    {
        var connector = new AzureDevOpsConnector(ado.Client(), url ?? ado.Collection.AbsoluteUri, token, clock ?? new TestClock(Now));
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        return connector;
    }

    /// <summary>
    /// Four projects, one per built-in process (the CMMI one inherited, with a custom type). Web holds the interesting
    /// items: an Epic › Feature › Story › Task chain, bugs linked as related and duplicate, a story with every kind of
    /// field, link, a file, two comments and four revisions.
    /// </summary>
    private static FakeAzureDevOps Sample()
    {
        var ado = new FakeAzureDevOps();
        ado.Fields.AddRange(FakeAzureDevOps.StandardFields());
        var web = ado.AddProject("Web", FakeProcess.Agile, 1);
        web.Iterations.Add(11, "Sprint 1", Utc(3, 3), Utc(3, 14)).Add(12, "Sprint 2", Utc(3, 17), Utc(3, 28)).Add(13, "Later");
        web.Areas.Add(21, "Checkout").Add(22, "Platform");
        var mobile = ado.AddProject("Mobile", FakeProcess.Scrum, 2);
        var ops = ado.AddProject("Ops", FakeProcess.Basic, 3);
        var finance = ado.AddProject("Finance", FakeProcess.Inherited("Acme CMMI", FakeProcess.Cmmi, "Audit Finding"), 4);

        const string Parent = "System.LinkTypes.Hierarchy-Reverse";
        ado.AddItem(web, 100, "Epic", "New", "Checkout", T0);
        ado.AddItem(web, 101, "Feature", "Active", "Pay with MobilePay", T0).Link(Parent, 100, "Parent");

        var description = "<div>Card first, then <a href=\"#\" data-vss-mention=\"version:2.0,u-sofie\">@Sofie Larsen</a>. See #104.</div>";
        var story = ado.AddItem(web, 102, "User Story", "New", "Pay by card", T0)
            .Set("Microsoft.VSTS.Common.Priority", 3)
            .Set("System.Tags", "payments; mobile")
            .Set("Microsoft.VSTS.Scheduling.StoryPoints", 5)
            .Set("Microsoft.VSTS.Scheduling.RemainingWork", 3)
            .Set("Microsoft.VSTS.Scheduling.CompletedWork", 2)
            .Set("Microsoft.VSTS.Scheduling.StartDate", "2025-03-03T00:00:00Z")
            .Set("System.AreaPath", "Web\\Checkout")
            .Set("Custom.Reviewer", Jonas())
            .Set("Custom.GoLive", "2025-04-30T22:00:00Z")
            .Set("Custom.Budget", 1200.5)
            .Link(Parent, 101, "Parent")
            .Link("System.LinkTypes.Dependency-Forward", 103, "Successor")
            .Link("System.LinkTypes.Related", 104, "Related");
        story.Relations.Add(Relation("ArtifactLink", $"vstfs:///Git/Commit/{web.Id}%2Frepo-1%2Fabc123", "Fixed in Commit"));
        story.Relations.Add(Relation("Hyperlink", "https://wiki.acme.example/cards", "Hyperlink", new JsonObject { ["comment"] = "Spec" }));
        story.Relations.Add(Relation("AttachedFile", $"https://dev.azure.com/acme/{web.Id}/_apis/wit/attachments/{Attachment}?fileName=flow.png", "flow.png",
            new JsonObject { ["resourceSize"] = 4, ["resourceCreatedDate"] = "2025-03-02T10:00:00Z" }));
        ado.Files[Attachment] = "PNG!"u8.ToArray();
        story
            .Update(T0.AddDays(2), Sofie(),
            [
                ("System.State", "New", "Active"),
                ("System.AssignedTo", null, Sofie()),
                ("System.IterationPath", "Web", "Web\\Sprint 1"),
                ("Custom.Team", null, "Blue"),
                ("Microsoft.VSTS.Common.StackRank", 1999.5, 1500),
                ("Microsoft.VSTS.Common.StateChangeDate", null, T0.AddDays(2).ToString("O")),
            ])
            .Update(T0.AddDays(3), Jonas(),
            [
                ("Microsoft.VSTS.Common.Priority", 3, 2),
                ("System.Description", "<div>Draft</div>", description),
                ("Microsoft.VSTS.Common.AcceptanceCriteria", null, "<ul><li>Visa works</li></ul>"),
            ], added: [Relation(Parent, "https://dev.azure.com/acme/_apis/wit/workItems/101", "Parent")])
            .Update(T0.AddDays(5), Sofie(),
            [
                ("System.State", "Active", "Resolved"),
                ("Microsoft.VSTS.Scheduling.TargetDate", null, "2025-03-19T23:00:00Z"),
            ])
            .Comment(1, "<div>Looks good</div>", Jonas(), T0.AddDays(1))
            .Comment(2, "**Shipped** to staging", Sofie(), T0.AddDays(4), format: "markdown");

        ado.AddItem(web, 103, "Task", "Closed", "Wire up gateway", T0)
            .Set("Microsoft.VSTS.Scheduling.OriginalEstimate", 4)
            .Set("Microsoft.VSTS.Scheduling.RemainingWork", 1)
            .Set("System.IterationPath", "Web\\Later")
            .Set("Microsoft.VSTS.Common.ClosedDate", "2025-03-06T12:00:00Z")
            .Link(Parent, 102, "Parent")
            .Link("System.LinkTypes.Dependency-Reverse", 102, "Predecessor");
        ado.AddItem(web, 104, "Bug", "Active", "Card declined twice", T0)
            .Set("Microsoft.VSTS.TCM.ReproSteps", "<ol><li>Pay twice</li></ol>")
            .Set("Microsoft.VSTS.Common.Severity", "1 - Critical")
            .Link("System.LinkTypes.Related", 102, "Related")
            .Link("System.LinkTypes.Duplicate-Reverse", 105, "Duplicate Of");
        ado.AddItem(web, 105, "Bug", "Removed", "Double charge", T0)
            .Set("Microsoft.VSTS.Common.StateChangeDate", "2025-03-07T08:00:00Z")
            .Link("System.LinkTypes.Duplicate-Forward", 104, "Duplicate");
        ado.AddItem(web, 106, "Test Case", "Design", "Card flow", T0);

        ado.AddItem(mobile, 200, "Product Backlog Item", "Approved", "Login", T0);
        ado.AddItem(mobile, 201, "Impediment", "Open", "Blocked on store review", T0);
        ado.AddItem(ops, 300, "Issue", "Doing", "Rotate keys", T0);
        ado.AddItem(ops, 301, "Task", "To Do", "Rotate the S3 key", T0).Link(Parent, 300, "Parent");
        ado.AddItem(finance, 400, "Requirement", "Proposed", "VAT report", T0);
        ado.AddItem(finance, 401, "Audit Finding", "Investigating", "Missing invoice trail", T0);

        // The story's description as it is now (the update above set it).
        story.Set("System.Description", description);
        return ado;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
