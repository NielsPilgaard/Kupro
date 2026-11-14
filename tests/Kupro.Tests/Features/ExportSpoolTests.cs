using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kupro.Import.Kupro;
using Kupro.Import.Snapshot;

namespace Kupro.Tests.Features;

/// <summary>
/// A Kupro export's <c>workspace.json</c> streamed to disk table by table (task 023): rows and tokens split across
/// reads, rows larger than the read buffer, skipped tables, and the size cap.
/// </summary>
public sealed class ExportSpoolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1 << 20)]
    public async Task Every_wanted_row_comes_back_as_written_however_the_stream_is_cut(int chunk)
    {
        var big = new string('x', 300_000);
        var json = new JsonObject
        {
            ["format"] = 1,
            ["exportedAt"] = "2026-11-12T00:00:00Z",
            ["workspace"] = new JsonObject { ["name"] = "Acme", ["nested"] = new JsonObject { ["issues"] = new JsonArray(1, 2) } },
            ["meetings"] = new JsonArray(new JsonObject { ["summary"] = big }, new JsonObject { ["id"] = "m2" }),
            ["issues"] = new JsonArray([.. Enumerable.Range(1, 500).Select(n => (JsonNode)new JsonObject
            {
                ["id"] = $"i{n}",
                ["title"] = $"Issue \"{n}\" ✓",
                ["description"] = n == 250 ? big : null,
                ["labels"] = new JsonArray(new JsonObject { ["labelId"] = "l1" }),
                ["rank"] = 1000 - n,
            })]),
            ["labels"] = new JsonArray(),
            ["notes"] = new JsonArray("a", 1, new JsonArray()),
        };
        await using var stream = new Trickle(Encoding.UTF8.GetBytes(json.ToJsonString()), chunk);

        using var spool = await ExportSpool.SplitAsync(stream, ["issues", "labels", "comments"], long.MaxValue, Ct);

        spool.Format.ShouldBe(1);
        var issues = spool.Rows("issues").ToList();
        issues.Select(i => i.GetProperty("id").GetString()).ShouldBe(Enumerable.Range(1, 500).Select(n => $"i{n}"));
        issues[249].GetProperty("description").GetString().ShouldBe(big);
        issues[0].GetProperty("title").GetString().ShouldBe("Issue \"1\" ✓");
        spool.Rows("labels").ShouldBeEmpty();
        spool.Rows("comments").ShouldBeEmpty();
        spool.Rows("meetings").ShouldBeEmpty();

        var offsets = spool.RowsWithOffsets("issues").OrderBy(r => r.Row.GetProperty("rank").GetInt32()).Select(r => r.Offset).ToList();
        spool.RowsAt("issues", offsets).Select(i => i.GetProperty("id").GetString()).First().ShouldBe("i500");
    }

    [Fact]
    public async Task More_than_the_cap_is_refused_even_when_the_zip_header_says_less()
    {
        await using var stream = new Trickle(Encoding.UTF8.GetBytes($$"""{"format":1,"issues":[{"title":"{{new string('x', 3 << 20)}}"}]}"""), 1 << 16);

        var error = await Should.ThrowAsync<SnapshotFormatException>(() => ExportSpool.SplitAsync(stream, ["issues"], 1 << 20, Ct));
        error.Message.ShouldContain("Imports:MaxExportJsonMegabytes");
    }

    [Theory]
    [InlineData("""{"format":1,"issues":[{"id":"a"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public async Task A_cut_or_foreign_file_fails_as_a_format_error(string text)
    {
        await using var stream = new Trickle(Encoding.UTF8.GetBytes(text), 3);

        var error = await Should.ThrowAsync<Exception>(() => ExportSpool.SplitAsync(stream, ["issues"], long.MaxValue, Ct));
        (error is SnapshotFormatException or JsonException).ShouldBeTrue(error.ToString());
    }

    /// <summary>Hands out at most <c>chunk</c> bytes per read, like a slow network or a ZIP entry.</summary>
    private sealed class Trickle(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
