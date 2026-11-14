using System.Text.Json.Nodes;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.GitHub;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>
/// The GitHub Issues connector (task 027) against <see cref="FakeGitHub"/>: Describe, the read into snapshot records
/// (pull requests skipped, sub-issue parents, close reasons, milestones), events as changes, deltas, rate limits, and
/// GitHub's default mapping.
/// </summary>
public sealed class GitHubConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "https://github.com/", "https://api.github.com/")]
    [InlineData("https://github.com/acme", "https://github.com/", "https://api.github.com/")]
    [InlineData("https://ghe.acme.example", "https://ghe.acme.example/", "https://ghe.acme.example/api/v3/")]
    [InlineData("https://ghe.acme.example/api/v3/", "https://ghe.acme.example/", "https://ghe.acme.example/api/v3/")]
    [InlineData("https://acme.ghe.com", "https://acme.ghe.com/", "https://api.acme.ghe.com/")]
    [InlineData("https://api.acme.ghe.com/", "https://acme.ghe.com/", "https://api.acme.ghe.com/")]
    public void The_address_is_github_or_an_enterprise_server(string? url, string web, string api)
    {
        var (w, a) = GitHubConnector.Addresses(url);
        (w.AbsoluteUri, a.AbsoluteUri).ShouldBe((web, api));
    }

    [Fact]
    public async Task Describe_reports_repositories_with_issues_org_types_statuses_and_people()
    {
        var github = Sample();

        var description = await Connect(github).DescribeAsync(Ct);

        description.Source.ShouldBe(new SnapshotSource { Kind = SourceKinds.GitHub, BaseUrl = "https://github.com" });
        description.Projects.Select(p => (p.Id, p.Key, p.Name)).ShouldBe([("100", "api", "acme/api"), ("200", "notes", "sofie/notes")], "a repository without issues is left out");
        description.ItemTypes.Select(t => (t.Id, t.Name)).ShouldBe([("issue", "Issue"), ("7", "Bug"), ("8", "Feature")], "a disabled type is left out");
        description.Statuses.Select(s => s.Id).ShouldBe([GitHubConnector.Open, GitHubConnector.Closed, GitHubConnector.NotPlanned, GitHubConnector.Duplicate]);
        description.People.Select(p => (p.Id, p.Handle, p.Bot)).ShouldBe([("1", "sofie", false), ("2", "jonas", false), ("3", "dependabot[bot]", true)], ignoreOrder: true);
        github.Requests.ShouldNotContain(r => r.StartsWith("orgs/sofie", StringComparison.Ordinal), "a user's repositories have no org types");
    }

    [Fact]
    public async Task A_secondary_limit_named_only_in_the_body_waits_a_minute()
    {
        var github = Sample();
        github.SecondaryLimitOnce = true;
        var waits = new List<TimeSpan>();
        var clock = new Clock { Now = T0 };
        var connector = new GitHubConnector(new HttpClient(github), null, FakeGitHub.Token, clock);
        connector.Client.Delay = (wait, _) =>
        {
            waits.Add(wait);
            clock.Now += wait;
            return Task.CompletedTask;
        };

        (await connector.DescribeAsync(Ct)).Projects.ShouldNotBeEmpty();
        waits.ShouldBe([TimeSpan.FromMinutes(1)]);
    }

    [Fact]
    public async Task Read_skips_pull_requests_and_keeps_numbers_parents_close_reasons_and_milestones()
    {
        var github = Sample();
        github.RateLimitOnce = true;
        var waits = new List<TimeSpan>();
        var clock = new Clock { Now = T0.AddDays(10) };
        var connector = new GitHubConnector(new HttpClient(github), null, FakeGitHub.Token, clock);
        connector.Client.Delay = (wait, _) =>
        {
            waits.Add(wait);
            clock.Now += wait;
            return Task.CompletedTask;
        };

        var records = await connector.ReadAsync(new ReadScope { Projects = ["100"] }, null, Ct).ToListAsync(Ct);
        var items = records.OfType<SourceItem>().ToDictionary(i => i.Key!);

        waits.ShouldHaveSingleItem().ShouldBeGreaterThan(TimeSpan.Zero, "the primary rate limit waits for its reset");
        items.Keys.ShouldBe(["acme/api#1", "acme/api#2", "acme/api#3", "acme/api#5"], ignoreOrder: true, "#4 is a pull request");
        var epic = items["acme/api#1"];
        epic.Type.ShouldBe("8");
        epic.Project.ShouldBe("100");
        epic.Status.ShouldBe(GitHubConnector.Open);
        epic.Description.ShouldBe("Pay **twice**");
        epic.DescriptionMarkup.ShouldBe(MarkupKind.Markdown);
        epic.Url.ShouldBe("https://github.com/acme/api/issues/1");

        var bug = items["acme/api#2"];
        bug.Parent.ShouldBe("1001", "a sub-issue of #1");
        bug.Type.ShouldBe("7");
        bug.Status.ShouldBe(GitHubConnector.Closed);
        bug.Resolved.ShouldBe(T0.AddDays(3));
        bug.Labels.ShouldBe(["payments", "p1"]);
        bug.Assignee.ShouldBe("2");
        bug.Fields[GitHubConnector.AssigneesField].EnumerateArray().Select(e => e.GetString()).ShouldBe(["jonas", "sofie"]);
        bug.Versions.ShouldBe(["900"]);

        items["acme/api#3"].Status.ShouldBe(GitHubConnector.NotPlanned);
        items["acme/api#3"].Type.ShouldBe(GitHubConnector.PlainIssueType);
        items["acme/api#3"].Parent.ShouldBe("1001");
        items["acme/api#5"].Status.ShouldBe(GitHubConnector.Duplicate);

        records.OfType<SourceRelation>().Select(r => (r.From, r.To, r.Kind)).ShouldBe([("1003", "1002", RelationKinds.Blocks)]);
        github.Requests.Count(r => r.Contains("/dependencies/", StringComparison.Ordinal)).ShouldBe(1, "only the blocked issue is asked");
        records.OfType<SourceVersion>().Select(v => (v.Id, v.Name, v.Date, v.Released)).ShouldBe(
            [("900", "v1.0", new DateOnly(2025, 4, 1), false), ("901", "Someday", (DateOnly?)null, true)]);
        records.OfType<SourcePerson>().Select(p => p.Handle).ShouldBe(["sofie", "jonas"], ignoreOrder: true, "people the read saw");

        var comments = records.OfType<SourceComment>().ToList();
        comments.Select(c => (c.Id, c.Item, c.Author)).ShouldBe([("501", "1002", "2")], "a pull request's comment is left out");
        comments[0].Markup.ShouldBe(MarkupKind.Markdown);

        // Labels are worked out backwards from the labels now: p1 came last, payments before it.
        var changes = records.OfType<SourceChange>().Where(c => c.Item == "1002").ToList();
        changes.Select(c => (c.Field, c.From ?? c.FromText, c.To ?? c.ToText)).ShouldBe(
        [
            ("labels", "", "payments"),
            ("assignee", null, "2"),
            ("labels", "payments", "p1, payments"),
            ("title", "Pay once", "Pay twice fails"),
            ("status", GitHubConnector.Open, GitHubConnector.Closed),
        ]);
        changes[1].Actor.ShouldBe("1");
        records.OfType<SourceChange>().Single(c => c.Item == "1003").To.ShouldBe(GitHubConnector.NotPlanned);
        records.OfType<ReadCursor>().Single().Cursor.ShouldBe(T0.AddDays(10).UtcDateTime.ToString("O"), "when the read started");
    }

    [Fact]
    public async Task A_delta_reads_what_changed_since_the_cursor()
    {
        var github = Sample();

        var records = await Connect(github).ReadAsync(new ReadScope { Projects = ["100"] }, T0.AddDays(3).ToString("O"), Ct).ToListAsync(Ct);

        github.Requests.ShouldContain(r => r.StartsWith("repos/acme/api/issues?", StringComparison.Ordinal) && r.Contains("since=2025-03-04T09:00:00Z", StringComparison.Ordinal));
        records.OfType<SourceItem>().Select(i => i.Key).ShouldBe(["acme/api#2", "acme/api#5"], ignoreOrder: true);
        records.OfType<SourceItem>().Single(i => i.Key == "acme/api#2").Parent.ShouldBe("1001", "its parent didn't change, so it is asked for");
        records.OfType<SourceChange>().Select(c => c.Field).ShouldBe(["status"], "only events since the cursor");
    }

    [Fact]
    public async Task A_rejected_token_and_an_unknown_repository_say_so()
    {
        var github = Sample();
        var bad = new GitHubConnector(new HttpClient(github), null, "nope");
        (await Should.ThrowAsync<SourceAccessException>(() => bad.DescribeAsync(Ct))).Message.ShouldContain("rejected the token");

        var error = await Should.ThrowAsync<SourceAccessException>(async () => await Connect(github).ReadAsync(new ReadScope { Projects = ["999"] }, null, Ct).ToListAsync(Ct));
        error.Message.ShouldContain("no repository 999");
    }

    [Fact]
    public async Task The_default_mapping_cancels_not_planned_and_duplicates_and_marks_bugs()
    {
        var stream = new MemoryStream();
        await Connect(Sample()).WriteSnapshotAsync(stream, new ReadScope(), null, includeFiles: false, TimeProvider.System, Ct);
        stream.Position = 0;
        await using var snapshot = await SnapshotReader.OpenAsync(stream, Ct);

        var mapping = ImportMappingDefaults.Propose(snapshot.Header, []);

        mapping.States.ShouldBe(new Dictionary<string, IssueState>
        {
            [GitHubConnector.Open] = IssueState.Todo,
            [GitHubConnector.Closed] = IssueState.Done,
            [GitHubConnector.NotPlanned] = IssueState.Canceled,
            [GitHubConnector.Duplicate] = IssueState.Canceled,
        }, ignoreOrder: true);
        mapping.Types["7"].ShouldBe(new TypeTarget(IssueType.Task, "bug"));
        mapping.Types["8"].Type.ShouldBe(IssueType.Feature);
        mapping.Types[GitHubConnector.PlainIssueType].Type.ShouldBe(IssueType.Task);
        mapping.Projects["100"].Key.ShouldBe("API");
        mapping.MilestoneVersions.ShouldBe(["900"]);
        mapping.People["3"].Action.ShouldBe(PersonAction.Skip, "a bot");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static GitHubConnector Connect(FakeGitHub github)
    {
        var connector = new GitHubConnector(new HttpClient(github), null, FakeGitHub.Token);
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        return connector;
    }

    /// <summary>
    /// acme/api: #1 a Feature with sub-issues #2 (a closed Bug, two assignees, a milestone, events) and #3 (closed as not
    /// planned), #4 a pull request, #5 closed as duplicate. sofie/notes is a user's repository; acme/wiki has issues off.
    /// </summary>
    internal static FakeGitHub Sample()
    {
        var github = new FakeGitHub();
        github.AddRepo(100, "acme", "api");
        github.AddRepo(200, "sofie", "notes", ownerType: "User");
        github.AddRepo(300, "acme", "wiki", hasIssues: false);
        github.IssueTypes["acme"] =
        [
            new JsonObject { ["id"] = 7, ["name"] = "Bug", ["is_enabled"] = true },
            new JsonObject { ["id"] = 8, ["name"] = "Feature", ["is_enabled"] = true },
            new JsonObject { ["id"] = 9, ["name"] = "Old", ["is_enabled"] = false },
        ];
        github.Assignees["acme/api"] = [FakeGitHub.User(1, "sofie"), FakeGitHub.User(2, "jonas"), FakeGitHub.User(3, "dependabot[bot]", "Bot")];
        github.Assignees["sofie/notes"] = [FakeGitHub.User(1, "sofie")];
        github.Milestones["acme/api"] =
        [
            new JsonObject { ["id"] = 900, ["number"] = 1, ["title"] = "v1.0", ["state"] = "open", ["due_on"] = "2025-04-01T07:00:00Z" },
            new JsonObject { ["id"] = 901, ["number"] = 2, ["title"] = "Someday", ["state"] = "closed", ["due_on"] = null },
        ];

        var feature = github.AddIssue("acme/api", 1001, 1, "Payments", T0);
        feature["body"] = "Pay **twice**";
        feature["type"] = new JsonObject { ["id"] = 8, ["name"] = "Feature" };
        feature["sub_issues_summary"] = new JsonObject { ["total"] = 2, ["completed"] = 2 };

        var bug = github.AddIssue("acme/api", 1002, 2, "Pay twice fails", T0.AddHours(1), T0.AddDays(4));
        bug["type"] = new JsonObject { ["id"] = 7, ["name"] = "Bug" };
        bug["state"] = "closed";
        bug["state_reason"] = "completed";
        bug["closed_at"] = T0.AddDays(3);
        bug["labels"] = new JsonArray(FakeGitHub.Label("payments"), FakeGitHub.Label("p1"));
        bug["assignees"] = new JsonArray(FakeGitHub.User(2, "jonas"), FakeGitHub.User(1, "sofie"));
        bug["milestone"] = new JsonObject { ["id"] = 900, ["number"] = 1, ["title"] = "v1.0", ["state"] = "open", ["due_on"] = "2025-04-01T07:00:00Z" };

        var dropped = github.AddIssue("acme/api", 1003, 3, "Pay by fax", T0.AddHours(2), T0.AddDays(2));
        dropped["state"] = "closed";
        dropped["state_reason"] = "not_planned";
        dropped["closed_at"] = T0.AddDays(2);

        var pull = github.AddIssue("acme/api", 1004, 4, "Fix pay", T0.AddHours(3));
        pull["pull_request"] = new JsonObject { ["url"] = "https://api.github.com/repos/acme/api/pulls/4" };

        var duplicate = github.AddIssue("acme/api", 1005, 5, "Pay twice (again)", T0.AddHours(4), T0.AddDays(3).AddHours(1));
        duplicate["state"] = "closed";
        duplicate["state_reason"] = "duplicate";
        duplicate["closed_at"] = T0.AddDays(3).AddHours(1);

        github.SubIssues["acme/api#1"] = [2, 3];
        bug["issue_dependencies_summary"] = new JsonObject { ["total_blocked_by"] = 1, ["blocking"] = 0 };
        github.BlockedBy["acme/api#2"] = [3];
        github.Comments["acme/api"] =
        [
            new JsonObject { ["id"] = 501, ["issue_url"] = "https://api.github.com/repos/acme/api/issues/2", ["user"] = FakeGitHub.User(2, "jonas"), ["body"] = "Seen it", ["created_at"] = T0.AddDays(1), ["updated_at"] = T0.AddDays(1) },
            new JsonObject { ["id"] = 502, ["issue_url"] = "https://api.github.com/repos/acme/api/issues/4", ["user"] = FakeGitHub.User(1, "sofie"), ["body"] = "LGTM", ["created_at"] = T0.AddDays(1), ["updated_at"] = T0.AddDays(1) },
        ];

        JsonObject Event(long id, int number, string kind, DateTimeOffset at, Action<JsonObject>? set = null)
        {
            var e = new JsonObject { ["id"] = id, ["event"] = kind, ["actor"] = FakeGitHub.User(1, "sofie"), ["created_at"] = at, ["issue"] = new JsonObject { ["id"] = 1000 + number, ["number"] = number } };
            set?.Invoke(e);
            return e;
        }

        // Newest first, as GitHub lists them.
        github.Events["acme/api"] =
        [
            Event(7006, 2, "closed", T0.AddDays(3), e => e["state_reason"] = "completed"),
            Event(7005, 3, "closed", T0.AddDays(2), e => e["state_reason"] = "not_planned"),
            Event(7004, 2, "renamed", T0.AddHours(5), e => e["rename"] = new JsonObject { ["from"] = "Pay once", ["to"] = "Pay twice fails" }),
            Event(7003, 2, "labeled", T0.AddHours(4), e => e["label"] = FakeGitHub.Label("p1")),
            Event(7002, 2, "assigned", T0.AddHours(3), e => e["assignee"] = FakeGitHub.User(2, "jonas")),
            Event(7001, 2, "labeled", T0.AddHours(2), e => e["label"] = FakeGitHub.Label("payments")),
            Event(7000, 4, "closed", T0.AddHours(2)),
        ];
        return github;
    }
}
