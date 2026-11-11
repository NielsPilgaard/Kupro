using System.Text.Json;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;

namespace Kupro.Tests.Features;

/// <summary>Source markup to Markdown and the imported description (task 023, ADR-019 Markup and Other fields).</summary>
public sealed class ImportMarkupTests
{
    private static string? Html(string html) => MarkupConverters.ToMarkdown(html, MarkupKind.Html);

    [Fact]
    public void Html_becomes_markdown()
    {
        Html("<p>Æble <b>først</b> and <i>then</i></p><p>Next <a href=\"https://x.example/a\">link</a></p>")
            .ShouldBe("Æble **først** and *then*\n\nNext [link](https://x.example/a)");
        Html("<h2>Title</h2><ul><li>One</li><li>Two<ul><li>Deep</li></ul></li></ul><ol><li>First</li></ol>")
            .ShouldBe("## Title\n\n- One\n- Two\n\n  - Deep\n\n1. First");
        Html("<pre>var a = `x`;\n  b</pre>").ShouldBe("```\nvar a = `x`;\n  b\n```");
        Html("<blockquote><p>Said</p></blockquote>").ShouldBe("> Said");
        Html("<div>one<br>two</div>").ShouldBe("one  \ntwo");
        Html("<code>a_b</code> <img src=\"a.png\" alt=\"pic\">").ShouldBe("`a_b` ![pic](a.png)");
        Html("<table><tr><th>A</th><th>B</th></tr><tr><td>1|2</td><td>3</td></tr></table>")
            .ShouldBe("| A | B |\n| --- | --- |\n| 1\\|2 | 3 |");
    }

    private static readonly Guid FileId = Guid.Parse("0198f0c2-0000-7000-8000-000000000001");

    private static readonly ImportReferences References = new(
        new Dictionary<string, string> { ["u1"] = "Sofie Larsen", ["u9"] = "A *star*" },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sofie"] = "Sofie Larsen" },
        new Dictionary<string, AttachmentRef>
        {
            ["https://src.example/files/1"] = new(FileId, "flow.png", Image: true),
            ["https://src.example/files/2"] = new(FileId, "spec.pdf", Image: false),
        },
        new Dictionary<string, string> { ["https://src.example/browse/OLD-7"] = "WEB-7" });

    [Fact]
    public void Mentions_files_and_source_issue_links_point_into_kupro()
    {
        References.Rewrite($"Hi {MarkupConverters.Mention("u1")} and @Sofie. Also {MarkupConverters.Mention("u9")} and @nobody")
            .ShouldBe("Hi @Sofie Larsen and @Sofie Larsen. Also @A \\*star\\* and @nobody");
        References.Rewrite("![pic](https://src.example/files/1) and [the spec](<https://src.example/files/2>)")
            .ShouldBe($"![pic](attachment:{FileId}) and [the spec](attachment:{FileId})");
        References.Rewrite("Bare https://src.example/files/2.")
            .ShouldBe($"Bare [spec.pdf](attachment:{FileId}).");
        References.Rewrite("See [OLD-7](https://src.example/browse/OLD-7), [this one](https://src.example/browse/OLD-7) and https://src.example/browse/OLD-7")
            .ShouldBe("See WEB-7, this one (WEB-7) and WEB-7");
        References.Rewrite("[other](https://elsewhere.example/x \"t\")").ShouldBe("[other](https://elsewhere.example/x \"t\")");
    }

    [Fact]
    public void Code_and_email_addresses_are_left_as_written()
    {
        References.Rewrite("mail sofie@x.example, `@sofie https://src.example/files/1` and\n```\n@sofie https://src.example/browse/OLD-7\n```\n@sofie")
            .ShouldBe("mail sofie@x.example, `@sofie https://src.example/files/1` and\n```\n@sofie https://src.example/browse/OLD-7\n```\n@Sofie Larsen");
        References.Rewrite("https://src.example/@sofie").ShouldBe("https://src.example/@sofie");
        ImportReferences.None.Rewrite("plain text").ShouldBe("plain text");
    }

    [Fact]
    public void Html_text_is_not_taken_for_markdown()
    {
        Html("<p>2 * 3 and [x] and &lt;b&gt;</p>").ShouldBe("2 \\* 3 and \\[x\\] and \\<b>");
        Html("<p># not a heading</p>").ShouldBe("\\# not a heading");
        Html("<p>see https://x.example/a_b_c and snake_case</p>").ShouldBe("see https://x.example/a_b_c and snake_case");
    }

    [Fact]
    public void Html_the_converter_does_not_know_is_kept_and_scripts_are_dropped()
    {
        Html("<p>Before</p><iframe src=\"https://v.example/1\"></iframe>").ShouldBe("Before\n\n```html\n<iframe src=\"https://v.example/1\"></iframe>\n```");
        Html("<p>Safe</p><script>alert(1)</script><style>p{}</style>").ShouldBe("Safe");
        Html("<o:p></o:p><p>Word</p>").ShouldBe("Word");
    }

    [Fact]
    public void Html_too_deep_to_walk_is_kept_whole_instead_of_taking_the_process_down()
    {
        var deep = string.Concat(Enumerable.Repeat("<div>", 20_000)) + "text";

        Html(deep)!.ShouldStartWith("```html\n<div><div>");
    }

