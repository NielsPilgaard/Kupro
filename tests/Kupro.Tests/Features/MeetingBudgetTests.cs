using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kupro.Api.Data;
using Kupro.Api.Features.Email;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>
/// Task 017: the meeting agent's AI budget. Fake seams report known usage: each utterance is 10 s of audio (50 cents at
/// €3 a minute) and each extraction 1,000 input and 500 output tokens (2 cents). One hour is worth €1 and a seat gets one
/// hour, so a one-seat workspace has 100 cents a month. Every test runs on its own day, because the daily spend is
/// server-wide and all test classes share one database.
/// </summary>
public sealed class MeetingBudgetTests(PostgresFixture postgres) : ApiTestBase(postgres, Settings)
{
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["MeetingAgent:Budget:HoursPerSeat"] = "1",
        ["MeetingAgent:Budget:EuroPerHour"] = "1",
        ["MeetingAgent:Budget:DailyCeilingEuro"] = "100",
        ["MeetingAgent:Transcription:EuroPerAudioMinute"] = "3",
        ["MeetingAgent:Extraction:EuroPerMillionInputTokens"] = "10",
        ["MeetingAgent:Extraction:EuroPerMillionOutputTokens"] = "20",
    };

    private readonly TestClock _clock = new();
    private readonly RecordingEmailSender _email = new();
    private WebApplicationFactory<Program> _app = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _app = WithFakes(Factory);
    }

    public override async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Audio_and_tokens_are_recorded_per_workspace_per_month()
    {
        _clock.Now = Copenhagen(2031, 3, 10, 9);
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);

        var heard = await AudioAsync(client, ws, meeting);

        heard.Extractor.ShouldBe(ExtractorKind.Model);
        heard.Proposals.ShouldHaveSingleItem();
        var usage = (await SettingsAsync(client, ws)).Usage.ShouldNotBeNull();
        usage.HoursUsed.ShouldBe(0.52);
        usage.HoursIncluded.ShouldBe(1);
        usage.ResetsOn.ShouldBe(new DateOnly(2031, 4, 1));
        usage.Spent.ShouldBeFalse();
        var row = await UsageRowAsync(ws, new DateOnly(2031, 3, 1));
        row.AudioSeconds.ShouldBe(10);
        row.InputTokens.ShouldBe(1000);
        row.OutputTokens.ShouldBe(500);
    }

    [Fact]
    public async Task Concurrent_utterances_in_one_workspace_are_counted_exactly()
    {
        _clock.Now = Copenhagen(2031, 4, 10, 9);
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SayAsync(client, ws, meeting, "We talked about the plan.")));

        var row = await UsageRowAsync(ws, new DateOnly(2031, 4, 1));
        row.InputTokens.ShouldBe(20 * 1000);
        row.OutputTokens.ShouldBe(20 * 500);
        row.CostCents.ShouldBe(40);
    }

    [Fact]
    public async Task Over_budget_audio_is_refused_and_text_falls_back_to_keywords_and_billing_is_warned_once_at_80_and_100()
    {
        _clock.Now = Copenhagen(2031, 5, 10, 9);
        var (client, ws) = await WorkspaceAsync();
        // Two seats (200 cents): the owner and a member with billing access. A guest is neither a seat nor billing.
        var billing = await JoinAsync(client, ws, WorkspaceRole.Member, billing: true);
        var guest = await JoinAsync(client, ws, WorkspaceRole.Guest);
        var meeting = await StartAsync(client, ws);

        for (var i = 0; i < 3; i++)
        {
            await AudioAsync(client, ws, meeting);
        }

        await SayAsync(client, ws, meeting, "Nothing decided.");
        var people = new[] { OwnerEmail(ws), billing.Email, guest.Email };
        _email.To(people).ShouldBeEmpty("158 of 200 cents");
        await SayAsync(client, ws, meeting, "Nothing decided.");
        await SayAsync(client, ws, meeting, "Nothing decided.");
        _email.To(people).Select(m => m.To).ShouldBe([billing.Email, OwnerEmail(ws)], ignoreOrder: true);
        _email.To(people).ShouldAllBe(m => m.Subject.StartsWith("80%"));

        await AudioAsync(client, ws, meeting);
        await ShouldBeProblemAsync(await PostAudioAsync(client, ws, meeting), HttpStatusCode.Conflict, "meeting_budget_spent");
        // Not "done": the audio already proposed that, and a repeat within two minutes is dropped.
        var typed = await SayAsync(client, ws, meeting, "KUP-1 is in review.");

        typed.Extractor.ShouldBe(ExtractorKind.Keyword);
        typed.BudgetSpent.ShouldBeTrue();
        typed.Proposals.ShouldHaveSingleItem().Operation.ShouldBe(ProposalOperation.State);
        (await SettingsAsync(client, ws)).Usage!.Spent.ShouldBeTrue();
        _email.To(people).Count(m => m.Subject.Contains("used up")).ShouldBe(2);
        _email.To(people).Count.ShouldBe(4);
        _email.To(people).ShouldNotContain(m => m.To == guest.Email);
    }

    [Fact]
    public async Task Budget_resets_on_the_first_and_last_months_row_is_kept()
    {
        _clock.Now = Copenhagen(2031, 6, 30, 23);
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);
        await AudioAsync(client, ws, meeting);
        await AudioAsync(client, ws, meeting);
        (await SettingsAsync(client, ws)).Usage!.Spent.ShouldBeTrue();

        // 00:30 on 1 July in Copenhagen is still 30 June in UTC: the month is Copenhagen's.
        _clock.Now = Copenhagen(2031, 7, 1, 0).AddMinutes(30);
        var usage = (await SettingsAsync(client, ws)).Usage!;

        usage.HoursUsed.ShouldBe(0);
        usage.Spent.ShouldBeFalse();
        usage.ResetsOn.ShouldBe(new DateOnly(2031, 8, 1));
        // The second utterance's audio spent the budget, so its text went to keyword matching: no tokens.
        (await UsageRowAsync(ws, new DateOnly(2031, 6, 1))).CostCents.ShouldBe(102);
    }

    [Fact]
    public async Task The_server_wide_daily_ceiling_stops_every_workspace()
    {
        await using var app = WithFakes(Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<MeetingAgentOptions>(o => o.Budget!.DailyCeilingEuro = 1.5m))));
        _clock.Now = Copenhagen(2031, 8, 10, 9);
        var (first, a) = await WorkspaceAsync(app);
        var (second, b) = await WorkspaceAsync(app);
        var inA = await StartAsync(first, a);
        var inB = await StartAsync(second, b);

        await AudioAsync(first, a, inA);
        await AudioAsync(first, a, inA);
        await AudioAsync(second, b, inB);

        // B has used 50 of its 100 cents (its audio reached the ceiling, so its text was keyword-matched), but the
        // server spent 152 of 150 today.
        await ShouldBeProblemAsync(await PostAudioAsync(second, b, inB), HttpStatusCode.Conflict, "meeting_budget_spent");
        (await SayAsync(second, b, inB, "KUP-1 is done.")).BudgetSpent.ShouldBeTrue();
        var usage = (await SettingsAsync(second, b)).Usage!;
        usage.HoursUsed.ShouldBe(0.5);
        usage.Spent.ShouldBeTrue();

        _clock.Now = Copenhagen(2031, 8, 11, 9);
        (await SayAsync(second, b, await StartAsync(second, b), "KUP-1 is done.")).Extractor.ShouldBe(ExtractorKind.Model);
    }

    [Fact]
    public async Task Meetings_end_after_three_hours()
    {
        _clock.Now = Copenhagen(2031, 9, 10, 9);
        var (client, ws) = await WorkspaceAsync();
        var talked = await StartAsync(client, ws);
        var forgotten = await StartAsync(client, ws);

        _clock.Now = _clock.Now.AddHours(3).AddMinutes(1);
        await ShouldBeProblemAsync(await PostSayAsync(client, ws, talked, "KUP-1 is done."), HttpStatusCode.Conflict, "meeting_time_limit");
        await ShouldBeProblemAsync(await PostAudioAsync(client, ws, talked), HttpStatusCode.Conflict, "meeting_not_live");
        (await MeetingAsync(client, ws, talked)).State.ShouldBe(MeetingState.Ended);

        await using (var scope = _app.Services.CreateAsyncScope())
        await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct))
        {
            await MeetingService.EndOverlongMeetingsAsync(db, _clock.Now, Ct);
        }

        var ended = await MeetingAsync(client, ws, forgotten);
        ended.State.ShouldBe(MeetingState.Ended);
        ended.EndedAt.ShouldBe(_clock.Now);
    }

    [Fact]
    public async Task Agents_see_the_fallback_and_the_usage_over_mcp()
    {
        _clock.Now = Copenhagen(2031, 10, 10, 9);
        var (client, ws) = await WorkspaceAsync();
        var meeting = await StartAsync(client, ws);
        await AudioAsync(client, ws, meeting);
        await AudioAsync(client, ws, meeting);
        var token = await NewTokenAsync(client, new(Name: "Budget agent", Kind: Kupro.Api.Features.Tokens.ApiTokenKind.Agent,
            AgentName: "budget-agent", Workspace: ws));
        await using var mcp = await McpAsync(token.Secret);

        var said = await CallAsync(mcp, "meeting", new() { ["action"] = "say", ["text"] = "KUP-1 is done.", ["workspace"] = ws });
        var usage = await CallAsync(mcp, "meeting", new() { ["action"] = "usage", ["workspace"] = ws });

        said.GetProperty("extractor").GetString().ShouldBe("keyword");
        said.GetProperty("budgetSpent").GetBoolean().ShouldBeTrue();
        usage.GetProperty("usage").GetProperty("spent").GetBoolean().ShouldBeTrue();
    }

    // ---------------------------------------------------------------- helpers

    private WebApplicationFactory<Program> WithFakes(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(_clock);
            s.AddSingleton<IEmailSender>(_email);
            s.AddSingleton<IProposalExtractor, MeteredExtractor>();
            s.AddSingleton<ISpeechToText, TenSecondSpeech>();
        }));

    private readonly Dictionary<string, string> _owners = [];

    private string OwnerEmail(string ws) => _owners[ws];

    private async Task<(HttpClient Client, string Workspace)> WorkspaceAsync(WebApplicationFactory<Program>? app = null)
    {
        var (owner, workspace, user) = await NewWorkspaceAsync();
        _owners[workspace.Slug] = user.Email;
        var client = (app ?? _app).CreateClient();
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
        await ReadAsync<WorkspaceSummary>(await Factory.CreateUserClient(user).PostAsync($"/api/v1/invites/{invited.Id}/accept", null, Ct));
        return user;
    }

    private static async Task<MeetingResponse> StartAsync(HttpClient client, string ws) =>
        await ReadAsync<MeetingResponse>(await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/meetings",
            new StartMeetingRequest("KUP"), Json.Options, Ct), HttpStatusCode.Created);

    private static Task<MeetingResponse> MeetingAsync(HttpClient client, string ws, MeetingResponse meeting) =>
        GetAsync<MeetingResponse>(client, $"/api/v1/workspaces/{ws}/meetings/{meeting.Id}");

    private static Task<HttpResponseMessage> PostSayAsync(HttpClient client, string ws, MeetingResponse meeting, string text) =>
        client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/meetings/{meeting.Id}/transcript", new TranscriptRequest(text), Json.Options, Ct);

    private static async Task<TranscriptResult> SayAsync(HttpClient client, string ws, MeetingResponse meeting, string text) =>
        await ReadAsync<TranscriptResult>(await PostSayAsync(client, ws, meeting, text));

    private static Task<HttpResponseMessage> PostAudioAsync(HttpClient client, string ws, MeetingResponse meeting)
    {
        var body = new ByteArrayContent(new byte[64]);
        body.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return client.PostAsync($"/api/v1/workspaces/{ws}/meetings/{meeting.Id}/audio", body, Ct);
    }

    private static async Task<TranscriptResult> AudioAsync(HttpClient client, string ws, MeetingResponse meeting) =>
        await ReadAsync<TranscriptResult>(await PostAudioAsync(client, ws, meeting));

    private static Task<MeetingSettingsResponse> SettingsAsync(HttpClient client, string ws) =>
        GetAsync<MeetingSettingsResponse>(client, $"/api/v1/workspaces/{ws}/meeting-settings");

    private async Task<MeetingUsage> UsageRowAsync(string ws, DateOnly period)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        // The test has no request workspace, so it reads past the tenant filter, matching on the workspace's slug.
        return await db.MeetingUsage.IgnoreQueryFilters([KuproDbContext.TenantFilter]).AsNoTracking()
            .SingleAsync(u => u.Period == period && db.Workspaces.Any(w => w.Id == u.WorkspaceId && w.Slug == ws), Ct);
    }

    private async Task<ModelContextProtocol.Client.McpClient> McpAsync(string token)
    {
        var http = _app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new ModelContextProtocol.Client.HttpClientTransport(new ModelContextProtocol.Client.HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true);
        return await ModelContextProtocol.Client.McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task<System.Text.Json.JsonElement> CallAsync(ModelContextProtocol.Client.McpClient mcp, string tool, Dictionary<string, object?> args)
    {
        var result = await mcp.CallToolAsync(tool, args, cancellationToken: Ct);
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
        result.IsError.ShouldNotBe(true, text);
        return System.Text.Json.JsonDocument.Parse(text).RootElement;
    }

    private static DateTimeOffset Copenhagen(int year, int month, int day, int hour)
    {
        var local = new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Keyword matching for the proposals, with a model's metered usage.</summary>
    private sealed class MeteredExtractor : IProposalExtractor
    {
        private static readonly KeywordProposalExtractor Keywords = new();

        public async Task<Extraction> ExtractAsync(ExtractionContext context, CancellationToken ct) =>
            new((await Keywords.ExtractAsync(context, ct)).Actions, new ModelUsage(1000, 0, 500));
    }

    private sealed class TenSecondSpeech : ISpeechToText
    {
        public bool Supports(string language) => true;

        public Task<Transcription> TranscribeAsync(Stream wav, string language, CancellationToken ct) =>
            Task.FromResult(new Transcription("KUP-1 is done.", 10));
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> _sent = new();

        /// <summary>Budget emails sent to these people (each test's users are new); invites go through here too.</summary>
        public List<EmailMessage> To(IReadOnlyCollection<string> people) =>
            [.. _sent.Where(m => people.Contains(m.To) && m.Subject.Contains("meeting hours", StringComparison.Ordinal))];

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            _sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
