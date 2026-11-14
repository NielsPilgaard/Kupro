using System.Net;
using System.Text;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>The import REST surface: the whole flow, who may use it, tenancy (task 023 Surfaces, ADR-009).</summary>
[Collection(nameof(StorageTests))]
public sealed class ImportsApiTests(PostgresFixture postgres, StorageFixture storage)
    : ApiTestBase(postgres, storage.Settings), IClassFixture<StorageFixture>
{
    private const string Csv = "Key,Title,Status,Assignee\nCSV-1,First,To Do,Sofie Larsen\nCSV-2,Second,Done,\n";

    [Fact]
    public async Task An_owner_uploads_maps_plans_runs_and_finishes_a_csv_import()
    {
        var (client, workspace, _) = await NewWorkspaceAsync("Imports");
        var url = $"/api/v1/workspaces/{workspace.Slug}/imports";

        var import = await ReadAsync<ImportResponse>(await client.PostAsJsonAsync(url, new CreateImportRequest(SourceKinds.Csv), Json.Options, Ct), HttpStatusCode.Created);
        import.HasToken.ShouldBeFalse();
        var upload = await ReadAsync<ImportUpload>(await client.PutAsync($"{url}/{import.Id}/source", new ByteArrayContent(Encoding.UTF8.GetBytes(Csv)), Ct));
        upload.Csv!.Rows.ShouldBe(2);
        upload.Csv.Columns.Key.ShouldBe("Key");

        var proposed = await GetAsync<ImportMappingResponse>(client, $"{url}/{import.Id}/mapping");
        proposed.Saved.ShouldBeFalse();
        var mapping = proposed.Mapping with { Projects = new Dictionary<string, ProjectTarget> { ["csv"] = new("CSV", "Spreadsheet") } };
        await ShouldBeProblemAsync(await client.PutAsJsonAsync($"{url}/{import.Id}/mapping",
            mapping with { Projects = new Dictionary<string, ProjectTarget> { ["csv"] = new("x", "Bad") } }, Json.Options, Ct), HttpStatusCode.BadRequest, "invalid_mapping");
        await ReadAsync<ImportMapping>(await client.PutAsJsonAsync($"{url}/{import.Id}/mapping", mapping, Json.Options, Ct));
        (await GetAsync<ImportMappingResponse>(client, $"{url}/{import.Id}/mapping")).Saved.ShouldBeTrue();

        var plan = await ReadAsync<ImportRunResponse>(await client.PostAsync($"{url}/{import.Id}/plan", null, Ct), HttpStatusCode.Accepted);
        var planned = await WaitAsync(client, $"{url}/{import.Id}/runs/{plan.Id}");
        planned.Plan!.Create.Count.ShouldBe(2);

        var run = await ReadAsync<ImportRunResponse>(await client.PostAsync($"{url}/{import.Id}/runs", null, Ct), HttpStatusCode.Accepted);
        var done = await WaitAsync(client, $"{url}/{import.Id}/runs/{run.Id}");
        done.Run.Counts!.Issues.ShouldBe(2);
        done.Report!.Conflicts.Count.ShouldBe(0);
        (await GetAsync<IssueDetail>(client, $"/api/v1/workspaces/{workspace.Slug}/issues/CSV-2")).State.ShouldBe(IssueState.Done);

        (await GetAsync<List<ImportResponse>>(client, url)).Single().LastRun!.Id.ShouldBe(run.Id);
        (await GetAsync<List<ImportRunResponse>>(client, $"{url}/{import.Id}/runs")).Count.ShouldBe(2);
        (await GetAsync<List<ImportConflictResponse>>(client, $"{url}/{import.Id}/conflicts")).ShouldBeEmpty();
        await ShouldBeProblemAsync(await client.PutAsJsonAsync($"{url}/{import.Id}/auto-sync", new AutoSyncRequest(true), Json.Options, Ct), HttpStatusCode.BadRequest, "auto_sync_needs_token");

        var finished = await ReadAsync<ImportResponse>(await client.PostAsync($"{url}/{import.Id}/finish", null, Ct));
        finished.State.ShouldBe(ImportState.Finished);
        await ShouldBeProblemAsync(await client.PostAsync($"{url}/{import.Id}/runs", null, Ct), HttpStatusCode.Conflict, "import_closed");
    }

    [Fact]
    public async Task Only_owners_signed_in_as_people_reach_imports_and_only_in_their_workspace()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync("Owned");
        var url = $"/api/v1/workspaces/{workspace.Slug}/imports";
        var import = await ReadAsync<ImportResponse>(await owner.PostAsJsonAsync(url, new CreateImportRequest(SourceKinds.Csv), Json.Options, Ct), HttpStatusCode.Created);

        var agent = await NewTokenAsync(owner, new CreateApiTokenRequest("bot", ApiTokenKind.Agent, "bot", null, workspace.Slug));
        using var viaAgent = Factory.CreateBearerClient(agent.Secret);
        (await viaAgent.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an agent token is refused, whoever made it");

        var member = TestUser.New("Ida");
        await ReadAsync<MemberResponse>(await owner.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/members",
            new InviteMemberRequest(member.Email, WorkspaceRole.Member), Json.Options, Ct), HttpStatusCode.Created);
        using var memberClient = Factory.CreateUserClient(member);
        (await memberClient.PostAsync("/api/v1/invites/accept-all", null, Ct)).Dispose();
        var forbidden = await memberClient.GetAsync(url, Ct);
        forbidden.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

        var (other, theirs, _) = await NewWorkspaceAsync("Theirs");
        await ShouldBeProblemAsync(await other.GetAsync($"/api/v1/workspaces/{theirs.Slug}/imports/{import.Id}", Ct), HttpStatusCode.NotFound, "import_not_found");
        await ShouldBeProblemAsync(await other.GetAsync($"{url}/{import.Id}", Ct), HttpStatusCode.NotFound, "workspace_not_found");
    }

    [Fact]
    public async Task Without_key_storage_a_token_import_answers_503()
    {
        var (owner, workspace, _) = await NewWorkspaceAsync("NoKeys");
        await using var noKeys = Factory.WithWebHostBuilder(b => b.UseSetting("KeyStorage:ServiceUrl", ""));
        using var bearer = noKeys.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;

        await ShouldBeProblemAsync(await bearer.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/imports",
            new CreateImportRequest(SourceKinds.Linear, Token: "lin_api_x"), Json.Options, Ct), HttpStatusCode.ServiceUnavailable, "imports_unavailable");
    }

    [Fact]
    public async Task The_cli_imports_a_csv_from_file_to_report_with_a_mapping_file_and_a_token_from_stdin()
    {
        var (client, workspace, _) = await NewWorkspaceAsync("Cli");
        var token = (await NewTokenAsync(client, new CreateApiTokenRequest("laptop", ApiTokenKind.Personal))).Secret;
        var csv = Path.GetTempFileName();
        var mappingFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(csv, Csv, Ct);
            var (exit, output, error) = await CliAsync(token, workspace.Slug, null, "import", "csv", csv);
            exit.ShouldBe(0, error);
            output.ShouldContain("2 rows");
            var id = (await GetAsync<List<ImportResponse>>(client, $"/api/v1/workspaces/{workspace.Slug}/imports")).Single().Id.ToString();

            (exit, output, error) = await CliAsync(token, workspace.Slug, null, "import", "mapping", id);
            exit.ShouldBe(0, error);
            var mapping = System.Text.Json.Nodes.JsonNode.Parse(output)!;
            mapping["projects"]!["csv"]!["key"] = "CLI";
            await File.WriteAllTextAsync(mappingFile, mapping.ToJsonString(), Ct);
            (exit, _, error) = await CliAsync(token, workspace.Slug, null, "import", "mapping", id, "--set", mappingFile);
            exit.ShouldBe(0, error);

            (exit, output, error) = await CliAsync(token, workspace.Slug, null, "import", "plan", id);
            exit.ShouldBe(0, error);
            output.ShouldContain("Create");
            (exit, output, error) = await CliAsync(token, workspace.Slug, null, "import", "run", id);
            exit.ShouldBe(0, error);
            output.ShouldContain("Created");
            (await GetAsync<IssueDetail>(client, $"/api/v1/workspaces/{workspace.Slug}/issues/CLI-1")).Title.ShouldBe("First");

            // The source key stays an alias: `kupro issue get` finds the issue by it.
            (exit, output, error) = await CliAsync(token, workspace.Slug, null, "issue", "get", "CSV-1");
            exit.ShouldBe(0, error);
            output.ShouldContain("CLI-1");
            output.ShouldContain("First");

            // A token never comes from an argument: here it is piped in, for a source Kupro can't read itself.
            (exit, _, error) = await CliAsync(token, workspace.Slug, "secret-from-stdin", "import", "create", "monday");
            exit.ShouldNotBe(0);
            error.ShouldContain("source_unsupported");
        }
        finally
        {
            File.Delete(csv);
            File.Delete(mappingFile);
        }
    }

    [Fact]
    public async Task Every_import_endpoint_treats_another_workspaces_ids_as_missing()
    {
        var (bob, theirs, _) = await NewWorkspaceAsync("Bobs");
        var theirUrl = $"/api/v1/workspaces/{theirs.Slug}/imports";
        var theirImport = await ReadAsync<ImportResponse>(await bob.PostAsJsonAsync(theirUrl, new CreateImportRequest(SourceKinds.Csv), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<ImportUpload>(await bob.PutAsync($"{theirUrl}/{theirImport.Id}/source", new ByteArrayContent(Encoding.UTF8.GetBytes(Csv)), Ct));
        var mapping = (await GetAsync<ImportMappingResponse>(bob, $"{theirUrl}/{theirImport.Id}/mapping")).Mapping;
        await ReadAsync<ImportMapping>(await bob.PutAsJsonAsync($"{theirUrl}/{theirImport.Id}/mapping", mapping, Json.Options, Ct));
        var theirRun = await ReadAsync<ImportRunResponse>(await bob.PostAsync($"{theirUrl}/{theirImport.Id}/plan", null, Ct), HttpStatusCode.Accepted);

        var (alice, mine, _) = await NewWorkspaceAsync("Alices");
        var url = $"/api/v1/workspaces/{mine.Slug}/imports";
        var myImport = await ReadAsync<ImportResponse>(await alice.PostAsJsonAsync(url, new CreateImportRequest(SourceKinds.Csv), Json.Options, Ct), HttpStatusCode.Created);
        var other = $"{url}/{theirImport.Id}";
        HttpContent Body(object body) => JsonContent.Create(body, options: Json.Options);

        var requests = new (HttpMethod Method, string Url, HttpContent? Body)[]
        {
            (HttpMethod.Get, other, null),
            (HttpMethod.Put, $"{other}/source", new ByteArrayContent(Encoding.UTF8.GetBytes(Csv))),
            (HttpMethod.Get, $"{other}/source", null),
            (HttpMethod.Get, $"{other}/mapping", null),
            (HttpMethod.Put, $"{other}/mapping", Body(mapping)),
            (HttpMethod.Post, $"{other}/plan", null),
            (HttpMethod.Post, $"{other}/runs", null),
            (HttpMethod.Get, $"{other}/runs", null),
            (HttpMethod.Get, $"{other}/runs/{theirRun.Id}", null),
            (HttpMethod.Get, $"{url}/{myImport.Id}/runs/{theirRun.Id}", null),
            (HttpMethod.Put, $"{other}/auto-sync", Body(new AutoSyncRequest(true))),
            (HttpMethod.Put, $"{other}/token", Body(new ImportTokenRequest("t"))),
            (HttpMethod.Get, $"{other}/conflicts", null),
            (HttpMethod.Post, $"{other}/conflicts/{Guid.NewGuid()}/resolve", Body(new ResolveConflictRequest(true))),
            (HttpMethod.Get, $"{other}/undo", null),
            (HttpMethod.Post, $"{other}/undo", null),
            (HttpMethod.Post, $"{other}/finish", null),
        };

        foreach (var (method, path, body) in requests)
        {
            using var response = await alice.SendAsync(new HttpRequestMessage(method, path) { Content = body }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path}");
        }

        (await GetAsync<List<ImportResponse>>(alice, url)).Select(i => i.Id).ShouldBe([myImport.Id]);
        (await GetAsync<ImportResponse>(bob, $"{theirUrl}/{theirImport.Id}")).State.ShouldBe(ImportState.Active, "nothing Alice sent reached Bob's import");
    }

    [Fact]
    public async Task The_cli_reads_jira_locally_and_uploads_a_snapshot_so_the_token_stays_on_the_machine()
    {
        var (client, workspace, _) = await NewWorkspaceAsync("Local");
        var token = (await NewTokenAsync(client, new CreateApiTokenRequest("laptop", ApiTokenKind.Personal))).Secret;
        var jira = JiraConnectorTests.Cloud();
        var url = $"/api/v1/workspaces/{workspace.Slug}/imports";
        var zip = Path.Combine(Path.GetTempPath(), $"kupro-test-{Guid.NewGuid():N}.zip");
        try
        {
            // --out writes the ZIP and stops: nothing reaches Kupro.
            var (exit, output, error) = await CliAsync(token, workspace.Slug, FakeJira.CloudToken, jira, "import", "jira", "--local", "--url", jira.SiteUrl, "--project", "web", "--out", zip);
            exit.ShouldBe(0, error);
            output.ShouldContain("kupro import snapshot");
            new FileInfo(zip).Length.ShouldBeGreaterThan(0);
            (await GetAsync<List<ImportResponse>>(client, url)).ShouldBeEmpty();

            (exit, output, error) = await CliAsync(token, workspace.Slug, FakeJira.CloudToken, jira, "import", "jira", "--local", "--url", jira.SiteUrl, "--project", "WEB");
            exit.ShouldBe(0, error);
            var import = (await GetAsync<List<ImportResponse>>(client, url)).ShouldHaveSingleItem();
            import.SourceKind.ShouldBe(SourceKinds.Snapshot);
            import.HasToken.ShouldBeFalse("the Jira token never left the CLI");
            output.ShouldContain($"Import {import.Id}");

            // The ZIP carries the attachment bytes, so the run never calls Jira.
            var proposed = (await GetAsync<ImportMappingResponse>(client, $"{url}/{import.Id}/mapping")).Mapping;
            await ReadAsync<ImportMapping>(await client.PutAsJsonAsync($"{url}/{import.Id}/mapping", proposed, Json.Options, Ct));
            var before = jira.Requests.Count;
            (exit, output, error) = await CliAsync(token, workspace.Slug, null, (HttpMessageHandler?)null, "import", "plan", import.Id.ToString());
            exit.ShouldBe(0, error);
            output.ShouldContain("Create");
            jira.Requests.Count.ShouldBe(before);

            // A re-run uploads to the same import.
            (exit, _, error) = await CliAsync(token, workspace.Slug, FakeJira.CloudToken, jira, "import", "jira", "--local", "--url", jira.SiteUrl, "--import", import.Id.ToString());
            exit.ShouldBe(0, error);
            (await GetAsync<List<ImportResponse>>(client, url)).ShouldHaveSingleItem();

            (exit, _, error) = await CliAsync(token, workspace.Slug, FakeJira.CloudToken, jira, "import", "jira", "--local", "--url", jira.SiteUrl, "--project", "NOPE");
            exit.ShouldNotBe(0);
            error.ShouldContain("no project NOPE");
            (exit, _, error) = await CliAsync(token, workspace.Slug, FakeJira.CloudToken, jira, "import", "jira", "--url", jira.SiteUrl, "--project", "WEB");
            exit.ShouldNotBe(0);
            error.ShouldContain("go with --local");
        }
        finally
        {
            File.Delete(zip);
        }
    }

    private Task<(int Exit, string Output, string Error)> CliAsync(string token, string workspace, string? stdin, params string[] args) =>
        CliAsync(token, workspace, stdin, null, args);

    private async Task<(int Exit, string Output, string Error)> CliAsync(string token, string workspace, string? stdin, HttpMessageHandler? source, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var config = new Kupro.Cli.CliConfig { InMemory = true, Url = "http://localhost", Token = token, Workspace = workspace };
        Kupro.Cli.Cli.Runtime.Value = new Kupro.Cli.CliRuntime(config, stdout, stderr, Factory.Server.CreateHandler(), false, stdin is null ? null : new StringReader(stdin), SourceTransport: source);
        var exit = await Kupro.Cli.Cli.BuildRoot().Parse(args).InvokeAsync(new System.CommandLine.InvocationConfiguration { Error = stderr, Output = stdout }, Ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static async Task<ImportRunDetail> WaitAsync(HttpClient client, string url)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (true)
        {
            var detail = await GetAsync<ImportRunDetail>(client, url);
            if (detail.Run.State is ImportRunState.Succeeded or ImportRunState.Failed)
            {
                detail.Run.State.ShouldBe(ImportRunState.Succeeded, detail.Run.Error);
                return detail;
            }

            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the run didn't finish in time");
            await Task.Delay(20, Ct);
        }
    }
}
