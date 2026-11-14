using Kupro.Api.Features.Imports;
using Kupro.Import.Snapshot;

namespace Kupro.Tests.Features;

/// <summary>Jira's markups to Markdown (task 024): golden files from real-shaped samples, and nothing ever vanishes.</summary>
public sealed class JiraMarkupTests
{
    private static string Golden(string name) => Path.Combine(AppContext.BaseDirectory, "Features", "Golden", "Jira", name);

    [Theory]
    [InlineData("description.adf.json", MarkupKind.Adf)]
    [InlineData("description.wiki.txt", MarkupKind.JiraWiki)]
    public void Golden_files_convert(string input, MarkupKind kind)
    {
        var actual = MarkupConverters.ToMarkdown(File.ReadAllText(Golden(input)), kind)!.ReplaceLineEndings("\n") + "\n";
        var expected = Golden(Path.ChangeExtension(input, ".md"));
        if (!File.Exists(expected))
        {
            // Writes the first output next to the binaries; review it, then copy it into Features/Golden/Jira.
            File.WriteAllText(expected, actual);
            Assert.Fail($"No golden file yet; wrote {expected}.");
        }

        actual.ShouldBe(File.ReadAllText(expected).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Unknown_adf_nodes_and_broken_documents_are_kept_as_fenced_json()
    {
        MarkupConverters.ToMarkdown("""{"type":"doc","content":[{"type":"futureThing","attrs":{"x":1}}]}""", MarkupKind.Adf)
            .ShouldBe("```json\n{\"type\":\"futureThing\",\"attrs\":{\"x\":1}}\n```");
        MarkupConverters.ToMarkdown("""{"type":"doc","content":[""", MarkupKind.Adf).ShouldBe("```json\n{\"type\":\"doc\",\"content\":[\n```");
    }

    [Fact]
    public void Adf_mentions_resolve_through_the_import_references()
    {
        var markdown = MarkupConverters.ToMarkdown(
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mention","attrs":{"id":"acc-1","text":"@Old Name"}}]}]}""", MarkupKind.Adf);
        var references = new ImportReferences(
            new Dictionary<string, string> { ["acc-1"] = "Sofie Larsen" }, new Dictionary<string, string>(),
            new Dictionary<string, AttachmentRef>(), new Dictionary<string, string>());
        references.Rewrite(markdown).ShouldBe("@Sofie Larsen");
    }

    [Theory]
    [InlineData("{code}\nnever closed", "`{code}`  \nnever closed")]
    [InlineData("snake_case_name and a-b-c", "snake_case_name and a-b-c")]
    [InlineData("*fed*, _kursiv_ og -slettet-", "**fed**, *kursiv* og ~~slettet~~")]
    public void Wiki_edge_cases(string wiki, string markdown) =>
        MarkupConverters.ToMarkdown(wiki, MarkupKind.JiraWiki).ShouldBe(markdown);

    [Fact]
    public void Adf_with_a_value_where_a_node_belongs_is_kept_as_json()
    {
        const string adf = """{"type":"doc","content":[{"type":"heading","attrs":"big","content":[1]},{"type":"codeBlock","content":["x"]}]}""";
        Should.NotThrow(() => MarkupConverters.ToMarkdown(adf, MarkupKind.Adf));
    }

    [Fact]
    public void A_code_block_inside_a_quote_or_panel_is_kept()
    {
        MarkupConverters.ToMarkdown("{quote}\nSee:\n{code}x(){code}\n{quote}", MarkupKind.JiraWiki).ShouldBe("> See:\n>\n> ```\n> x()\n> ```");
        MarkupConverters.ToMarkdown("{code}a{code}\n{info}\n{noformat}b{noformat}\n{info}", MarkupKind.JiraWiki)!.ShouldContain("> b");
    }
}
