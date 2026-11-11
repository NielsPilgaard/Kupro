using System.Globalization;
using System.Text;
using System.Text.Json;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Tests.Features;

/// <summary>A CSV through the whole pipeline (task 023 acceptance: 1,000 rows; a Danish file in Windows-1252).</summary>
public sealed partial class ImportCommitTests
{
    private async Task<Guid> CsvImportAsync(byte[] csv, Func<ImportMapping, ImportMapping>? adjust = null)
    {
        var created = await WithImportsAsync(s => s.CreateAsync(new CreateImportRequest(SourceKinds.Csv), Ct));
        var import = created.Value!.Id;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, csv, Ct);
            (await WithImportsAsync(s => s.StoreCsvAsync(import, path, Ct))).Error.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }

        var proposed = await WithImportsAsync(s => s.ProposeMappingAsync(import, Ct));
        proposed.Error.ShouldBeNull();
        var mapping = (adjust ?? (m => m))(proposed.Value!);
        await WithDbAsync(async db =>
            await db.WorkspaceImports.Where(i => i.Id == import).ExecuteUpdateAsync(s => s.SetProperty(i => i.Mapping, mapping.ToJson()), Ct));
        return import;
    }

    [Fact]
    public async Task A_thousand_row_csv_with_parents_labels_and_estimates_plans_what_it_commits()
    {
        var csv = new StringBuilder("Key,Title,Type,Status,Parent,Labels,Estimate,Due\n");
        for (var n = 1; n <= 1000; n++)
        {
            var (type, parent) = n <= 10 ? ("Epic", "") : n <= 100 ? ("Feature", $"CSV-{(n % 10) + 1}") : ("Task", $"CSV-{(n % 90) + 11}");
            csv.Append(CultureInfo.InvariantCulture, $"CSV-{n},Row {n},{type},{(n % 3 == 0 ? "Done" : "To Do")},{parent},\"team-{n % 4}, csv\",{n % 8},2030-01-{(n % 28) + 1:00}\n");
        }

        var import = await CsvImportAsync(Encoding.UTF8.GetBytes(csv.ToString()), m => m with
        {
            Projects = new Dictionary<string, ProjectTarget> { ["csv"] = new("CSV", "From a spreadsheet") },
        });

        var dryRun = await RunAsync(import, ImportRunKind.DryRun);
        dryRun.State.ShouldBe(ImportRunState.Succeeded, dryRun.Error);
        var plan = JsonSerializer.Deserialize<ImportPlan>(dryRun.Report!, JsonSerializerOptions.Web)!;
        plan.Create.Count.ShouldBe(1000);

        var commit = await RunAsync(import, ImportRunKind.Commit);
        commit.State.ShouldBe(ImportRunState.Succeeded, commit.Error);
        var counts = Counts(commit);
        counts.Issues.ShouldBe(plan.Create.Count);
        counts.Labels.ShouldBe(plan.LabelsToCreate);

        var task = await IssueAsync("CSV-500");
        task.Parent!.Key.ShouldBe($"CSV-{(500 % 90) + 11}");
        task.Labels.ShouldBe(["csv", "team-0"]);
        task.Effort.ShouldBe(500 % 8);
        (await IssueAsync("CSV-50")).Type.ShouldBe(IssueType.Feature);
        await WithDbAsync(async db => (await db.Issues.CountAsync(Ct)).ShouldBe(1000));

        // A key column makes it re-runnable: nothing is written twice.
        (Counts(await RunAsync(import, ImportRunKind.Commit)).Issues).ShouldBe(0);
    }

    [Fact]
    public async Task A_danish_csv_in_windows_1252_keeps_æøå()
    {
        var bytes = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes("Titel;Status;Ansvarlig\r\nRød grød med fløde;Færdig;Søren Ærø\r\n");

        var import = await CsvImportAsync(bytes, m => m with
        {
            Csv = m.Csv! with { Title = "Titel", Status = "Status", Assignee = "Ansvarlig" },
            Projects = new Dictionary<string, ProjectTarget> { ["csv"] = new("DK", "Dansk") },
        });
        (await RunAsync(import, ImportRunKind.Commit)).State.ShouldBe(ImportRunState.Succeeded);

        var issue = await IssueAsync("DK-1");
        issue.Title.ShouldBe("Rød grød med fløde");
        issue.Assignee!.Name.ShouldBe("Søren Ærø");
    }
}
