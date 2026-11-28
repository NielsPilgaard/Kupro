using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Kupro.Api.Features.Ai;
using Kupro.Api.Features.Email;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Cli;
using Kupro.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CliApp = Kupro.Cli.Cli;

namespace Kupro.Tests.Features;

/// <summary>
/// Task 035 (ADR-022): a workspace's own AI connections. Fake seams record which key each call used. Kupro's own route
/// uses <c>kupro-key</c>; a one-seat workspace has 100 cents of budget and each utterance's audio costs 50, so two
/// utterances spend it. The vendor check refuses keys containing "bad"; calls refuse keys starting "revoked".
/// </summary>
[Collection(nameof(StorageTests))]
public sealed class AiConnectionTests(PostgresFixture postgres, StorageFixture storage)
    : ApiTestBase(postgres, Settings(storage)), IClassFixture<StorageFixture>
{
    private const string KuproKey = "kupro-key";

    /// <summary>
    /// Only the key ring from the storage fixture: with snapshot storage too, this API's import job would pick up other
    /// test classes' import runs in the shared database and fail them, their snapshots being in another bucket.
    /// </summary>
    private static Dictionary<string, string?> Settings(StorageFixture storage) => new(storage.Settings.Where(s => s.Key.StartsWith("KeyStorage:", StringComparison.Ordinal)))
    {
        ["MeetingAgent:Transcription:Endpoint"] = "https://speech.test/v1",
        ["MeetingAgent:Transcription:Model"] = "speech",
        ["MeetingAgent:Transcription:ApiKey"] = KuproKey,
        ["MeetingAgent:Transcription:EuroPerAudioMinute"] = "3",
        ["MeetingAgent:Extraction:Endpoint"] = "https://llm.test/v1",
        ["MeetingAgent:Extraction:Model"] = "llm",
        ["MeetingAgent:Extraction:ApiKey"] = KuproKey,
        ["MeetingAgent:Budget:HoursPerSeat"] = "1",
        ["MeetingAgent:Budget:EuroPerHour"] = "1",
    };

    private readonly RecordingSpeech _speech = new();
    private readonly RecordingExtractor _extractor = new();
    private readonly RecordingEmail _email = new();
    private readonly TestClock _clock = new();
    private WebApplicationFactory<Program> _app = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _app = Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ISpeechToText>(_speech);
            s.AddSingleton<IProposalExtractor>(_extractor);
            s.AddSingleton<IAiModels, FakeModels>();
            s.AddSingleton<IEmailSender>(_email);
            s.AddSingleton<TimeProvider>(_clock);
        }));
    }

    public override async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task An_own_scaleway_key_serves_meetings_with_no_budget()
    {
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);
        await AudioAsync(client, ws, meeting);
        await AudioAsync(client, ws, meeting);
        await ShouldBeProblemAsync(await PostAudioAsync(client, ws, meeting), HttpStatusCode.Conflict, "meeting_budget_spent");

        var connection = await AddAsync(client, ws, new AddAiConnectionRequest(AiProviders.ScalewayId, "scw-secret-0001",
            UseFor: [AiCapability.Transcription, AiCapability.MeetingProposals, AiCapability.VoiceCommands]));
        connection.Status.ShouldBe(AiConnectionStatus.Healthy);
        connection.KeyHint.ShouldBe("0001");
        var before = _speech.Keys.Count(k => k == KuproKey);

        for (var i = 0; i < 4; i++)
        {
            (await AudioAsync(client, ws, meeting)).Extractor.ShouldBe(ExtractorKind.Model);
        }

        _speech.Keys.Count(k => k == "scw-secret-0001").ShouldBe(4);
        _speech.Keys.Count(k => k == KuproKey).ShouldBe(before, "Kupro's key isn't used once the workspace has its own");
        _extractor.Keys.ShouldContain("scw-secret-0001");
        _speech.Endpoints.Last().ShouldBe("https://api.scaleway.ai/v1");
        _speech.Models.Last().ShouldBe("whisper-large-v3");
        var status = await StatusAsync(client, ws);
        status.Routes.Where(r => r.Language is null).ShouldAllBe(r => r.PaidBy == AiPaidBy.Workspace && r.Available && r.TestedModel);
        var own = status.Usage.Single(u => u.PaidBy == AiPaidBy.Workspace);
        own.AudioMinutes.ShouldBe(0.7); // 4 × 10 s
        own.InputTokens.ShouldBe(4000);
        own.EstimatedEuro.ShouldBeGreaterThan(0);
        // Kupro's budget is still spent: the own key's calls didn't count against it, and the hours don't come back.
        (await GetAsync<MeetingSettingsResponse>(client, $"/api/v1/workspaces/{ws}/meeting-settings")).Usage!.Spent.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refused_own_key_stops_the_microphone_and_never_borrows_kupros_key()
    {
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);
        var connection = await AddAsync(client, ws, new AddAiConnectionRequest(AiProviders.ScalewayId, "revoked-key-0001",
            UseFor: [AiCapability.Transcription, AiCapability.MeetingProposals]));
        connection.Status.ShouldBe(AiConnectionStatus.Healthy, "the vendor still listed models when it was saved");
        var kuproCalls = _speech.Keys.Count(k => k == KuproKey) + _extractor.Keys.Count(k => k == KuproKey);

        await ShouldBeProblemAsync(await PostAudioAsync(client, ws, meeting), HttpStatusCode.Conflict, "ai_connection_rejected");
        await ShouldBeProblemAsync(await PostAudioAsync(client, ws, meeting), HttpStatusCode.Conflict, "ai_connection_rejected");
        var typed = await SayAsync(client, ws, meeting, "KUP-1 is done.");

        typed.Extractor.ShouldBe(ExtractorKind.Keyword);
        typed.Proposals.ShouldHaveSingleItem();
        (_speech.Keys.Count(k => k == KuproKey) + _extractor.Keys.Count(k => k == KuproKey)).ShouldBe(kuproCalls);
        _email.Rejections().ShouldHaveSingleItem().To.ShouldBe(OwnerEmail(ws));
        var status = await StatusAsync(client, ws);
        var rejected = status.Connections.ShouldHaveSingleItem();
        rejected.Status.ShouldBe(AiConnectionStatus.Rejected);
        rejected.LastError.ShouldNotBeNull().ShouldContain("401");
        status.Routes.Single(r => r.Capability == AiCapability.Transcription).Available.ShouldBeFalse();
        (await MeetingAsync(client, ws, meeting)).CanTranscribe.ShouldBeFalse();

        var fixedKey = await ReadAsync<AiConnectionResponse>(await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{ws}/ai/connections/{connection.Id}/key", new SetAiConnectionKeyRequest("scw-new-key-0002"), Json.Options, Ct));

        fixedKey.Status.ShouldBe(AiConnectionStatus.Healthy);
        await AudioAsync(client, ws, meeting);
        _speech.Keys.Last().ShouldBe("scw-new-key-0002");
    }

    [Fact]
    public async Task Removing_the_connection_goes_back_to_kupros_hours()
    {
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);
        var connection = await AddAsync(client, ws, new AddAiConnectionRequest(AiProviders.ScalewayId, "scw-secret-0003",
            UseFor: [AiCapability.Transcription]));
        await AudioAsync(client, ws, meeting);
        _speech.Keys.Last().ShouldBe("scw-secret-0003");

        (await client.DeleteAsync($"/api/v1/workspaces/{ws}/ai/connections/{connection.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await AudioAsync(client, ws, meeting);

        _speech.Keys.Last().ShouldBe(KuproKey);
        (await StatusAsync(client, ws)).Routes.Single(r => r.Capability == AiCapability.Transcription).PaidBy.ShouldBe(AiPaidBy.Kupro);
    }

    [Fact]
    public async Task Workspaces_never_share_keys_and_a_new_key_takes_effect_at_once()
    {
        var (a, wsA) = await WorkspaceAsync();
        var (b, wsB) = await WorkspaceAsync();
        var inA = await StartAsync(a, wsA);
        var inB = await StartAsync(b, wsB);
        var connA = await AddAsync(a, wsA, new AddAiConnectionRequest(AiProviders.ScalewayId, "key-of-workspace-a", UseFor: [AiCapability.Transcription]));
        await AddAsync(b, wsB, new AddAiConnectionRequest(AiProviders.ScalewayId, "key-of-workspace-b", UseFor: [AiCapability.Transcription]));

        await Task.WhenAll(Enumerable.Range(0, 10).SelectMany(_ => new[] { AudioAsync(a, wsA, inA), AudioAsync(b, wsB, inB) }));

        _speech.Keys.Count(k => k == "key-of-workspace-a").ShouldBe(10);
        _speech.Keys.Count(k => k == "key-of-workspace-b").ShouldBe(10);
        await ReadAsync<AiConnectionResponse>(await a.PutAsJsonAsync(
            $"/api/v1/workspaces/{wsA}/ai/connections/{connA.Id}/key", new SetAiConnectionKeyRequest("key-of-workspace-a-v2"), Json.Options, Ct));
        await AudioAsync(a, wsA, inA);
        _speech.Keys.Last().ShouldBe("key-of-workspace-a-v2");
    }

    [Fact]
    public async Task A_custom_endpoint_must_be_public_https_and_confirmed_in_the_eu()
    {
        var (client, ws) = await WorkspaceAsync();
        var url = $"/api/v1/workspaces/{ws}/ai/connections";

        await ShouldBeProblemAsync(await client.PostAsJsonAsync(url, Custom("http://51.15.0.10/v1"), Json.Options, Ct),
            HttpStatusCode.BadRequest, "endpoint_not_https");
        await ShouldBeProblemAsync(await client.PostAsJsonAsync(url, Custom("https://10.0.0.5/v1"), Json.Options, Ct),
            HttpStatusCode.BadRequest, "endpoint_not_public");
        await ShouldBeProblemAsync(await client.PostAsJsonAsync(url, Custom("https://169.254.169.254/v1"), Json.Options, Ct),
            HttpStatusCode.BadRequest, "endpoint_not_public");
        await ShouldBeProblemAsync(await client.PostAsJsonAsync(url, Custom("https://51.15.0.10/v1") with { EuConfirmed = false }, Json.Options, Ct),
            HttpStatusCode.BadRequest, "eu_confirmation_required");
        await ShouldBeProblemAsync(await client.PostAsJsonAsync(url, Custom("https://51.15.0.10/v1") with { TranscriptionModel = null }, Json.Options, Ct),
            HttpStatusCode.BadRequest, "model_required");

        var whisper = await AddAsync(client, ws, Custom("https://51.15.0.10/v1"));

        whisper.EuConfirmedById.ShouldNotBeNull();
        whisper.EuConfirmedAt.ShouldNotBeNull();
        whisper.Endpoint.ShouldBe("https://51.15.0.10/v1");
    }

    [Fact]
    public async Task A_self_hosted_whisper_transcribes_while_scaleway_reads_the_meeting()
    {
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);
        await AddAsync(client, ws, Custom("https://51.15.0.10/v1"));
        await AddAsync(client, ws, new AddAiConnectionRequest(AiProviders.ScalewayId, "scw-secret-0004", UseFor: [AiCapability.MeetingProposals]));

        await AudioAsync(client, ws, meeting);

        _speech.Keys.Last().ShouldBe("whisper-token-1234");
        _speech.Endpoints.Last().ShouldBe("https://51.15.0.10/v1");
        _speech.Models.Last().ShouldBe("hviske-v3");
        _extractor.Keys.Last().ShouldBe("scw-secret-0004");
        var status = await StatusAsync(client, ws);
        status.Routes.Single(r => r.Capability == AiCapability.Transcription).TestedModel.ShouldBeFalse();
    }

    [Fact]
    public async Task A_key_the_vendor_refuses_is_saved_rejected_and_nothing_is_routed_to_it()
    {
        var (client, ws) = await WorkspaceAsync();

        var bad = await AddAsync(client, ws, new AddAiConnectionRequest(AiProviders.ScalewayId, "bad-key-000000",
            UseFor: [AiCapability.Transcription]));

        bad.Status.ShouldBe(AiConnectionStatus.Rejected);
        bad.LastError.ShouldNotBeNull().ShouldContain("401");
        (await StatusAsync(client, ws)).Routes.Single(r => r.Capability == AiCapability.Transcription).PaidBy.ShouldBe(AiPaidBy.Kupro);
    }

    [Fact]
    public async Task On_its_own_key_a_meeting_has_no_three_hour_limit()
    {
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);
        _clock.Advance(TimeSpan.FromHours(4));
        await AddAsync(client, ws, new AddAiConnectionRequest(AiProviders.ScalewayId, "scw-secret-0005", UseFor: [AiCapability.Transcription]));

        await AudioAsync(client, ws, meeting);

        _speech.Keys.Last().ShouldBe("scw-secret-0005");
    }

    [Fact]
    public async Task Approval_stats_split_by_the_model_that_proposed_and_mark_an_untested_one()
    {
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);
        var kupros = (await SayAsync(client, ws, meeting, "KUP-1 is done.")).Proposals.ShouldHaveSingleItem();
        await ReviewAsync(client, ws, meeting, kupros, ReviewDecision.Reject);
        await AddAsync(client, ws, new AddAiConnectionRequest(AiProviders.ScalewayId, "scw-secret-0006",
            UseFor: [AiCapability.MeetingProposals], ChatModel: "own-llm"));
        var own = (await SayAsync(client, ws, meeting, "KUP-1 is done.")).Proposals.ShouldHaveSingleItem();
        await ReviewAsync(client, ws, meeting, own, ReviewDecision.Approve);

        var settings = await GetAsync<MeetingSettingsResponse>(client, $"/api/v1/workspaces/{ws}/meeting-settings");

        settings.ProposalModel.ShouldBe(new ProposalModelInfo("own-llm", Tested: false));
        settings.Stats.Single(s => s.Operation == ProposalOperation.State).ShouldBe(new OperationStats(ProposalOperation.State, 2, 1, 0, 1));
        settings.StatsByModel.Select(m => (m.Model, m.Tested)).ShouldBe([("llm", true), ("own-llm", false)]);
        settings.StatsByModel[0].Operations.Single(s => s.Operation == ProposalOperation.State).Rejected.ShouldBe(1);
        settings.StatsByModel[1].Operations.Single(s => s.Operation == ProposalOperation.State).ApprovedUnchanged.ShouldBe(1);
    }

    [Fact]
    public async Task Keys_are_never_returned()
    {
        const string secret = "scw-very-secret-key-9876";
        var (client, ws) = await WorkspaceAsync();
        var added = await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/ai/connections",
            new AddAiConnectionRequest(AiProviders.ScalewayId, secret, UseFor: [AiCapability.Transcription]), Json.Options, Ct);

        foreach (var body in new[]
                 {
                     await added.Content.ReadAsStringAsync(Ct),
                     await client.GetStringAsync($"/api/v1/workspaces/{ws}/ai", Ct),
                     await client.GetStringAsync($"/api/v1/workspaces/{ws}/ai/providers", Ct),
                 })
        {
            body.ShouldNotContain(secret);
            body.ShouldNotContain("very-secret");
        }
    }

    [Fact]
    public async Task Keys_never_reach_a_log_line_or_a_trace()
    {
        string[] secrets = ["revoked-leak-secret-1", "scw-leak-secret-2", "bad-leak-secret-3"];
        var logs = new AllLogs();
        var traces = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => traces.Enqueue(string.Join('\n', [
                a.DisplayName, a.StatusDescription ?? "",
                .. a.TagObjects.Select(t => $"{t.Key}={t.Value}"),
                .. a.Events.SelectMany(e => e.Tags.Select(t => $"{e.Name} {t.Key}={t.Value}")),
                .. a.Baggage.Select(b => $"{b.Key}={b.Value}"),
            ])),
        };
        ActivitySource.AddActivityListener(listener);
        await using var app = _app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddLogging(l =>
        {
            l.AddProvider(logs);
            l.AddFilter<AllLogs>(null, LogLevel.Trace);
        })));
        var (owner, ws) = await WorkspaceAsync();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;
        var meeting = await StartAsync(client, ws);

        var connection = await AddAsync(client, ws, new AddAiConnectionRequest(AiProviders.ScalewayId, secrets[0],
            UseFor: [AiCapability.Transcription, AiCapability.MeetingProposals]));
        await ShouldBeProblemAsync(await PostAudioAsync(client, ws, meeting), HttpStatusCode.Conflict, "ai_connection_rejected");
        await SayAsync(client, ws, meeting, "KUP-1 is done.");
        await ReadAsync<AiConnectionResponse>(await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{ws}/ai/connections/{connection.Id}/key", new SetAiConnectionKeyRequest(secrets[1]), Json.Options, Ct));
        await AudioAsync(client, ws, meeting);
        await SayAsync(client, ws, meeting, "KUP-1 is in review.");
        await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/ai/connections",
            new AddAiConnectionRequest(AiProviders.ScalewayId, secrets[2], "Second"), Json.Options, Ct);

        logs.Lines.ShouldNotBeEmpty();
        traces.ShouldNotBeEmpty();
        foreach (var secret in secrets)
        {
            logs.Lines.Where(l => l.Contains(secret, StringComparison.Ordinal)).ShouldBeEmpty($"log lines with {secret}");
            traces.Where(t => t.Contains(secret, StringComparison.Ordinal)).ShouldBeEmpty($"traces with {secret}");
        }
    }

    [Fact]
    public async Task Only_people_with_billing_access_change_ai_settings_and_agents_never_do()
    {
        var (owner, ws) = await WorkspaceAsync();
        var member = await JoinAsync(owner, ws, WorkspaceRole.Member);
        var billing = await JoinAsync(owner, ws, WorkspaceRole.Member, billing: true);
        var agent = await NewTokenAsync(owner, new CreateApiTokenRequest("bot", ApiTokenKind.Agent, "ai-bot", null, ws));
        var request = new AddAiConnectionRequest(AiProviders.ScalewayId, "scw-secret-0006");
        var url = $"/api/v1/workspaces/{ws}/ai/connections";

        (await Client(member).PostAsJsonAsync(url, request, Json.Options, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Client(agent.Secret).PostAsJsonAsync(url, request, Json.Options, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var added = await ReadAsync<AiConnectionResponse>(await Client(billing).PostAsJsonAsync(url, request, Json.Options, Ct), HttpStatusCode.Created);
        (await Client(member).DeleteAsync($"{url}/{added.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Client(agent.Secret).PutAsJsonAsync($"/api/v1/workspaces/{ws}/ai/routes",
            new SetAiRouteRequest(AiCapability.Transcription, added.Id), Json.Options, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await GetAsync<AiStatusResponse>(Client(member), $"/api/v1/workspaces/{ws}/ai")).Connections.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_cli_connects_a_key_from_stdin_and_shows_what_runs_where()
    {
        var (client, ws) = await WorkspaceAsync();
        var token = await NewTokenAsync(client, new CreateApiTokenRequest("cli", ApiTokenKind.Personal));

        var (exit, _, error) = await CliAsync(token.Secret, ws, new StringReader("scw-secret-from-stdin-7"),
            "ai", "connections", "add", "--use-for-all");
        exit.ShouldBe(0, error);
        var (statusExit, table, _) = await CliAsync(token.Secret, ws, null, "ai", "status");

        statusExit.ShouldBe(0);
        table.ShouldContain("you, no limit");
        table.ShouldContain("…in-7");
        table.ShouldNotContain("scw-secret-from-stdin-7");
        (await StatusAsync(client, ws)).Routes.Where(r => r.Language is null).ShouldAllBe(r => r.PaidBy == AiPaidBy.Workspace);
    }

    // ---------------------------------------------------------------- helpers

    private static AddAiConnectionRequest Custom(string endpoint) => new(
        AiProviders.OpenAiCompatibleId, "whisper-token-1234", "Self-hosted Whisper", endpoint, EuConfirmed: true,
        UseFor: [AiCapability.Transcription], TranscriptionModel: "hviske-v3");

    private readonly Dictionary<string, string> _owners = [];

    private string OwnerEmail(string ws) => _owners[ws];

    private HttpClient Client(TestUser user) => Client(KuproApiFactory.CreateJwt(user));

    private HttpClient Client(string bearer)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private async Task<(HttpClient Client, string Workspace)> WorkspaceAsync()
    {
        var (owner, workspace, user) = await NewWorkspaceAsync();
        _owners[workspace.Slug] = user.Email;
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;
        await ReadAsync<ProjectResponse>(await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/projects",
            new CreateProjectRequest("KUP", "Kupro"), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<IssueDetail>(await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/issues",
            new CreateIssueRequest("KUP", "Render delivery plan", State: IssueState.Todo), Json.Options, Ct), HttpStatusCode.Created);
        return (client, workspace.Slug);
    }

    private async Task<TestUser> JoinAsync(HttpClient owner, string ws, WorkspaceRole role, bool billing = false)
    {
        var user = TestUser.New("Joiner");
        var invited = await ReadAsync<MemberResponse>(await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/members",
            new InviteMemberRequest(user.Email, role, billing), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<WorkspaceSummary>(await Client(user).PostAsync($"/api/v1/invites/{invited.Id}/accept", null, Ct));
        return user;
    }

    private static async Task<AiConnectionResponse> AddAsync(HttpClient client, string ws, AddAiConnectionRequest request) =>
        await ReadAsync<AiConnectionResponse>(await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/ai/connections", request, Json.Options, Ct),
            HttpStatusCode.Created);

    private static Task<AiStatusResponse> StatusAsync(HttpClient client, string ws) => GetAsync<AiStatusResponse>(client, $"/api/v1/workspaces/{ws}/ai");

    private static async Task<MeetingResponse> StartAsync(HttpClient client, string ws) =>
        await ReadAsync<MeetingResponse>(await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/meetings",
            new StartMeetingRequest("KUP"), Json.Options, Ct), HttpStatusCode.Created);

    private static Task<MeetingResponse> MeetingAsync(HttpClient client, string ws, MeetingResponse meeting) =>
        GetAsync<MeetingResponse>(client, $"/api/v1/workspaces/{ws}/meetings/{meeting.Id}");

    private static async Task<TranscriptResult> SayAsync(HttpClient client, string ws, MeetingResponse meeting, string text) =>
        await ReadAsync<TranscriptResult>(await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/meetings/{meeting.Id}/transcript",
            new TranscriptRequest(text), Json.Options, Ct));

    private static async Task ReviewAsync(HttpClient client, string ws, MeetingResponse meeting, ProposalResponse proposal, ReviewDecision decision) =>
        (await ReadAsync<List<ProposalReviewResult>>(await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/meetings/{meeting.Id}/proposals/review",
            new ReviewProposalsRequest([new ProposalReview(proposal.Id, decision)]), Json.Options, Ct))).ShouldHaveSingleItem().Error.ShouldBeNull();

    private static Task<HttpResponseMessage> PostAudioAsync(HttpClient client, string ws, MeetingResponse meeting)
    {
        var body = new ByteArrayContent(new byte[64]);
        body.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return client.PostAsync($"/api/v1/workspaces/{ws}/meetings/{meeting.Id}/audio", body, Ct);
    }

    private static async Task<TranscriptResult> AudioAsync(HttpClient client, string ws, MeetingResponse meeting) =>
        await ReadAsync<TranscriptResult>(await PostAudioAsync(client, ws, meeting));

    private async Task<(int Exit, string Out, string Error)> CliAsync(string token, string ws, TextReader? stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var config = new CliConfig { InMemory = true, Url = "http://localhost", Token = token, Workspace = ws };
        CliApp.Runtime.Value = new CliRuntime(config, stdout, stderr, _app.Server.CreateHandler(), false, stdin);
        var exit = await CliApp.BuildRoot().Parse(args)
            .InvokeAsync(new System.CommandLine.InvocationConfiguration { Error = stderr, Output = stdout }, Ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static HttpRequestException Refused() => new("401 Unauthorized", null, HttpStatusCode.Unauthorized);

    /// <summary>Every log line in every category, as text: message, each structured value, scopes and exception.</summary>
    private sealed class AllLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                lines.Enqueue(Text(state));
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{formatter(state, exception)}\n{Text(state)}\n{exception}");

            private static string Text(object? state) => state is IEnumerable<KeyValuePair<string, object?>> fields
                ? string.Join('\n', fields.Select(f => $"{f.Key}={f.Value}"))
                : state?.ToString() ?? "";
        }
    }

    private sealed class RecordingSpeech : ISpeechToText
    {
        private readonly ConcurrentQueue<(string Key, string Endpoint, string Model)> _calls = new();

        public List<string> Keys => [.. _calls.Select(c => c.Key)];
        public List<string> Endpoints => [.. _calls.Select(c => c.Endpoint)];
        public List<string> Models => [.. _calls.Select(c => c.Model)];

        public Task<Transcription> TranscribeAsync(Stream wav, string language, ModelRoute route, CancellationToken ct)
        {
            if (route.ApiKey!.StartsWith("revoked", StringComparison.Ordinal))
            {
                throw Refused();
            }

            _calls.Enqueue((route.ApiKey, route.Endpoint!, route.Model!));
            return Task.FromResult(new Transcription("KUP-1 is done.", 10));
        }
    }

    private sealed class RecordingExtractor : IProposalExtractor
    {
        private static readonly KeywordProposalExtractor Keywords = new();
        private readonly ConcurrentQueue<string> _keys = new();

        public List<string> Keys => [.. _keys];

        public async Task<Extraction> ExtractAsync(ExtractionContext context, ModelRoute route, CancellationToken ct)
        {
            if (route.ApiKey!.StartsWith("revoked", StringComparison.Ordinal))
            {
                throw Refused();
            }

            _keys.Enqueue(route.ApiKey);
            return new Extraction((await Keywords.ExtractAsync(context, ct)).Actions, new ModelUsage(1000, 0, 500));
        }
    }

    private sealed class FakeModels : IAiModels
    {
        public Task<IReadOnlyList<string>> ListAsync(string baseUrl, string key, CancellationToken ct) =>
            key.Contains("bad", StringComparison.Ordinal)
                ? throw Refused()
                : Task.FromResult<IReadOnlyList<string>>(["whisper-large-v3", "deepseek-v4.1-flash", "hviske-v3", "own-llm"]);
    }

    private sealed class RecordingEmail : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> _sent = new();

        public List<EmailMessage> Rejections() => [.. _sent.Where(m => m.Subject.Contains("rejected the key", StringComparison.Ordinal))];

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            _sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private TimeSpan _offset;

        public void Advance(TimeSpan by) => _offset += by;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;
    }
}