    [Fact]
    public void Plain_text_keeps_its_lines_and_its_characters()
    {
        MarkupConverters.ToMarkdown("one\ntwo *three*\n\n- four", MarkupKind.Plain).ShouldBe("one  \ntwo \\*three\\*\n\n\\- four");
        MarkupConverters.ToMarkdown("  ", MarkupKind.Plain).ShouldBeNull();
        MarkupConverters.ToMarkdown("**as is**", MarkupKind.Markdown).ShouldBe("**as is**");
    }

    [Fact]
    public void A_markup_with_no_converter_yet_is_fenced_not_dropped()
    {
        MarkupConverters.ToMarkdown("h1. Wiki", MarkupKind.JiraWiki).ShouldBe("```text\nh1. Wiki\n```");
        MarkupConverters.ToMarkdown("a ```` b", MarkupKind.Adf).ShouldBe("`````text\na ```` b\n`````");
    }

    [Theory]
    [InlineData("WEB-12", 12)]
    [InlineData("4567", 4567)]
    [InlineData("owner/repo#89", 89)]
    [InlineData("A-B-3", 3)]
    [InlineData("WEB-0", null)]
    [InlineData("PROJ", null)]
    [InlineData("WEB-99999999999", null)]
    [InlineData(null, null)]
    public void A_source_key_keeps_its_number_when_it_has_one(string? key, int? number) =>
        ImportCommit.SourceNumber(key).ShouldBe(number);

    private static SnapshotHeader Header() => new()
    {
        Format = 1,
        Source = new SnapshotSource { Kind = SourceKinds.Jira },
        CreatedAt = DateTimeOffset.UnixEpoch,
        Fields = [new SourceField { Id = "team", Name = "Team", Kind = FieldKind.Select }, new SourceField { Id = "lead", Name = "Lead", Kind = FieldKind.User }],
        People = [new SourcePerson { Id = "u1", Name = "Sofie" }],
        Statuses = [new SourceStatus { Id = "s", Name = "Open" }],
        ItemTypes = [new SourceItemType { Id = "t", Name = "Story" }],
    };

    private static SourceItem Item() => new()
    {
        Id = "1",
        Key = "WEB-1",
        Project = "p",
        Type = "t",
        Status = "s",
        Title = "T",
        Created = DateTimeOffset.UnixEpoch,
        Updated = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void The_block_holds_what_kupro_has_no_field_for()
    {
        var description = new ImportDescription(Header(), new ImportMapping { LabelFields = ["team"] });
        var item = Item() with
        {
            Description = "Body",
            Points = 3,
            Url = "https://x.example/WEB-1",
            Links = [new SourceLink { Url = "https://git.example/7", Title = "PR | 7" }],
            Fields = new Dictionary<string, JsonElement>
            {
                ["team"] = JsonSerializer.SerializeToElement("Blue"),
                ["lead"] = JsonSerializer.SerializeToElement("u1"),
            },
        };

        var (text, truncated) = description.Build(item, [new ChecklistLine("Step *one*", true)], description.Details(item, [], assigneeSkipped: false), ImportReferences.None);

        truncated.ShouldBeFalse();
        text.ShouldNotBeNull();
        text.ShouldStartWith("Body\n\n- [x] Step \\*one\\*\n\n<details>\n<summary>Imported from Jira</summary>\n\n");
        text.ShouldContain("| Points | 3 |");
        text.ShouldContain("| Status | Open |");
        text.ShouldContain("| Lead | Sofie |");
        text.ShouldNotContain("| Team |", Case.Sensitive);
        text.ShouldContain("[Open the original](https://x.example/WEB-1)");
        text.ShouldEndWith("</details>");
    }

    [Fact]
    public void A_label_is_kept_from_every_source_and_one_too_long_goes_to_the_block()
    {
        var description = new ImportDescription(Header(), new ImportMapping { LabelFields = ["team"] });
        var longName = new string('x', 51);
        var item = Item() with
        {
            Labels = ["Payments", longName],
            Fields = new Dictionary<string, JsonElement> { ["team"] = JsonSerializer.SerializeToElement(new[] { "Blue", "Red" }) },
        };
        var tooLong = new List<string>();

        description.Labels(item, new TypeTarget(IssueType.Task, "bug"), "Big Initiative", tooLong)
            .ShouldBe(["big initiative", "blue", "bug", "payments", "red"]);
        tooLong.ShouldBe([longName]);
    }

    [Fact]
    public void A_description_too_long_is_cut_and_says_so_and_keeps_its_link()
    {
        var description = new ImportDescription(Header(), new ImportMapping());
        var item = Item() with { Description = new string('a', 200_000), DescriptionMarkup = MarkupKind.Markdown, Url = "https://x.example/1" };

        var (text, truncated) = description.Build(item, [], description.Details(item, [], assigneeSkipped: false), ImportReferences.None);

        truncated.ShouldBeTrue();
        text!.Length.ShouldBeLessThanOrEqualTo(ImportDescription.MaxLength);
        text.ShouldContain("Cut at import");
        text.ShouldEndWith("</details>");
    }
}
