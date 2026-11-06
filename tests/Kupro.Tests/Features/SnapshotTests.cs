using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Kupro.Import;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>
/// The import snapshot format (task 023, ADR-019): connector → ZIP → reader round trip, chunked streaming, scope
/// filtering, rejection of snapshots this version can't read, and the public JSON Schema kept in step with the model.
/// </summary>
public sealed class SnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Round_trip_keeps_every_record_and_reference_data()
    {
        var source = FakeConnector.Sample();

        await using var snapshot = await ReadBackAsync(source, new ReadScope(), includeFiles: true);

        var header = snapshot.Header;
        header.Format.ShouldBe(1);
        header.Source.Kind.ShouldBe(SourceKinds.Jira);
        header.CreatedAt.ShouldBe(Now);
        header.Cursor.ShouldBe("cursor-1");
        header.Projects.Select(p => p.Key).ShouldBe(["WEB", "OPS"]);
        header.Fields.Single().Kind.ShouldBe(FieldKind.Select);
        header.Sprints.Single().End.ShouldBe(new DateOnly(2025, 3, 14));
        header.Versions.Single().Name.ShouldBe("1.0");
        header.Counts.ShouldBe(new SnapshotCounts { Items = 3, Relations = 1, Comments = 1, Attachments = 1, Changes = 1 });

        // Seen once without an email and once with: the email is kept. A person only seen while reading is added.
        header.People.Single(p => p.Id == "u1").Email.ShouldBe("sofie@acme.example");
        header.People.Select(p => p.Id).ShouldBe(["u1", "u2"], ignoreOrder: true);

        var items = await snapshot.ReadItemsAsync(Ct).ToListAsync(Ct);
        var story = items.Single(i => i.Id == "101");
        var original = source.Records.OfType<SourceItem>().Single(i => i.Id == "101");
        JsonSerializer.Serialize(story, SnapshotJson.Default.SourceItem).ShouldBe(JsonSerializer.Serialize(original, SnapshotJson.Default.SourceItem));
        story.Fields["cf_team"].GetString().ShouldBe("Blue");
        story.Description.ShouldBe("<p>Æble <b>først</b></p>");

        (await snapshot.ReadRelationsAsync(Ct).SingleAsync(Ct)).Kind.ShouldBe(RelationKinds.Relates);
        (await snapshot.ReadCommentsAsync(Ct).SingleAsync(Ct)).Body.ShouldBe("Looks good");
        (await snapshot.ReadChangesAsync(Ct).SingleAsync(Ct)).ToText.ShouldBe("Done");

        var attachment = await snapshot.ReadAttachmentsAsync(Ct).SingleAsync(Ct);
        attachment.File.ShouldNotBeNull();
        await using var file = await new SnapshotConnector(snapshot).FetchAttachmentAsync(attachment, Ct);
        (await new StreamReader(file).ReadToEndAsync(Ct)).ShouldBe("PNG!");
    }

    [Fact]
    public async Task Without_files_attachments_stay_references_for_the_connector_to_fetch()
    {
        await using var snapshot = await ReadBackAsync(FakeConnector.Sample(), new ReadScope(), includeFiles: false);

        var attachment = await snapshot.ReadAttachmentsAsync(Ct).SingleAsync(Ct);
        attachment.File.ShouldBeNull();
        await Should.ThrowAsync<SnapshotFormatException>(() => new SnapshotConnector(snapshot).FetchAttachmentAsync(attachment, Ct));
    }

    [Fact]
    public async Task Large_parts_are_chunked_and_read_back_in_order()
    {
        var source = FakeConnector.Sample();
        source.Records.RemoveAll(r => r is SourceItem);
        var padding = new string('x', 2_000);
        source.Records.AddRange(Enumerable.Range(1, 2_000).Select(n => new SourceItem
        {
            Id = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Project = "10",
            Type = "story",
            Status = "1",
            Title = $"Item {n}",
            Description = padding,
            Created = Now,
            Updated = Now,
        }));

        using var zip = new MemoryStream();
        await source.WriteSnapshotAsync(zip, new ReadScope(), null, includeFiles: false, new FixedTime(Now), Ct);

        zip.Position = 0;
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        {
            // About 4 MB of items at a megabyte per chunk.
            archive.Entries.Count(e => e.FullName.StartsWith("items/", StringComparison.Ordinal)).ShouldBeGreaterThan(2);
        }

        zip.Position = 0;
        await using var snapshot = await SnapshotReader.OpenAsync(zip, Ct);
        var ids = await snapshot.ReadItemsAsync(Ct).Select(i => int.Parse(i.Id, System.Globalization.CultureInfo.InvariantCulture)).ToListAsync(Ct);
        ids.ShouldBe(Enumerable.Range(1, 2_000));
        snapshot.Header.Counts.Items.ShouldBe(2_000);
    }

    [Fact]
    public async Task Snapshot_source_reads_only_the_projects_and_history_in_scope()
    {
        await using var snapshot = await ReadBackAsync(FakeConnector.Sample(), new ReadScope(), includeFiles: true);
        var connector = new SnapshotConnector(snapshot);

        var records = await connector.ReadAsync(new ReadScope { Projects = ["20"] }, null, Ct).ToListAsync(Ct);

        records.OfType<SourceItem>().Select(i => i.Key).ShouldBe(["OPS-1"]);
        records.OfType<SourceComment>().ShouldBeEmpty();
        records.OfType<SourceChange>().ShouldBeEmpty();
        records.OfType<SourceVersion>().ShouldBeEmpty();
        records.OfType<SourceSprint>().ShouldHaveSingleItem(); // no project: spans every project
        records[^1].ShouldBe(new ReadCursor("cursor-1"));

        var recent = await connector.ReadAsync(new ReadScope { ResolvedAfter = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero) }, null, Ct)
            .ToListAsync(Ct);
        recent.OfType<SourceItem>().Select(i => i.Id).ShouldBe(["100", "200"]); // 101 was resolved in March
    }

    [Fact]
    public async Task Snapshot_from_a_newer_format_is_refused_with_a_clear_message()
    {
        using var zip = Zip(("snapshot.json", """{"format":2,"source":{"kind":"jira"},"createdAt":"2026-11-06T00:00:00Z"}"""));

        var error = await Should.ThrowAsync<SnapshotFormatException>(() => SnapshotReader.OpenAsync(zip, Ct));
        error.Message.ShouldContain("format 2");
    }

    [Fact]
    public async Task Not_a_zip_or_no_header_is_refused()
    {
        using var text = new MemoryStream(Encoding.UTF8.GetBytes("title,status\nA,Done\n"));
        await Should.ThrowAsync<SnapshotFormatException>(() => SnapshotReader.OpenAsync(text, Ct));

        using var empty = Zip(("items/000001.ndjson", "{}"));
        (await Should.ThrowAsync<SnapshotFormatException>(() => SnapshotReader.OpenAsync(empty, Ct))).Message.ShouldContain("snapshot.json");
    }

    [Fact]
    public async Task Bad_record_names_its_part_and_line()
    {
        using var zip = Zip(
            ("snapshot.json", """{"format":1,"source":{"kind":"script"},"createdAt":"2026-11-06T00:00:00Z"}"""),
            ("items/000001.ndjson", """
                {"id":"1","project":"p","type":"t","status":"s","title":"ok","created":"2026-01-01T00:00:00Z","updated":"2026-01-01T00:00:00Z"}
                {"id":"2","project":"p","type":"t","status":"s","created":"2026-01-01T00:00:00Z","updated":"2026-01-01T00:00:00Z"}
                """));
        await using var snapshot = await SnapshotReader.OpenAsync(zip, Ct);

        var error = await Should.ThrowAsync<SnapshotFormatException>(() => snapshot.ReadItemsAsync(Ct).ToListAsync(Ct).AsTask());
        error.Message.ShouldContain("items/000001.ndjson line 2");
    }

    [Fact]
    public async Task Null_where_the_format_has_none_is_refused()
    {
        using var zip = Zip(
            ("snapshot.json", """{"format":1,"source":{"kind":"script"},"createdAt":"2026-11-06T00:00:00Z"}"""),
            ("comments/000001.ndjson", """{"id":"c","item":"1","body":null,"created":"2026-01-01T00:00:00Z"}"""));
        await using var snapshot = await SnapshotReader.OpenAsync(zip, Ct);

        await Should.ThrowAsync<SnapshotFormatException>(() => snapshot.ReadCommentsAsync(Ct).ToListAsync(Ct).AsTask());
    }

    [Fact]
    public async Task Attachment_file_must_be_under_files()
    {
        using var zip = Zip(("snapshot.json", """{"format":1,"source":{"kind":"script"},"createdAt":"2026-11-06T00:00:00Z"}"""));
        await using var snapshot = await SnapshotReader.OpenAsync(zip, Ct);

        (await snapshot.OpenFileAsync("snapshot.json", Ct)).ShouldBeNull();
        (await snapshot.OpenFileAsync("files/../snapshot.json", Ct)).ShouldBeNull();
    }

    /// <summary>docs/import-format.schema.json documents the model: same properties, same required ones.</summary>
    [Theory]
    [InlineData(typeof(SnapshotHeader), "header")]
    [InlineData(typeof(SnapshotSource), "source")]
    [InlineData(typeof(SnapshotCounts), "counts")]
    [InlineData(typeof(SourceProject), "project")]
    [InlineData(typeof(SourcePerson), "person")]
    [InlineData(typeof(SourceItemType), "itemType")]
    [InlineData(typeof(SourceStatus), "status")]
    [InlineData(typeof(SourceField), "field")]
    [InlineData(typeof(SourceSprint), "sprint")]
    [InlineData(typeof(SourceVersion), "version")]
    [InlineData(typeof(SourceItem), "item")]
    [InlineData(typeof(SourceLink), "link")]
    [InlineData(typeof(SourceRelation), "relation")]
    [InlineData(typeof(SourceComment), "comment")]
    [InlineData(typeof(SourceAttachment), "attachment")]
    [InlineData(typeof(SourceChange), "change")]
    public void Public_schema_matches_the_model(Type type, string definition)
    {
        var model = SnapshotJson.Default.Options.GetJsonSchemaAsNode(type);
        var documented = PublicSchema["$defs"]![definition]
            ?? throw new ShouldAssertException($"import-format.schema.json has no $defs/{definition}.");

        Names(documented["properties"]!.AsObject().Select(p => p.Key)).ShouldBe(Names(model["properties"]!.AsObject().Select(p => p.Key)));
        Names(documented["required"]?.AsArray().Select(r => r!.GetValue<string>()))
            .ShouldBe(Names(model["required"]?.AsArray().Select(r => r!.GetValue<string>())));
    }

    [Fact]
    public void Public_schema_documents_every_enum_value()
    {
        Values(PublicSchema["$defs"]!["field"]!["properties"]!["kind"]!).ShouldBe(Enum.GetValues<FieldKind>().Select(Json).Order(StringComparer.Ordinal));
        Values(PublicSchema["$defs"]!["markup"]!).ShouldBe(Enum.GetValues<MarkupKind>().Select(Json).Order(StringComparer.Ordinal));

        static string[] Values(JsonNode node) => [.. node["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Order(StringComparer.Ordinal)];
        static string Json<T>(T value) => JsonSerializer.SerializeToElement(value, SnapshotJson.Default.Options).GetString()!;
    }

    private static JsonNode PublicSchema { get; } =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "import-format.schema.json")))!;

    private static string[] Names(IEnumerable<string>? names) => [.. (names ?? []).Order(StringComparer.Ordinal)];

    private static async Task<SnapshotReader> ReadBackAsync(ISourceConnector source, ReadScope scope, bool includeFiles)
    {
        var zip = new MemoryStream();
        await source.WriteSnapshotAsync(zip, scope, null, includeFiles, new FixedTime(Now), Ct);
        zip.Position = 0;
        return await SnapshotReader.OpenAsync(zip, Ct);
    }

    private static MemoryStream Zip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
