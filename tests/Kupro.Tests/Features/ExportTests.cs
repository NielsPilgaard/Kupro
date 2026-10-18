using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kupro.Api.Data;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Email;
using Kupro.Api.Features.Exports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Cli;
using Kupro.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CliApp = Kupro.Cli.Cli;

namespace Kupro.Tests.Features;

/// <summary>Task 016: owners export the whole workspace as a ZIP, built in the background and downloaded from storage.</summary>
public sealed class ExportTests(PostgresFixture postgres, StorageFixture storage)
    : ApiTestBase(postgres, storage.Settings), IClassFixture<StorageFixture>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private readonly HttpClient _storage = new();
    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private TestUser _owner = null!;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _owner) = await NewWorkspaceAsync("Kobber & Søn");
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("FLOW", "Kupro"), Json.Options, Ct),
            HttpStatusCode.Created);
        await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync($"{Ws}/issues",
            new CreateIssueRequest("FLOW", "Rødgrød med fløde, \"quoted\", and a comma"), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync($"{Ws}/issues", new CreateIssueRequest("FLOW", "=HYPERLINK(\"x\")"), Json.Options, Ct),
            HttpStatusCode.Created);
        await ReadAsync<CommentResponse>(await _client.PostAsJsonAsync($"{Ws}/issues/FLOW-1/comments", new CommentRequest("Første kommentar\nmed to linjer"),
            Json.Options, Ct), HttpStatusCode.Created);

        var upload = await ReadAsync<AttachmentUpload>(await _client.PostAsJsonAsync($"{Ws}/attachments",
            new CreateAttachmentRequest("skærm.png", "image/png", Png.Length, "FLOW-1"), Json.Options, Ct), HttpStatusCode.Created);
        using var content = new ByteArrayContent(Png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await _storage.PutAsync(upload.UploadUrl, content, Ct)).EnsureSuccessStatusCode();
        await ReadAsync<AttachmentResponse>(await _client.PostAsync($"{Ws}/attachments/{upload.Attachment.Id}/complete", null, Ct));
    }

    public override async ValueTask DisposeAsync()
    {
        _storage.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Owner_exports_everything_as_a_zip()
    {
        var started = await ReadAsync<ExportResponse>(await _client.PostAsync($"{Ws}/exports", null, Ct), HttpStatusCode.Accepted);
        started.State.ShouldBe(ExportState.Building);
        started.DownloadUrl.ShouldBeNull();
        var again = await ReadAsync<ExportResponse>(await _client.PostAsync($"{Ws}/exports", null, Ct), HttpStatusCode.Accepted);
        if (again.State == ExportState.Building)
        {
            again.Id.ShouldBe(started.Id, "one export at a time");
        }

        var ready = await ReadyAsync(started.Id);
        ready.Size.ShouldNotBeNull().ShouldBeGreaterThan(0);
        (await GetAsync<List<ExportResponse>>(_client, $"{Ws}/exports")).ShouldContain(e => e.Id == started.Id);

        using var zip = await DownloadAsync(ready.DownloadUrl!);
        zip.Entries.Select(e => e.FullName).ShouldContain("README.txt");

        using var json = JsonDocument.Parse(await ReadEntryAsync(zip, "workspace.json"));
        var root = json.RootElement;
        root.GetProperty("format").GetInt32().ShouldBe(1);
        root.GetProperty("workspace").GetProperty("name").GetString().ShouldBe("Kobber & Søn");
        root.GetProperty("issues").GetArrayLength().ShouldBe(2);
        root.GetProperty("issues")[0].GetProperty("title").GetString().ShouldBe("Rødgrød med fløde, \"quoted\", and a comma");
        root.GetProperty("comments")[0].GetProperty("body").GetString().ShouldBe("Første kommentar\nmed to linjer");
        root.GetProperty("users").GetArrayLength().ShouldBe(1);
        root.GetProperty("users")[0].TryGetProperty("externalId", out _).ShouldBeFalse("only name and email of users");
        root.GetProperty("activity").GetArrayLength().ShouldBeGreaterThan(0);

        var issues = await ReadEntryAsync(zip, "issues.csv", bom: true);
        var lines = issues.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("key,type,title,state");
        lines[1].ShouldStartWith("FLOW-1,task,\"Rødgrød med fløde, \"\"quoted\"\", and a comma\",");
        lines[2].ShouldStartWith("FLOW-2,task,\"'=HYPERLINK(\"\"x\"\")\",", Case.Sensitive, "formulas are neutralised");
        (await ReadEntryAsync(zip, "comments.csv", bom: true)).ShouldContain("\"Første kommentar\nmed to linjer\"");

        var file = zip.Entries.Single(e => e.FullName.StartsWith("attachments/FLOW-1/", StringComparison.Ordinal));
        file.FullName.ShouldEndWith("-skærm.png");
        await using var bytes = await file.OpenAsync(Ct);
        using var copy = new MemoryStream();
        await bytes.CopyToAsync(copy, Ct);
        copy.ToArray().ShouldBe(Png);
    }

    [Fact]
    public async Task A_missing_attachment_file_is_listed_instead_of_failing_the_export()
    {
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<WorkspaceContext>();
            context.SetForSystem(_workspace.Id);
            await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
            var attachment = await db.Attachments.SingleAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<ObjectStorage>().DeleteAsync($"{attachment.WorkspaceId}/{attachment.Id}", Ct);
        }

        var ready = await ReadyAsync((await ReadAsync<ExportResponse>(await _client.PostAsync($"{Ws}/exports", null, Ct), HttpStatusCode.Accepted)).Id);
        using var zip = await DownloadAsync(ready.DownloadUrl!);
        zip.Entries.ShouldNotContain(e => e.FullName.EndsWith(".png", StringComparison.Ordinal));
        (await ReadEntryAsync(zip, "attachments/MISSING.txt")).ShouldContain("-skærm.png");
    }

    [Fact]
    public async Task A_failed_email_does_not_fail_a_finished_export()
    {
        var email = new FailingEmailSender();
        await using var factory = Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(email)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", KuproApiFactory.CreateJwt(_owner));

        var started = await ReadAsync<ExportResponse>(await client.PostAsync($"{Ws}/exports", null, Ct), HttpStatusCode.Accepted);
        var ready = await ReadyAsync(started.Id);
        ready.DownloadUrl.ShouldNotBeNull();
        email.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Only_owners_can_export()
    {
        // Agent tokens are capped at Member, so agents can't take a copy of the workspace either.
        using var agent = Factory.CreateBearerClient((await NewTokenAsync(_client,
            new CreateApiTokenRequest("bot", ApiTokenKind.Agent, "bot", null, _workspace.Slug))).Secret);
        (await agent.PostAsync($"{Ws}/exports", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.GetAsync($"{Ws}/exports", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var started = await ReadAsync<ExportResponse>(await _client.PostAsync($"{Ws}/exports", null, Ct), HttpStatusCode.Accepted);
        var (other, otherWorkspace, _) = await NewWorkspaceAsync("Other");
        (await other.GetAsync($"/api/v1/workspaces/{otherWorkspace.Slug}/exports/{started.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetAsync($"{Ws}/exports/{started.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await ReadyAsync(started.Id);
    }

    [Fact]
    public async Task Cli_exports_and_downloads_the_zip()
    {
        var token = (await NewTokenAsync(_client, new CreateApiTokenRequest("laptop", ApiTokenKind.Personal))).Secret;
        var target = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}.zip");
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var config = new CliConfig { InMemory = true, Url = "http://localhost", Token = token, Workspace = _workspace.Slug };
            CliApp.Runtime.Value = new CliRuntime(config, stdout, stderr, Factory.Server.CreateHandler(), false, null);
            var exit = await CliApp.BuildRoot().Parse(["workspace", "export", "-o", target])
                .InvokeAsync(new System.CommandLine.InvocationConfiguration { Error = stderr, Output = stdout }, Ct);

            exit.ShouldBe(0, stderr.ToString());
            stdout.ToString().ShouldContain("Saved the export");
            await using var file = File.OpenRead(target);
            using var zip = new ZipArchive(file);
            zip.GetEntry("workspace.json").ShouldNotBeNull();
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task Exports_are_deleted_after_seven_days()
    {
        var ready = await ReadyAsync((await ReadAsync<ExportResponse>(await _client.PostAsync($"{Ws}/exports", null, Ct), HttpStatusCode.Accepted)).Id);
        ready.ExpiresAt.ShouldBe(ready.CreatedAt + TimeSpan.FromDays(7));

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
            var objects = scope.ServiceProvider.GetRequiredService<ObjectStorage>();
            await ExportJob.PurgeExpiredAsync(db, objects, DateTimeOffset.UtcNow.AddDays(6), Ct);
            (await _storage.GetAsync(ready.DownloadUrl!, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            await ExportJob.PurgeExpiredAsync(db, objects, DateTimeOffset.UtcNow.AddDays(8), Ct);
        }

        (await _client.GetAsync($"{Ws}/exports/{ready.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _storage.GetAsync(ready.DownloadUrl!, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Every_workspace_table_is_exported_or_deliberately_left_out()
    {
        using var scope = Factory.Services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContext();
        var tenantTables = db.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => typeof(ITenantOwned).IsAssignableFrom(t)).ToList();

        tenantTables.Where(t => !ExportWriter.Exported.Contains(t) && !ExportWriter.NotExported.Contains(t)).Select(t => t.Name)
            .ShouldBeEmpty("new workspace tables need a section in ExportWriter, or a reason in NotExported");
    }

    private async Task<ExportResponse> ReadyAsync(Guid id)
    {
        for (var i = 0; i < 100; i++)
        {
            var export = await GetAsync<ExportResponse>(_client, $"{Ws}/exports/{id}");
            if (export.State != ExportState.Building)
            {
                export.State.ShouldBe(ExportState.Ready);
                return export;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("Export did not finish in 10 seconds.");
    }

    private async Task<ZipArchive> DownloadAsync(string url)
    {
        using var response = await _storage.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        return new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(Ct)));
    }

    private static async Task<string> ReadEntryAsync(ZipArchive zip, string name, bool bom = false)
    {
        await using var stream = await zip.GetEntry(name).ShouldNotBeNull().OpenAsync(Ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        var bytes = buffer.ToArray();
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble).ShouldBe(bom, $"{name} BOM");
        return Encoding.UTF8.GetString(bom ? bytes[3..] : bytes);
    }

    private sealed class FailingEmailSender : IEmailSender
    {
        public int Attempts { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Attempts++;
            throw new HttpRequestException("SMTP is down");
        }
    }
}
