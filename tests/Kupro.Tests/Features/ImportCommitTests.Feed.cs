using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>What an import looks like in the feed, and its source keys as lookups (task 023, ADR-019 History and Keys).</summary>
public sealed partial class ImportCommitTests
{
    [Fact]
    public async Task The_feed_says_where_imported_history_came_from_and_who_did_it_there()
    {
        var source = History();
        var mapping = await MappingAsync(source) with { People = new Dictionary<string, PersonTarget> { ["u2"] = new(PersonAction.Skip) } };
        var import = await AddImportAsync(source, mapping);
        (await RunAsync(import, ImportRunKind.Commit)).State.ShouldBe(ImportRunState.Succeeded);
        await RerunAsync(import, Rename(source, "101", "Pay with Dankort"));

        var feed = await GetAsync<List<FeedItem>>(_client, Url("issues/WEB-2/activity"));

        var imported = feed.Single(f => f.Kind == FeedItemKind.Imported);
        imported.Source.ShouldBe(SourceKinds.Jira);
        imported.Via.ShouldBe(Surface.Import);
        feed.Single(f => f.Kind == FeedItemKind.SourceChanged).Changes.Single().ShouldBe(new FeedChange("Team", "Blue", "Red"));
        feed.Single(f => f.Kind == FeedItemKind.Comment && f.Body == "Looks good").SourceActor.ShouldBe("Jonas Holm");
        feed.Single(f => f.Kind == FeedItemKind.Updated && f.Changes.Any(c => c.Field == "title")).SourceActor.ShouldBe("Jonas Holm");
        feed.Single(f => f.Kind == FeedItemKind.Reimported).Changes.ShouldContain(new FeedChange("title", "Pay with MobilePay", "Pay with Dankort"));
    }

    [Fact]
    public async Task A_source_key_finds_its_issue_in_lookups_search_and_the_feed()
    {
        await CommitAsync(Source(Item("ado", key: "4567", title: "Rotate keys"), Item("old", key: "OLD-9", title: "Old key")));

        (await IssueAsync(Uri.EscapeDataString("#4567"))).Key.ShouldBe("WEB-4567");
        (await IssueAsync("old-9")).Key.ShouldBe("WEB-9");
        (await GetAsync<List<FeedItem>>(_client, Url("issues/OLD-9/activity"))).ShouldContain(f => f.Kind == FeedItemKind.Imported);

        var found = await GetAsync<IssuePage>(_client, Url("issues?q=OLD-9"));
        found.Items[0].Key.ShouldBe("WEB-9");
    }

    private static FakeConnector Rename(FakeConnector source, string id, string title)
    {
        var item = (SourceItem)source.Records.Single(r => r is SourceItem i && i.Id == id);
        source.Records[source.Records.IndexOf(item)] = item with { Title = title };
        return source;
    }
}
