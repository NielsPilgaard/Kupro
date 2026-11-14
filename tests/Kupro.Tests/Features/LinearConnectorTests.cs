using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Kupro.Import;
using Kupro.Import.Linear;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;
using static Kupro.Tests.Infrastructure.FakeLinear;

namespace Kupro.Tests.Features;

/// <summary>
/// The Linear connector (task 026) against <see cref="FakeLinear"/>: Describe, the read into snapshot records,
/// sub-issues and projects, cycles, comments with replies, uploads, history as changes, paging within Linear's page
/// and complexity limits, delta cursors, scope, rate limits, and where the key may go.
/// </summary>
public sealed class LinearConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2025, 3, 20, 12, 0, 0, TimeSpan.Zero);

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Describe_reports_teams_people_types_states_by_type_and_fields()
    {
        var s = new Sample();
        var description = await Connect(s.Linear).DescribeAsync(Ct);

        description.Source.ShouldBe(new SnapshotSource { Kind = SourceKinds.Linear, BaseUrl = "https://linear.app/acme" });
        description.Projects.Select(p => (p.Id, p.Key, p.Name, p.Url)).ShouldBe(
        [
            (Id(s.Eng), "ENG", "Engineering", "https://linear.app/acme/team/ENG"),
            (Id(s.Ops), "OPS", "Operations", "https://linear.app/acme/team/OPS"),
        ], "an archived team isn't offered");

        description.People.ShouldBe(
        [
            new SourcePerson { Id = Id(s.Sofie), Name = "Sofie Larsen", Email = "sofie@acme.example", Handle = "sofie" },
            new SourcePerson { Id = Id(s.Jonas), Name = "Jonas Holm", Email = "jonas@acme.example", Handle = "jonas" },
            new SourcePerson { Id = Id(s.Mads), Name = "Mads Berg", Email = "mads@acme.example", Handle = "mads" },
        ], "disabled users too: their issues and comments keep an author");

        description.ItemTypes.Select(t => (t.Id, t.Level)).ShouldBe([(LinearConnector.ProjectType, 2), (LinearConnector.IssueType, 1)]);

        // Workflow states per team, in Linear's order, with their type as the category.
        description.Statuses.Where(st => st.Project == Id(s.Eng)).Select(st => (st.Name, st.Category)).ShouldBe(
        [
            ("Triage", "triage"), ("Backlog", "backlog"), ("Todo", "unstarted"), ("In Progress", "started"),
            ("In Review", "started"), ("Done", "completed"), ("Canceled", "canceled"),
        ]);
        description.Statuses.Where(st => st.Project == Id(s.Eng)).ShouldAllBe(st => st.ItemType == LinearConnector.IssueType);
        description.Statuses.Where(st => st.ItemType == LinearConnector.ProjectType).Select(st => (st.Name, st.Category, st.Project)).ShouldBe(
            [("Planned", "planned", (string?)null), ("In Progress", "started", null), ("Completed", "completed", null)]);

        description.Fields.Select(f => (f.Id, f.Kind)).ShouldBe(
            [(LinearConnector.ProjectField, FieldKind.Select), (LinearConnector.InitiativesField, FieldKind.MultiSelect)]);
    }

    [Fact]
    public async Task Read_maps_issues_sub_issues_projects_cycles_milestones_and_relations()
    {
        var s = new Sample();
        var records = await Connect(s.Linear).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        var items = records.OfType<SourceItem>().ToDictionary(i => i.Id);

        items.Values.Select(i => i.Title).ShouldBe(
            ["Checkout", "Old revamp", "Pay by card", "Test with Visa", "Visa 3DS", "Card payments", "Old banner", "Rotate keys"],
            ignoreOrder: true, "trashed issues and archived teams are left out; archived (closed) issues are not");

        var pay = items[s.Pay.Id];
        pay.Key.ShouldBe("ENG-1");
        pay.Project.ShouldBe(Id(s.Eng));
        pay.Type.ShouldBe(LinearConnector.IssueType);
        pay.Status.ShouldBe(Id(s.InProgress));
        pay.Title.ShouldBe("Pay by card");
        pay.Parent.ShouldBe(Id(s.Checkout), "a top-level issue sits under its project");
        pay.Assignee.ShouldBe(Id(s.Sofie));
        pay.Creator.ShouldBe(Id(s.Jonas));
        pay.Priority.ShouldBe("High");
        pay.Labels.ShouldBe(["Bug", "Type/Feature"], "a label in a group is named with its group");
        pay.Points.ShouldBe(3);
        pay.EstimateSeconds.ShouldBeNull("Linear has no time estimates");
        pay.Due.ShouldBe(new DateOnly(2025, 3, 20));
        pay.Sprint.ShouldBe(Id(s.Cycle2));
        pay.Versions.ShouldBe([Id(s.Beta)]);
        pay.Fields[LinearConnector.ProjectField].GetString().ShouldBe(Id(s.Checkout));
        pay.Links.Select(l => (l.Url, l.Title)).ShouldBe([("https://github.com/acme/web/pull/7", "PR #7")], "an uploaded attachment is a file, not a link");
        pay.DescriptionMarkup.ShouldBe(MarkupKind.Markdown);
        pay.Description.ShouldBe($"Card first.\n\n![flow.png]({s.Flow})");
        pay.Url.ShouldBe("https://linear.app/acme/issue/ENG-1/pay-by-kort");
        (pay.Created, pay.Updated, pay.Resolved).ShouldBe((T0, T0.AddDays(3), (DateTimeOffset?)null));

        // Sub-issues keep their parent issue, however deep; the pipeline fits the tree (ADR-019 Hierarchy fit).
        items[s.Visa.Id].Parent.ShouldBe(s.Pay.Id, "a parent issue wins over the project");
        items[s.Deep.Id].Parent.ShouldBe(s.Visa.Id);
        items[s.Dup.Id].Resolved.ShouldBe(T0.AddDays(3), "canceled counts as resolved");
        items[s.Dup.Id].Priority.ShouldBe("No priority");
        items[s.OldBanner.Id].Resolved.ShouldBe(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));
        items[s.Rotate.Id].Project.ShouldBe(Id(s.Ops));
        items[s.Rotate.Id].Parent.ShouldBe(Id(s.Checkout), "a project spanning teams is the parent in each");

        var checkout = items[Id(s.Checkout)];
        checkout.Type.ShouldBe(LinearConnector.ProjectType);
        checkout.Key.ShouldBeNull();
        checkout.Project.ShouldBe(Id(s.Eng), "read once, under the first of its teams");
        checkout.Status.ShouldBe(Id(s.Started));
        checkout.Description.ShouldBe("Pay faster\n\n## Goal\nOne-click");
        (checkout.Assignee, checkout.Creator, checkout.Priority).ShouldBe((Id(s.Sofie), Id(s.Jonas), "High"), "the lead is the assignee");
        (checkout.Start, checkout.Due).ShouldBe((new DateOnly(2025, 2, 1), new DateOnly(2025, 4, 30)), "the target date is the due date");
        checkout.Labels.ShouldBe(["Q2"]);
        checkout.Fields[LinearConnector.InitiativesField].EnumerateArray().Select(e => e.GetString()).ShouldBe(["Growth"]);
        items[Id(s.Revamp)].Resolved.ShouldBe(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));

        records.OfType<SourceSprint>().Select(sp => (sp.Id, sp.Name, sp.Project, sp.Start, sp.End, sp.Closed)).ShouldBe(
        [
            (Id(s.Cycle1), "Cycle 1", Id(s.Eng), new DateOnly(2025, 2, 15), new DateOnly(2025, 3, 1), true),
            (Id(s.Cycle2), "Sprint Two", Id(s.Eng), new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 15), false),
        ], "an unnamed cycle is called by its number");
        records.OfType<SourceVersion>().Select(v => (v.Id, v.Project, v.Name, v.Date, v.Released)).ShouldBe(
        [
            (Id(s.Beta), Id(s.Eng), "Beta", new DateOnly(2025, 3, 31), false),
            (Id(s.Alpha), Id(s.Eng), "Alpha", new DateOnly(2025, 2, 15), true),
        ], ignoreOrder: true);

        records.OfType<SourceRelation>().Select(r => (r.From, r.To, r.Kind, r.Name)).ShouldBe(
        [
            (s.Pay.Id, s.Visa.Id, RelationKinds.Blocks, "blocks"),
            (s.Pay.Id, s.Dup.Id, RelationKinds.Relates, "related"),
            (s.Dup.Id, s.Pay.Id, RelationKinds.Duplicates, "duplicate"),
        ], ignoreOrder: true, "each relation once, from its own issue; Linear's 'similar' suggestions are not relations");

        var people = records.OfType<SourcePerson>().ToList();
        people.Select(p => p.Id).ShouldBe([Id(s.Sofie), Id(s.Jonas)], ignoreOrder: true);
        records.IndexOf(people.Single(p => p.Id == Id(s.Sofie))).ShouldBeLessThan(records.IndexOf(checkout), "people come before records that use them");

        records[^1].ShouldBe(new ReadCursor(Utc(Now)));
    }

    [Fact]
    public async Task Comments_with_replies_and_uploads_come_across_and_files_download_with_the_key()
    {
        var s = new Sample();
        var connector = Connect(s.Linear);
        var records = await connector.ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);

        var comments = records.OfType<SourceComment>().OrderBy(c => c.Created).ToList();
        comments.Select(c => (c.Item, c.Author, c.Markup, c.Body)).ShouldBe(
        [
            (s.Pay.Id, Id(s.Jonas), MarkupKind.Markdown, "Looks good"),
            (s.Pay.Id, Id(s.Sofie), MarkupKind.Markdown, "Thanks, **shipping**"),
            (s.Pay.Id, Id(s.Jonas), MarkupKind.Markdown, $"Logs: [log.txt]({s.Log})"),
        ], "a reply is a comment like the others; the comment on a trashed issue is left out");
        comments[1].Updated.ShouldBe(T0.AddDays(2.5), "the edit time");
        comments[0].Updated.ShouldBeNull();

        var attachments = records.OfType<SourceAttachment>().ToDictionary(a => a.FileName);
        attachments.Keys.ShouldBe(["spec.pdf", "flow.png", "log.txt"], ignoreOrder: true);
        (attachments["spec.pdf"].Item, attachments["spec.pdf"].Author, attachments["spec.pdf"].Created, attachments["spec.pdf"].Url).ShouldBe(
            (s.Pay.Id, Id(s.Sofie), T0.AddDays(2), s.Spec));
        (attachments["flow.png"].Author, attachments["flow.png"].Created, attachments["flow.png"].Url).ShouldBe(
            (Id(s.Jonas), T0, s.Flow), "pasted into the description: by the issue's creator");
        (attachments["log.txt"].Item, attachments["log.txt"].Author, attachments["log.txt"].Created).ShouldBe(
            (s.Pay.Id, Id(s.Jonas), T0.AddDays(3)), "pasted into a comment: by its author");
        records.OfType<SourceAttachment>().Select(a => a.Id).ShouldBeUnique();

        await using (var file = await connector.FetchAttachmentAsync(attachments["flow.png"], Ct))
        {
            (await new StreamReader(file).ReadToEndAsync(Ct)).ShouldBe("PNG!");
        }

        s.Linear.Downloads.ShouldBe([$"GET {new Uri(s.Flow).AbsolutePath}"]);

        // An OAuth token works for uploads too.
        var oauth = Connect(s.Linear, OAuthToken, LinearAuth.OAuth);
        await using (var file = await oauth.FetchAttachmentAsync(attachments["log.txt"], Ct))
        {
            (await new StreamReader(file).ReadToEndAsync(Ct)).ShouldBe("LOG!");
        }
    }

    [Fact]
    public async Task History_replays_as_changes_in_linear_terms()
    {
        var s = new Sample();
        var records = await Connect(s.Linear).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        var changes = records.OfType<SourceChange>().Where(c => c.Item == s.Pay.Id).ToList();

        changes.Select(c => c.Id).ShouldBeUnique();
        changes.Select(c => c.Field).ShouldBe(
        [
            "status", "assignee", "labels",
            "priority", "points", "sprint", LinearConnector.ProjectField, "title", "due", "description", "labels",
        ], "one history entry can change several fields");

        var move = changes[0];
        (move.From, move.To, move.FromText, move.ToText, move.Actor, move.At).ShouldBe(
            (Id(s.Backlog), Id(s.InProgress), "Backlog", "In Progress", Id(s.Sofie), T0.AddDays(1)));
        move.Id.ShouldEndWith(":status");
        (changes[1].From, changes[1].To, changes[1].ToText).ShouldBe((null, Id(s.Sofie), "Sofie Larsen"));
        (changes[2].From, changes[2].To).ShouldBe((null, "Bug"));
        (changes[3].From, changes[3].To, changes[3].Actor).ShouldBe(("No priority", "High", Id(s.Jonas)));
        (changes[4].From, changes[4].To).ShouldBe((null, "3"));
        (changes[5].To, changes[5].ToText).ShouldBe((Id(s.Cycle2), "Sprint Two"));
        (changes[6].To, changes[6].ToText).ShouldBe((Id(s.Checkout), "Checkout"));
        (changes[7].From, changes[7].To).ShouldBe(("Pay by kort", "Pay by card"));
        (changes[8].From, changes[8].To).ShouldBe((null, "2025-03-20"));
        (changes[9].From, changes[9].To).ShouldBe((null, null), "description edits are a marker");
        (changes[10].To, changes[10].Id).ShouldBe(("Type/Feature", $"{changes[10].Id.Split(':')[0]}:labels:+{Id(s.Feature)}"));

        records.OfType<SourceChange>().Where(c => c.Item == s.Visa.Id).Select(c => (c.Field, c.From, c.To)).ShouldBe(
            [("parent", null, s.Pay.Id), ("project", Id(s.Ops), Id(s.Eng))], "a sub-issue moved under its parent, and between teams");
    }

    [Fact]
    public async Task Pages_stay_within_linear_limits_and_overflowing_nested_lists_are_paged_on_their_own()
    {
        var linear = new FakeLinear();
        var me = linear.AddUser("Sofie Larsen", "sofie", "sofie@acme.example");
        var team = linear.AddTeam("BIG", "Big");
        var todo = linear.AddState(team, "Todo", "unstarted");
        for (var i = 1; i <= 120; i++)
        {
            linear.AddIssue(team, $"Issue {i}", todo, T0.AddMinutes(i), me);
        }

        var (first, second, third) = (linear.Issues[0], linear.Issues[1], linear.Issues[2]);
        for (var h = 0; h < 30; h++)
        {
            first.Change(T0.AddHours(3).AddMinutes(h), me, new JsonObject { ["fromTitle"] = $"T{h}", ["toTitle"] = $"T{h + 1}" });
        }

        for (var r = 0; r < 15; r++)
        {
            second.Relate("related", linear.Issues[r + 3], T0.AddHours(3));
        }

        for (var a = 0; a < 12; a++)
        {
            second.Attach(T0.AddHours(3), me, $"https://example.com/doc/{a}", $"Doc {a}", "url");
        }

        for (var c = 0; c < 300; c++)
        {
            third.Comment(T0.AddHours(4).AddSeconds(c), me, $"Comment {c}");
        }

        var records = await Connect(linear).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);

        records.OfType<SourceItem>().Select(i => i.Id).ShouldBeUnique();
        records.OfType<SourceItem>().Count().ShouldBe(120);
        records.OfType<SourceChange>().Count(c => c.Item == first.Id).ShouldBe(30);
        records.OfType<SourceRelation>().Count(r => r.From == second.Id).ShouldBe(15);
        records.OfType<SourceItem>().Single(i => i.Id == second.Id).Links.Count.ShouldBe(12);
        records.OfType<SourceComment>().Select(c => c.Body).Distinct().Count().ShouldBe(300);

        int Count(string operation) => linear.Requests.Count(r => r.StartsWith(operation + " ", StringComparison.Ordinal));
        (Count("Issues"), Count("IssueHistory"), Count("IssueRelations"), Count("IssueAttachments"), Count("Comments")).ShouldBe((3, 1, 1, 1, 2));
        // A page of issues with their nested lists is the costliest query: near the cap, not over it (the fake rejects
        // a query over it, as Linear does).
        linear.Complexities.Max().ShouldBeInRange(5_000, MaxComplexity);
    }

    [Fact]
    public async Task A_delta_read_returns_only_what_changed_since_the_cursor()
    {
        var s = new Sample();
        var connector = Connect(s.Linear);

        var records = await connector.ReadAsync(new ReadScope(), Utc(T0.AddDays(2.75)), Ct).ToListAsync(Ct);

        records.OfType<SourceItem>().Select(i => i.Title).ShouldBe(["Pay by card", "Card payments"], ignoreOrder: true);
        records.OfType<SourceComment>().Select(c => c.Body).ShouldBe([$"Logs: [log.txt]({s.Log})"], "only comments written or edited since");
        records.OfType<SourceChange>().Count(c => c.Item == s.Pay.Id).ShouldBe(11, "a changed issue brings its whole history; re-runs skip what they have");
        records.OfType<SourceSprint>().Count().ShouldBe(2, "cycles are small and always read");
        records[^1].ShouldBe(new ReadCursor(Utc(Now)));
        s.Linear.Requests.ShouldContain(r => r.StartsWith("Issues ", StringComparison.Ordinal) && r.Contains("\"updatedAt\":{\"gte\":\"2025-03-04T03:00:00.000Z\"}", StringComparison.Ordinal));
        s.Linear.Requests.ShouldContain(r => r.StartsWith("Projects ", StringComparison.Ordinal) && r.Contains("\"gte\"", StringComparison.Ordinal));

        var nothing = await connector.ReadAsync(new ReadScope(), "2026-01-01T00:00:00Z", Ct).ToListAsync(Ct);
        nothing.OfType<SourceItem>().ShouldBeEmpty();
        nothing.OfType<SourceComment>().ShouldBeEmpty();
        nothing[^1].ShouldBe(new ReadCursor(Utc(Now)), "no change: the read's start, never earlier than it was");
    }

    [Fact]
    public async Task Scope_limits_teams_reads_a_named_archived_team_and_skips_items_resolved_before_the_cutoff()
    {
        var s = new Sample();
        var connector = Connect(s.Linear);

        var ops = await connector.ReadAsync(new ReadScope { Projects = [Id(s.Ops)] }, null, Ct).OfType<SourceItem>().ToListAsync(Ct);
        ops.Select(i => (i.Title, i.Project)).ShouldBe([("Checkout", Id(s.Ops)), ("Rotate keys", Id(s.Ops))], "a shared project goes under the team in scope");

        var old = await connector.ReadAsync(new ReadScope { Projects = [Id(s.Old)] }, null, Ct).OfType<SourceItem>().ToListAsync(Ct);
        old.Select(i => (i.Title, i.Project)).ShouldBe([("Legacy thing", Id(s.Old))]);

        var recent = await connector.ReadAsync(new ReadScope { ResolvedAfter = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) }, null, Ct).ToListAsync(Ct);
        recent.OfType<SourceItem>().Select(i => i.Title).ShouldNotContain("Old banner");
        recent.OfType<SourceItem>().Select(i => i.Title).ShouldNotContain("Old revamp");
        recent.OfType<SourceChange>().ShouldNotContain(c => c.Item == s.OldBanner.Id);
        recent.OfType<SourceItem>().Select(i => i.Title).ShouldContain("Card payments", "canceled this year");

        var missing = await Should.ThrowAsync<SourceAccessException>(
            () => connector.ReadAsync(new ReadScope { Projects = ["no-such-team"] }, null, Ct).ToListAsync(Ct).AsTask());
        missing.Status.ShouldBe(HttpStatusCode.NotFound);
        missing.TokenRejected.ShouldBeFalse();
    }

    [Fact]
    public async Task Rate_limited_requests_wait_for_retry_after_and_then_succeed()
    {
        var s = new Sample();
        s.Linear.Throttle = 2;
        var (connector, waits) = Clocked(s.Linear);

        (await connector.DescribeAsync(Ct)).Projects.Count.ShouldBe(2);

        waits.ShouldBe([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)]);
    }

    [Fact]
    public async Task Without_retry_after_the_reset_header_says_how_long_to_wait()
    {
        // Older answers: 400 with the RATELIMITED code and only the X-RateLimit-* headers.
        var s = new Sample();
        s.Linear.Throttle = 1;
        s.Linear.ThrottleStatus = HttpStatusCode.BadRequest;
        s.Linear.RetryAfter = null;
        var (connector, waits) = Clocked(s.Linear);
        s.Linear.RequestsReset = Now.AddMinutes(10);

        (await connector.DescribeAsync(Ct)).Projects.Count.ShouldBe(2);

        waits.ShouldBe([TimeSpan.FromMinutes(10)]);
    }

    [Fact]
    public async Task The_client_stops_before_the_hourly_requests_run_out()
    {
        var s = new Sample();
        var (connector, waits) = Clocked(s.Linear);
        s.Linear.RequestsRemaining = 2;
        s.Linear.RequestsReset = Now.AddMinutes(20);

        (await connector.DescribeAsync(Ct)).Projects.Count.ShouldBe(2);

        waits.ShouldBe([TimeSpan.FromMinutes(20)]);
        s.Linear.Throttled.ShouldBe(0, "it waited for the reset instead of running into the limit");
    }

    [Fact]
    public async Task A_rejected_key_or_token_fails_as_a_credential_problem_without_the_credential()
    {
        var key = await Should.ThrowAsync<SourceAccessException>(() => Connect(new Sample().Linear, "lin_api_wrong").DescribeAsync(Ct));
        key.TokenRejected.ShouldBeTrue();
        key.Message.ShouldContain("API key");
        key.Message.ShouldNotContain("lin_api_wrong");

        var token = await Should.ThrowAsync<SourceAccessException>(() => Connect(new Sample().Linear, "lin_oauth_wrong", LinearAuth.OAuth).DescribeAsync(Ct));
        token.TokenRejected.ShouldBeTrue();
        token.Message.ShouldContain("access token");
        token.Message.ShouldNotContain("lin_oauth_wrong");

        // The scheme matters: a key sent as a bearer token is refused.
        var swapped = await Should.ThrowAsync<SourceAccessException>(() => Connect(new Sample().Linear, ApiKey, LinearAuth.OAuth).DescribeAsync(Ct));
        swapped.TokenRejected.ShouldBeTrue();
        swapped.Message.ShouldNotContain(ApiKey);

        (await Connect(new Sample().Linear, OAuthToken, LinearAuth.OAuth).DescribeAsync(Ct)).Projects.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_key_is_never_sent_to_another_host()
    {
        var s = new Sample();
        var connector = Connect(s.Linear);
        await connector.ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        s.Linear.ForeignHosts.ShouldBeEmpty();

        // A file somewhere else is fetched without the key.
        s.Linear.Files["https://files.example/acme/report.pdf"] = "PDF?"u8.ToArray();
        var elsewhere = new SourceAttachment { Id = "x", Item = s.Pay.Id, FileName = "report.pdf", Created = T0, Url = "https://files.example/acme/report.pdf" };
        await using (var file = await connector.FetchAttachmentAsync(elsewhere, Ct))
        {
            (await new StreamReader(file).ReadToEndAsync(Ct)).ShouldBe("PDF?");
        }

        // A host that only starts like Linear's upload host gets no key, and plain http is refused before any request.
        var lookalike = elsewhere with { Url = "https://uploads.linear.app.evil.example/org/a/b" };
        await Should.ThrowAsync<SourceAccessException>(() => connector.FetchAttachmentAsync(lookalike, Ct));
        await Should.ThrowAsync<SourceAccessException>(() => connector.FetchAttachmentAsync(elsewhere with { Url = s.Flow.Replace("https:", "http:", StringComparison.Ordinal) }, Ct));

        s.Linear.ForeignHosts.ShouldBe(["files.example", "uploads.linear.app.evil.example"]);
        s.Linear.Leaks.ShouldBeEmpty();
        s.Linear.Downloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_writes_a_snapshot_with_every_record_and_file()
    {
        var zip = new MemoryStream();
        await Connect(new Sample().Linear).WriteSnapshotAsync(zip, new ReadScope(), null, includeFiles: true, new TestClock(Now), Ct);
        zip.Position = 0;

        await using var snapshot = await SnapshotReader.OpenAsync(zip, Ct);

        snapshot.Header.Source.Kind.ShouldBe(SourceKinds.Linear);
        snapshot.Header.Counts.ShouldBe(new SnapshotCounts { Items = 8, Relations = 3, Comments = 3, Attachments = 3, Changes = 14 });
        snapshot.Header.People.Count.ShouldBe(3);
        snapshot.Header.Sprints.Count.ShouldBe(2);
        snapshot.Header.Versions.Count.ShouldBe(2);
        snapshot.Header.Cursor.ShouldBe(Utc(Now));
        (await snapshot.ReadAttachmentsAsync(Ct).ToListAsync(Ct)).ShouldAllBe(a => a.File != null);
        var items = await snapshot.ReadItemsAsync(Ct).ToListAsync(Ct);
        items.Single(i => i.Key == "ENG-1").Labels.ShouldBe(["Bug", "Type/Feature"]);
    }

    private static LinearConnector Connect(
        FakeLinear linear, string credential = ApiKey, LinearAuth auth = LinearAuth.ApiKey, TimeProvider? clock = null)
    {
        var connector = new LinearConnector(linear.Client(), credential, auth, clock ?? new TestClock(Now));
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        return connector;
    }

    /// <summary>A connector whose waits move a shared clock forward, so rate-limit resets pass without sleeping.</summary>
    private static (LinearConnector Connector, List<TimeSpan> Waits) Clocked(FakeLinear linear)
    {
        var clock = new TestClock(Now);
        var waits = new List<TimeSpan>();
        linear.Clock = () => clock.Now;
        linear.RequestsReset = Now.AddHours(1);
        var connector = Connect(linear, clock: clock);
        connector.Client.Delay = (wait, _) =>
        {
            waits.Add(wait);
            clock.Now += wait;
            return Task.CompletedTask;
        };
        return (connector, waits);
    }

    /// <summary>
    /// Two teams and an archived one. Engineering holds the interesting issues: one with every field, uploads, links,
    /// a comment thread and a history; a sub-issue tree; a canceled duplicate; a closed, archived one; a trashed one.
    /// Checkout is a project across both teams, with milestones and an initiative.
    /// </summary>
    private sealed class Sample
    {
        public Sample()
        {
            Sofie = Linear.AddUser("Sofie Larsen", "sofie", "sofie@acme.example");
            Jonas = Linear.AddUser("Jonas Holm", "jonas", "jonas@acme.example");
            Mads = Linear.AddUser("Mads Berg", "mads", "mads@acme.example", active: false);

            Eng = Linear.AddTeam("ENG", "Engineering");
            Ops = Linear.AddTeam("OPS", "Operations");
            Old = Linear.AddTeam("OLD", "Legacy", archived: T0.AddDays(-100));

            var triage = Linear.AddState(Eng, "Triage", "triage");
            Backlog = Linear.AddState(Eng, "Backlog", "backlog");
            var todo = Linear.AddState(Eng, "Todo", "unstarted");
            InProgress = Linear.AddState(Eng, "In Progress", "started");
            Linear.AddState(Eng, "In Review", "started");
            var done = Linear.AddState(Eng, "Done", "completed");
            var canceled = Linear.AddState(Eng, "Canceled", "canceled");
            var opsTodo = Linear.AddState(Ops, "Todo", "unstarted");
            Linear.AddState(Ops, "Done", "completed");
            var oldDone = Linear.AddState(Old, "Done", "completed");

            Bug = Linear.AddLabel("Bug");
            var type = Linear.AddLabel("Type", isGroup: true);
            Feature = Linear.AddLabel("Feature", group: type);
            Linear.AddLabel("Infra", team: Eng);

            Linear.AddProjectStatus("Planned", "planned");
            Started = Linear.AddProjectStatus("In Progress", "started");
            var completed = Linear.AddProjectStatus("Completed", "completed");

            Cycle1 = Linear.AddCycle(Eng, 1, T0.AddDays(-14), T0, completed: T0);
            Cycle2 = Linear.AddCycle(Eng, 2, T0, T0.AddDays(14), name: "Sprint Two");

            Checkout = Linear.AddProject("Checkout", T0.AddDays(-10), Started, Jonas, Eng, Ops);
            Checkout["description"] = "Pay faster";
            Checkout["content"] = "## Goal\nOne-click";
            Checkout["lead"] = Ref(Sofie);
            Checkout["startDate"] = "2025-02-01";
            Checkout["targetDate"] = "2025-04-30";
            Checkout["priority"] = 2;
            Checkout["labels"] = new JsonArray(new JsonObject { ["id"] = Linear.NextId(), ["name"] = "Q2" });
            Checkout["initiatives"] = new JsonArray(new JsonObject { ["id"] = Linear.NextId(), ["name"] = "Growth" });
            Beta = Linear.AddMilestone(Checkout, "Beta", "2025-03-31", "next");
            Alpha = Linear.AddMilestone(Checkout, "Alpha", "2025-02-15", "done");

            Revamp = Linear.AddProject("Old revamp", T0.AddDays(-300), completed, Sofie, Eng);
            Revamp["completedAt"] = "2024-06-01T00:00:00.000Z";
            Revamp["updatedAt"] = "2024-06-01T00:00:00.000Z";

            Flow = Linear.Upload("PNG!"u8.ToArray());
            Spec = Linear.Upload("PDF!"u8.ToArray());
            Log = Linear.Upload("LOG!"u8.ToArray());

            Pay = Linear.AddIssue(Eng, "Pay by kort", Backlog, T0, Jonas)
                .Set("description", $"Card first.\n\n![flow.png]({Flow})")
                .Set("projectMilestone", Ref(Beta))
                .Change(T0.AddDays(1), Sofie, new JsonObject { ["fromStateId"] = Id(Backlog), ["toStateId"] = Id(InProgress), ["toAssigneeId"] = Id(Sofie) })
                .Change(T0.AddDays(1), Sofie, new JsonObject { ["addedLabelIds"] = new JsonArray(Id(Bug)) })
                .Change(T0.AddDays(2), Jonas, new JsonObject
                {
                    ["fromTitle"] = "Pay by kort",
                    ["toTitle"] = "Pay by card",
                    ["fromPriority"] = 0,
                    ["toPriority"] = 2,
                    ["toEstimate"] = 3,
                    ["toCycleId"] = Id(Cycle2),
                    ["toProjectId"] = Id(Checkout),
                    ["toDueDate"] = "2025-03-20",
                    ["updatedDescription"] = true,
                    ["addedLabelIds"] = new JsonArray(Id(Feature)),
                })
                .Attach(T0.AddDays(2), Jonas, "https://github.com/acme/web/pull/7", "PR #7", "github")
                .Attach(T0.AddDays(2), Sofie, Spec, "spec.pdf");
            var thread = Pay.Comment(T0.AddDays(1), Jonas, "Looks good");
            Pay.Comment(T0.AddDays(2), Sofie, "Thanks, **shipping**", parent: thread, edited: T0.AddDays(2.5));
            Pay.Comment(T0.AddDays(3), Jonas, $"Logs: [log.txt]({Log})");

            Visa = Linear.AddIssue(Ops, "Test with Visa", todo, T0.AddDays(1), Sofie)
                .Set("assignee", Ref(Jonas))
                .Set("project", Ref(Checkout))
                .Change(T0.AddDays(1.5), Sofie, new JsonObject { ["toParentId"] = Pay.Id })
                .Change(T0.AddDays(1.5), Sofie, new JsonObject { ["fromTeamId"] = Id(Ops), ["toTeamId"] = Id(Eng) })
                .Set("team", Ref(Eng))
                .Set("identifier", "ENG-9");
            Deep = Linear.AddIssue(Eng, "Visa 3DS", todo, T0.AddDays(1), Sofie).Set("parent", new JsonObject { ["id"] = Visa.Id });
            Dup = Linear.AddIssue(Eng, "Card payments", canceled, T0.AddDays(2), Jonas)
                .Set("canceledAt", Time(T0.AddDays(3)))
                .Relate("duplicate", Pay, T0.AddDays(3));
            Pay.Relate("blocks", Visa, T0.AddDays(2)).Relate("related", Dup, T0.AddDays(3)).Relate("similar", Deep, T0.AddDays(3));

            OldBanner = Linear.AddIssue(Eng, "Old banner", todo, new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero), Sofie)
                .Change(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), Sofie, new JsonObject { ["fromStateId"] = Id(todo), ["toStateId"] = Id(done) })
                .Set("completedAt", "2024-06-01T00:00:00.000Z")
                .Set("archivedAt", "2024-09-01T00:00:00.000Z");

            var spam = Linear.AddIssue(Eng, "Spam", triage, T0.AddDays(1), null).Set("trashed", true);
            spam.Comment(T0.AddDays(1), Jonas, "Spam?");

            Rotate = Linear.AddIssue(Ops, "Rotate keys", opsTodo, T0.AddDays(1), Sofie).Set("project", Ref(Checkout));
            Linear.AddIssue(Old, "Legacy thing", oldDone, T0.AddDays(-200), Sofie);
        }

        public FakeLinear Linear { get; } = new();
        public JsonObject Sofie { get; }
        public JsonObject Jonas { get; }
        public JsonObject Mads { get; }
        public JsonObject Eng { get; }
        public JsonObject Ops { get; }
        public JsonObject Old { get; }
        public JsonObject Backlog { get; }
        public JsonObject InProgress { get; }
        public JsonObject Bug { get; }
        public JsonObject Feature { get; }
        public JsonObject Started { get; }
        public JsonObject Cycle1 { get; }
        public JsonObject Cycle2 { get; }
        public JsonObject Checkout { get; }
        public JsonObject Revamp { get; }
        public JsonObject Beta { get; }
        public JsonObject Alpha { get; }
        public string Flow { get; }
        public string Spec { get; }
        public string Log { get; }
        public FakeIssue Pay { get; }
        public FakeIssue Visa { get; }
        public FakeIssue Deep { get; }
        public FakeIssue Dup { get; }
        public FakeIssue OldBanner { get; }
        public FakeIssue Rotate { get; }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
