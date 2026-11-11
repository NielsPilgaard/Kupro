using System.Text;
using Kupro.Import.Csv;
using Kupro.Import.Snapshot;

namespace Kupro.Tests.Features;

/// <summary>Reading a CSV: encodings, delimiters, quoting and the known exports (task 023, CSV source).</summary>
public sealed class CsvImportTests
{
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    [Fact]
    public void A_danish_file_in_windows_1252_keeps_its_letters_and_semicolons()
    {
        var table = CsvTable.Parse(Windows1252.GetBytes("Titel;Beskrivelse\r\nRød grød;\"Med fløde; og \"\"sukker\"\"\nto linjer\"\r\nÆble;\r\n"));

        table.Encoding.ShouldBe("windows-1252");
        table.Delimiter.ShouldBe(';');
        table.Headers.ShouldBe(["Titel", "Beskrivelse"]);
        table.Rows.Count.ShouldBe(2);
        table.Rows[0].ShouldBe(["Rød grød", "Med fløde; og \"sukker\"\nto linjer"]);
        table.Rows[1].ShouldBe(["Æble", ""]);
    }

    [Fact]
    public void Utf8_with_or_without_a_bom_reads_the_same()
    {
        var text = "Title,Labels\nSø,\"a, b\"\n";
        var plain = CsvTable.Parse(Encoding.UTF8.GetBytes(text));
        var bom = CsvTable.Parse([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)]);

        foreach (var table in new[] { plain, bom })
        {
            table.Encoding.ShouldBe("utf-8");
            table.Headers[0].ShouldBe("Title");
            table.Rows[0].ShouldBe(["Sø", "a, b"]);
        }
    }

    [Fact]
    public async Task A_jira_export_is_recognised_with_its_repeated_labels_estimates_and_parents()
    {
        const string Csv = """
            Summary,Issue key,Issue id,Issue Type,Status,Priority,Assignee,Reporter,Created,Labels,Labels,Original Estimate,Parent id,Custom field (Team)
            Checkout,WEB-1,10001,Epic,To Do,High,Sofie Larsen,Jonas Holm,01/Mar/25 9:00 AM,payments,,,,Blue
            Pay with MobilePay,WEB-2,10002,Story,Done,Medium,sofie@acme.example,Jonas Holm,02/Mar/25 10:30 AM,payments,mobile,21600,10001,
            """;
        var table = CsvTable.Parse(Encoding.UTF8.GetBytes(Csv));
        var columns = CsvColumns.Propose(table.Headers);

        columns.Preset.ShouldBe("jira");
        columns.DescriptionMarkup.ShouldBe(MarkupKind.JiraWiki);
        var records = await new CsvConnector(table, columns, "Web").ReadAsync(new ReadScope(), null, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        var items = records.OfType<SourceItem>().ToList();

        var story = items.Single(i => i.Key == "WEB-2");
        story.Id.ShouldBe("10002");
        story.Parent.ShouldBe("10001");
        story.Labels.ShouldBe(["payments", "mobile"]);
        story.EstimateSeconds.ShouldBe(21600);
        story.Assignee.ShouldBe("sofie@acme.example");
        story.Created.ShouldBe(new DateTimeOffset(2025, 3, 2, 10, 30, 0, TimeSpan.Zero));
        items.Single(i => i.Key == "WEB-1").Fields["Custom field (Team)"].GetString().ShouldBe("Blue");
        records.OfType<SourcePerson>().Single(p => p.Id == "sofie@acme.example").Email.ShouldBe("sofie@acme.example");
    }

    [Fact]
    public void A_linear_export_and_a_plain_file_get_their_columns_by_name()
    {
        var linear = CsvColumns.Propose(["ID", "Team", "Title", "Description", "Status", "Estimate", "Parent issue", "Labels"]);
        (linear.Preset, linear.Key, linear.Points, linear.Parent, linear.DescriptionMarkup).ShouldBe(("linear", "ID", "Estimate", "Parent issue", MarkupKind.Markdown));

        var plain = CsvColumns.Propose(["name", "STATE", "Tags", "Deadline", "Estimate"]);
        (plain.Preset, plain.Title, plain.Status, plain.Labels, plain.Due, plain.EstimateHours).ShouldBe(((string?)null, "name", "STATE", "Tags", "Deadline", "Estimate"));
    }

    [Fact]
    public void An_empty_file_says_what_it_needs()
    {
        Should.Throw<FormatException>(() => CsvTable.Parse([])).Message.ShouldContain("header row");
    }
}
