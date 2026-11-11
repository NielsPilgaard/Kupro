using System.Globalization;
using System.Text.Json.Nodes;
using Kupro.Import;
using Kupro.Import.Snapshot;
using Kupro.Import.Trello;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>
/// The Trello connector (task 026) against <see cref="FakeTrello"/>: Describe, the read into snapshot records,
/// checklists, attachments, actions as comments and changes, paging past 1,000 cards and actions, delta cursors,
/// scope, throttling, and where the key and token may go.
/// </summary>
public sealed class TrelloConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2025, 3, 20, 12, 0, 0, TimeSpan.Zero);

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Describe_reports_open_boards_with_lists_people_types_and_fields()
    {
        var s = new Sample();
        var description = await Connect(s.Trello).DescribeAsync(Ct);

        description.Source.ShouldBe(new SnapshotSource { Kind = SourceKinds.Trello, BaseUrl = "https://trello.com" });
        description.Projects.Select(p => (p.Id, p.Name)).ShouldBe([(s.Web.Id, "Website"), (s.Marketing.Id, "Marketing")], "archived boards aren't offered");
        description.Projects[0].Url.ShouldStartWith("https://trello.com/b/");

        description.People.ShouldBe(
            [
                new SourcePerson { Id = s.Sofie.Id, Name = "Sofie Larsen", Email = "sofie@acme.example", Handle = s.Sofie.Username },
                new SourcePerson { Id = s.Jonas.Id, Name = "Jonas Holm", Handle = "jonas" },
            ],
            ignoreOrder: true);

        description.ItemTypes.Select(t => (t.Id, t.Level)).ShouldBe([(TrelloConnector.CardType, 1), (TrelloConnector.CheckItemType, 0)]);

        // Lists are the states, per board; Trello has no category beyond an archived list.
        var lists = description.Statuses.Where(st => st.Project == s.Web.Id).ToList();
        lists.Select(l => l.Name).ShouldBe(["To do", "Doing", "Review", "Done", "Old"]);
        lists.ShouldAllBe(l => l.ItemType == TrelloConnector.CardType);
        lists.Single(l => l.Name == "Old").Category.ShouldBe("archived");
        lists.Single(l => l.Name == "Doing").Category.ShouldBeNull();
        description.Statuses.Where(st => st.ItemType == TrelloConnector.CheckItemType).Select(st => st.Id).ShouldBe(["checkItem/incomplete", "checkItem/complete"]);

        var fields = description.Fields.ToDictionary(f => f.Name);
        fields.Keys.ShouldBe(["Archived", "Due complete", "Members", "Priority", "Points", "Go-live", "Approved", "Notes"]);
        fields["Priority"].Kind.ShouldBe(FieldKind.Select);
        fields["Priority"].Options.ShouldBe(["High", "Low"]);
        fields["Points"].Kind.ShouldBe(FieldKind.Number);
        fields["Go-live"].Kind.ShouldBe(FieldKind.Date);
        fields["Approved"].Kind.ShouldBe(FieldKind.Select);
        fields["Notes"].Kind.ShouldBe(FieldKind.Text);
        fields["Members"].Kind.ShouldBe(FieldKind.MultiSelect);

        s.Trello.Requests.ShouldContain(r => r.Contains("/labels?", StringComparison.Ordinal) && r.Contains("limit=1000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_maps_cards_with_lists_members_labels_dates_fields_and_checklists()
    {
        var s = new Sample();
        var records = await Connect(s.Trello).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        var items = records.OfType<SourceItem>().ToDictionary(i => i.Id);

        items.Values.Select(i => i.Title).ShouldBe(["Pay by card", "Test with Visa", "Old banner", "Hero image", "Newsletter"], ignoreOrder: true);

        var card = items[s.Pay.Id];
        card.Key.ShouldBe("1");
        card.Project.ShouldBe(s.Web.Id);
        card.Type.ShouldBe(TrelloConnector.CardType);
        card.Status.ShouldBe(s.Doing);
        card.Assignee.ShouldBe(s.Sofie.Id, "the first member");
        card.Creator.ShouldBe(s.Jonas.Id, "from the createCard action");
        card.Labels.ShouldBe(["green", "bug"], "a colour-only label is named by its colour");
        card.Start.ShouldBe(new DateOnly(2025, 3, 3));
        card.Due.ShouldBe(new DateOnly(2025, 3, 20));
        card.Created.ShouldBe(T0, "a Trello ID holds its creation time");
        card.Updated.ShouldBe(T0.AddDays(4));
        card.Resolved.ShouldBeNull();
        card.Url.ShouldStartWith("https://trello.com/c/");
        card.Priority.ShouldBeNull();
        card.DescriptionMarkup.ShouldBe(MarkupKind.Markdown);
        card.Description.ShouldBe(
            "Card first, then **Visa**.\n\n## Steps\n\n- [x] Wire gateway\n- [ ] Test with Visa (@jonas, 2025-03-18)",
            "checklists follow as task lists, in pos order, with an item's member and due date");
        card.Links.Select(l => (l.Url, l.Title)).ShouldBe([("https://wiki.acme.example/cards", "Spec")]);

        card.Fields[TrelloConnector.MembersField].EnumerateArray().Select(e => e.GetString()).ShouldBe([s.Sofie.Id, s.Jonas.Id]);
        card.Fields[s.Priority].GetString().ShouldBe("High");
        card.Fields[s.Points].GetDecimal().ShouldBe(5);
        card.Fields[s.GoLive].GetString().ShouldBe("2025-05-01");
        card.Fields[s.Approved].GetString().ShouldBe("true");
        card.Fields[s.Notes].GetString().ShouldBe("Ask finance");
        card.Fields.ShouldNotContainKey(TrelloConnector.ArchivedField);

        // A checklist item with its own member or due date also comes as an item under the card.
        var checkItem = items.Values.Single(i => i.Type == TrelloConnector.CheckItemType);
        (checkItem.Title, checkItem.Parent, checkItem.Assignee, checkItem.Due, checkItem.Status).ShouldBe(
            ("Test with Visa", s.Pay.Id, s.Jonas.Id, new DateOnly(2025, 3, 18), "checkItem/incomplete"));
        records.IndexOf(checkItem).ShouldBeGreaterThan(records.IndexOf(card));

        var banner = items[s.Banner.Id];
        banner.Resolved.ShouldBe(T0.AddDays(6), "archived: the archive action's time");
        banner.Fields[TrelloConnector.ArchivedField].GetString().ShouldBe("true");
        banner.Fields[TrelloConnector.DueCompleteField].GetString().ShouldBe("true");
        banner.Status.ShouldBe(s.Done);
        items[s.Hero.Id].Labels.ShouldBe(["Design"]);
        items[s.Hero.Id].Description.ShouldBeNull();
        items[s.Hero.Id].Fields.ShouldBeEmpty();

        var people = records.OfType<SourcePerson>().ToList();
        people.Select(p => p.Id).ShouldBe([s.Sofie.Id, s.Jonas.Id], ignoreOrder: true);
        people.Single(p => p.Id == s.Sofie.Id).Email.ShouldBe("sofie@acme.example", "the token's own member has an email");
        records.IndexOf(people.Single(p => p.Id == s.Jonas.Id)).ShouldBeLessThan(records.IndexOf(card), "people come before records that use them");

        records[^1].ShouldBe(new ReadCursor(Utc(T0.AddDays(6))));
    }

    [Fact]
    public async Task Comments_and_attachments_come_across_and_files_download_from_the_api_host()
    {
        var s = new Sample();
        var connector = Connect(s.Trello);
        var records = await connector.ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);

        var comments = records.OfType<SourceComment>().ToList();
        comments.Select(c => (c.Item, c.Author, c.Markup, c.Body)).ShouldBe(
        [
            (s.Pay.Id, s.Jonas.Id, MarkupKind.Markdown, "Looks good"),
            (s.Pay.Id, s.Sofie.Id, MarkupKind.Markdown, "**Shipped** to staging"),
        ], "the comment on a deleted card is dropped");
        comments[0].Created.ShouldBe(T0.AddDays(1));
        comments[1].Updated.ShouldBe(T0.AddDays(4.5));
        comments.Select(c => c.Id).ShouldAllBe(id => id.Length == 24, "a comment's ID is its action's");

        var attachment = records.OfType<SourceAttachment>().Single();
        (attachment.Item, attachment.FileName, attachment.ContentType, attachment.Size, attachment.Author, attachment.Created).ShouldBe(
            (s.Pay.Id, "flow.png", "image/png", 4L, s.Sofie.Id, T0.AddDays(1)));
        attachment.Url.ShouldStartWith("https://trello.com/1/cards/");

        await using var file = await connector.FetchAttachmentAsync(attachment, Ct);
        (await new StreamReader(file).ReadToEndAsync(Ct)).ShouldBe("PNG!");
        s.Trello.Requests[^1].ShouldBe($"GET /1/cards/{s.Pay.Id}/attachments/{attachment.Id}/download/flow.png");
        s.Trello.ForeignHosts.ShouldBeEmpty("trello.com download URLs are fetched from api.trello.com");
    }

    [Fact]
    public async Task Actions_replay_as_changes_in_kupro_terms_skipping_bookkeeping()
    {
        var s = new Sample();
        var records = await Connect(s.Trello).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        var changes = records.OfType<SourceChange>().Where(c => c.Item == s.Pay.Id).ToList();

        changes.Select(c => c.Id).ShouldBeUnique();
        changes.Select(c => c.Field).ShouldBe(
        [
            "status", "members", "members", "labels", "title", "description", "due",
            s.Priority, s.Points, s.GoLive, s.Approved, s.Notes, TrelloConnector.ChecklistField,
        ], "the position change is bookkeeping");

        var move = changes[0];
        (move.From, move.To, move.FromText, move.ToText, move.Actor, move.At).ShouldBe((s.Todo, s.Doing, "To do", "Doing", s.Sofie.Id, T0.AddDays(2)));
        move.Id.ShouldEndWith(":idList");
        (changes[1].To, changes[1].ToText).ShouldBe((s.Sofie.Id, "Sofie Larsen"));
        (changes[3].From, changes[3].To).ShouldBe((null, "bug"));
        (changes[4].From, changes[4].To, changes[4].Actor).ShouldBe(("Pay by kort", "Pay by card", s.Jonas.Id));
        (changes[5].From, changes[5].To).ShouldBe((null, null), "description edits are a marker");
        (changes[6].From, changes[6].To).ShouldBe((null, "2025-03-20"));
        (changes[7].From, changes[7].To).ShouldBe((null, "High"), "a list field's option by name");
        changes[8].To.ShouldBe("5");
        changes[9].To.ShouldBe("2025-05-01");
        changes[10].To.ShouldBe("true");
        (changes[12].From, changes[12].To, changes[12].ToText).ShouldBe(("incomplete", "complete", "Wire gateway: complete"));

        records.OfType<SourceChange>().Where(c => c.Item == s.Banner.Id).Select(c => (c.Field, c.From, c.To)).ShouldBe(
            [(TrelloConnector.DueCompleteField, "false", "true"), (TrelloConnector.ArchivedField, "false", "true")]);
    }

    [Fact]
    public async Task A_board_past_the_page_limits_keeps_every_card_and_comment()
    {
        var trello = new FakeTrello();
        var board = trello.AddBoard("Big", T0.AddDays(-1));
        board.Members.Add(trello.Me);
        var list = board.AddList("To do");
        var first = board.AddCard(list, "Card 1", T0, trello.Me);
        for (var i = 2; i <= 2_500; i++)
        {
            board.AddCard(list, $"Card {i}", T0.AddSeconds(i), trello.Me);
        }

        for (var i = 0; i < 2_100; i++)
        {
            first.Comment(T0.AddDays(1).AddSeconds(i), trello.Me, $"Comment {i}");
        }

        var records = await Connect(trello).ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);

        records.OfType<SourceItem>().Select(i => i.Id).ShouldBeUnique();
        records.OfType<SourceItem>().Count().ShouldBe(2_500);
        records.OfType<SourceComment>().Select(c => c.Body).Distinct().Count().ShouldBe(2_100);
        records.OfType<SourceItem>().Single(i => i.Id == first.Id).Creator.ShouldBe(trello.Me.Id, "the oldest create action is on the last page");
        trello.Requests.Count(r => r.Contains("/cards?", StringComparison.Ordinal)).ShouldBe(3);
        trello.Requests.Count(r => r.Contains("/actions?filter=commentCard", StringComparison.Ordinal)).ShouldBe(3);
        trello.Requests.Count(r => r.Contains("/actions?filter=createCard", StringComparison.Ordinal)).ShouldBe(3);
    }

    [Fact]
    public async Task A_delta_read_returns_only_cards_changed_since_the_cursor()
    {
        var s = new Sample();
        var connector = Connect(s.Trello);

        var records = await connector.ReadAsync(new ReadScope(), Utc(T0.AddDays(5)), Ct).ToListAsync(Ct);

        var banner = records.OfType<SourceItem>().ShouldHaveSingleItem();
        banner.Id.ShouldBe(s.Banner.Id);
        banner.Creator.ShouldBe(s.Sofie.Id, "creators come from the full action log, not just the delta");
        records.OfType<SourceChange>().Select(c => c.Field).ShouldBe([TrelloConnector.ArchivedField]);
        records.OfType<SourceComment>().ShouldBeEmpty();
        records[^1].ShouldBe(new ReadCursor(Utc(T0.AddDays(6))));
        s.Trello.Requests.ShouldContain(r => r.Contains("filter=commentCard", StringComparison.Ordinal) && r.Contains("since=2025-03-06T09", StringComparison.Ordinal));

        var nothing = await connector.ReadAsync(new ReadScope(), "2026-01-01T00:00:00Z", Ct).ToListAsync(Ct);
        nothing.OfType<SourceItem>().ShouldBeEmpty();
        nothing[^1].ShouldBe(new ReadCursor("2026-01-01T00:00:00Z"), "no change: the cursor stays");
    }

    [Fact]
    public async Task Scope_limits_boards_reads_a_named_archived_board_and_skips_cards_archived_before_the_cutoff()
    {
        var s = new Sample();
        var connector = Connect(s.Trello);

        var marketing = await connector.ReadAsync(new ReadScope { Projects = [s.Marketing.Id] }, null, Ct).OfType<SourceItem>().ToListAsync(Ct);
        marketing.Select(i => i.Title).ShouldBe(["Newsletter"]);

        var archived = await connector.ReadAsync(new ReadScope { Projects = [s.Archive.Id] }, null, Ct).OfType<SourceItem>().ToListAsync(Ct);
        archived.Select(i => (i.Title, i.Project)).ShouldBe([("Old site", s.Archive.Id)]);

        var recent = await connector.ReadAsync(new ReadScope { ResolvedAfter = T0.AddDays(7) }, null, Ct).ToListAsync(Ct);
        recent.OfType<SourceItem>().Select(i => i.Id).ShouldNotContain(s.Banner.Id);
        recent.OfType<SourceChange>().ShouldNotContain(c => c.Item == s.Banner.Id);
        recent.OfType<SourceItem>().Select(i => i.Id).ShouldContain(s.Pay.Id);

        var missing = await Should.ThrowAsync<SourceAccessException>(
            () => connector.ReadAsync(new ReadScope { Projects = ["0123456789abcdef01234567"] }, null, Ct).ToListAsync(Ct).AsTask());
        missing.Status.ShouldBe(System.Net.HttpStatusCode.NotFound);
        missing.TokenRejected.ShouldBeFalse();
    }

    [Fact]
    public async Task Throttled_requests_wait_for_retry_after_and_then_succeed()
    {
        var s = new Sample();
        s.Trello.Throttle = 2;
        var waits = new List<TimeSpan>();
        var clock = new TestClock(Now);
        var connector = Connect(s.Trello, clock: clock);
        connector.Client.Delay = (wait, _) =>
        {
            waits.Add(wait);
            clock.Now += wait;
            return Task.CompletedTask;
        };

        (await connector.DescribeAsync(Ct)).Projects.Count.ShouldBe(2);

        waits.ShouldBe([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)]);
    }

    [Fact]
    public async Task A_rejected_token_fails_as_a_token_problem_and_a_rejected_key_does_not()
    {
        var error = await Should.ThrowAsync<SourceAccessException>(() => Connect(new Sample().Trello, token: "wrong-token").DescribeAsync(Ct));
        error.TokenRejected.ShouldBeTrue();
        error.Message.ShouldNotContain("wrong-token");
        error.Message.ShouldNotContain(FakeTrello.Key);

        var key = await Should.ThrowAsync<SourceAccessException>(() => Connect(new Sample().Trello, key: "wrong-key").DescribeAsync(Ct));
        key.TokenRejected.ShouldBeFalse("Kupro's key is not something the owner can fix by authorizing again");
        key.Message.ShouldNotContain("wrong-key");
        key.Message.ShouldNotContain(FakeTrello.Token);
    }

    [Fact]
    public async Task The_key_and_token_are_never_sent_to_another_host()
    {
        var s = new Sample();
        var connector = Connect(s.Trello);
        await connector.ReadAsync(new ReadScope(), null, Ct).ToListAsync(Ct);
        s.Trello.Requests.ShouldAllBe(r => !r.Contains(FakeTrello.Token, StringComparison.Ordinal) && !r.Contains(FakeTrello.Key, StringComparison.Ordinal));

        // A file somewhere else (an old S3 upload) is fetched without credentials.
        s.Trello.Files["https://files.example/acme/banner.png"] = "PNG?"u8.ToArray();
        var elsewhere = new SourceAttachment { Id = "x", Item = s.Pay.Id, FileName = "banner.png", Created = T0, Url = "https://files.example/acme/banner.png" };
        await using (var file = await connector.FetchAttachmentAsync(elsewhere, Ct))
        {
            (await new StreamReader(file).ReadToEndAsync(Ct)).ShouldBe("PNG?");
        }

        // A trello.com path on another host is not treated as a Trello upload.
        var lookalike = elsewhere with { Url = $"https://trello.com.evil.example/1/cards/{s.Pay.Id}/attachments/{s.Pay.Id}/download/x.png" };
        await Should.ThrowAsync<SourceAccessException>(() => connector.FetchAttachmentAsync(lookalike, Ct));
        await Should.ThrowAsync<SourceAccessException>(() => connector.FetchAttachmentAsync(elsewhere with { Url = "http://files.example/x.png" }, Ct));

        s.Trello.ForeignHosts.ShouldBe(["files.example", "trello.com.evil.example"]);
        s.Trello.Leaks.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_writes_a_snapshot_with_every_record_and_file()
    {
        var zip = new MemoryStream();
        await Connect(new Sample().Trello).WriteSnapshotAsync(zip, new ReadScope(), null, includeFiles: true, new TestClock(Now), Ct);
        zip.Position = 0;

        await using var snapshot = await SnapshotReader.OpenAsync(zip, Ct);

        snapshot.Header.Source.Kind.ShouldBe(SourceKinds.Trello);
        snapshot.Header.Counts.ShouldBe(new SnapshotCounts { Items = 5, Relations = 0, Comments = 2, Attachments = 1, Changes = 15 });
        snapshot.Header.People.Count.ShouldBe(2);
        snapshot.Header.Cursor.ShouldBe(Utc(T0.AddDays(6)));
        (await snapshot.ReadAttachmentsAsync(Ct).SingleAsync(Ct)).File.ShouldNotBeNull();
        var items = await snapshot.ReadItemsAsync(Ct).ToListAsync(Ct);
        items.Single(i => i.Title == "Pay by card").Fields.Count.ShouldBe(6);
    }

    private static TrelloConnector Connect(
        FakeTrello trello, string key = FakeTrello.Key, string token = FakeTrello.Token, TimeProvider? clock = null)
    {
        var connector = new TrelloConnector(trello.Client(), key, token, clock ?? new TestClock(Now));
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        return connector;
    }

    /// <summary>
    /// Two open boards and an archived one. Website holds the interesting cards: one with every kind of field, a
    /// checklist, a file, a link, two comments and a dozen changes; an archived one; a plain one. Plus a comment on a
    /// card that has since been deleted.
    /// </summary>
    private sealed class Sample
    {
        public Sample()
        {
            Sofie = Trello.Me;
            Jonas = new FakeMember(FakeTrello.Id(new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero), 2), "Jonas Holm", "jonas");

            Web = Trello.AddBoard("Website", T0.AddDays(-30));
            Web.Members.AddRange([Sofie, Jonas]);
            Todo = Web.AddList("To do");
            Doing = Web.AddList("Doing");
            Web.AddList("Review");
            Done = Web.AddList("Done");
            Web.AddList("Old", archived: true);
            var bug = Web.AddLabel("bug", "red");
            var green = Web.AddLabel(null, "green");
            var design = Web.AddLabel("Design", "purple");
            (Priority, var levels) = Web.AddCustomField("Priority", "list", "High", "Low");
            Points = Web.AddCustomField("Points", "number").Id;
            GoLive = Web.AddCustomField("Go-live", "date").Id;
            Approved = Web.AddCustomField("Approved", "checkbox").Id;
            Notes = Web.AddCustomField("Notes", "text").Id;

            Pay = Web.AddCard(Todo, "Pay by kort", T0, Jonas);
            Pay.Start = "2025-03-03T08:00:00.000Z";
            Pay.IdLabels.Add(green);
            Pay.Attach(T0.AddDays(1), Sofie, "flow.png", bytes: "PNG!"u8.ToArray(), mimeType: "image/png");
            Pay.Attach(T0.AddDays(1), Sofie, "Spec", url: "https://wiki.acme.example/cards");
            Pay.AddChecklist("Steps", ("Wire gateway", false, null, null), ("Test with Visa", false, Jonas, "2025-03-18T12:00:00.000Z"));
            Pay.Comment(T0.AddDays(1), Jonas, "Looks good")
                .Move(T0.AddDays(2), Sofie, Doing)
                .AddMember(T0.AddDays(2), Sofie, Sofie)
                .AddMember(T0.AddDays(2), Sofie, Jonas)
                .AddLabel(T0.AddDays(2), Sofie, bug)
                .Update(T0.AddDays(3), Jonas, "name", "Pay by kort", "Pay by card")
                .Update(T0.AddDays(3), Jonas, "desc", "Draft", "Card first, then **Visa**.")
                .Update(T0.AddDays(3), Jonas, "pos", 16384, 8192)
                .Update(T0.AddDays(3), Jonas, "due", null, "2025-03-20T11:00:00.000Z")
                .SetField(T0.AddDays(3), Jonas, Priority, levels[0])
                .SetField(T0.AddDays(3), Jonas, Points, new JsonObject { ["number"] = "5" })
                .SetField(T0.AddDays(3), Jonas, GoLive, new JsonObject { ["date"] = "2025-05-01T10:00:00.000Z" })
                .SetField(T0.AddDays(3), Jonas, Approved, new JsonObject { ["checked"] = "true" })
                .SetField(T0.AddDays(3), Jonas, Notes, new JsonObject { ["text"] = "Ask finance" })
                .Tick(T0.AddDays(4), Jonas, "Wire gateway", complete: true)
                .Comment(T0.AddDays(4), Sofie, "**Shipped** to staging", edited: T0.AddDays(4.5));

            Banner = Web.AddCard(Done, "Old banner", T0, Sofie)
                .Update(T0.AddDays(5), Sofie, "dueComplete", false, true)
                .Archive(T0.AddDays(6), Sofie);
            Hero = Web.AddCard(Todo, "Hero image", T0, Sofie);
            Hero.IdLabels.Add(design);

            Web.Act(T0.AddDays(1), Jonas, "commentCard", new JsonObject
            {
                ["text"] = "On a deleted card",
                ["card"] = new JsonObject { ["id"] = Trello.NextId(T0), ["name"] = "Gone", ["idShort"] = 99 },
            });

            Marketing = Trello.AddBoard("Marketing", T0.AddDays(-20));
            Marketing.Members.AddRange([Jonas, Sofie]);
            Marketing.AddCard(Marketing.AddList("Ideas"), "Newsletter", T0, Jonas);

            Archive = Trello.AddBoard("Archive 2023", T0.AddDays(-400), closed: true);
            Archive.Members.Add(Sofie);
            Archive.AddCard(Archive.AddList("Done"), "Old site", T0, Sofie);
        }

        public FakeTrello Trello { get; } = new();
        public FakeMember Sofie { get; }
        public FakeMember Jonas { get; }
        public FakeBoard Web { get; }
        public FakeBoard Marketing { get; }
        public FakeBoard Archive { get; }
        public string Todo { get; }
        public string Doing { get; }
        public string Done { get; }
        public string Priority { get; }
        public string Points { get; }
        public string GoLive { get; }
        public string Approved { get; }
        public string Notes { get; }
        public FakeCard Pay { get; }
        public FakeCard Banner { get; }
        public FakeCard Hero { get; }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
