using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Kupro.Api.Data;
using Kupro.Api.Features.Ai;
using Kupro.Api.Features.Email;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Users;
using Kupro.Api.Features.Voice;
using Kupro.Cli;
using Kupro.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CliApp = Kupro.Cli.Cli;

namespace Kupro.Tests.Features;

/// <summary>
/// Task 036: voice control. The fake speech seam hears whatever <see cref="RecordingSpeech.Next"/> says (10 s of audio,
/// 50 cents on Kupro's key); the fake interpreter reads with the keyword rules but reports a model's tokens. A one-seat
/// workspace has 100 cents of AI budget, shared with meetings, so two utterances spend it. Keys starting "revoked" are
/// refused by the vendor.
/// </summary>
[Collection(nameof(StorageTests))]
public sealed class VoiceTests(PostgresFixture postgres, StorageFixture storage)
    : ApiTestBase(postgres, Settings(storage)), IClassFixture<StorageFixture>
{
    private const string KuproKey = "kupro-key";

    /// <summary>Only the key ring: with snapshot storage too, this API's import job would fail other classes' import runs.</summary>
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
    private readonly FakeInterpreter _interpreter = new();
    private readonly RecordingEmail _email = new();
    private WebApplicationFactory<Program> _app = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _app = Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ISpeechToText>(_speech);
            s.AddSingleton<IVoiceInterpreter>(_interpreter);
            s.AddSingleton<IAiModels, FakeModels>();
            s.AddSingleton<IEmailSender>(_email);
        }));
    }

    public override async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Move_to_next_sprint_shows_the_diff_changes_nothing_and_applies_and_undoes_through_the_issue_api()
    {
        var w = await WorkspaceAsync();

        var reading = await InterpretAsync(w, new VoiceInterpretRequest("move kup 1 to next sprint"));

        reading.Kind.ShouldBe(VoiceKind.Operations);
        reading.Extractor.ShouldBe(ExtractorKind.Model);
        reading.Text.ShouldBe("move KUP-1 to next sprint");
        var op = reading.Operations.ShouldHaveSingleItem();
        op.Operation.ShouldBe(ProposalOperation.Sprint);
        op.Issue!.Key.ShouldBe("KUP-1");
        op.Value.ShouldBe(w.Next.ToString());
        op.ValueDisplay.ShouldBe("Sprint 2");
        op.Before.ShouldBe("none");
        op.Held.ShouldBeEmpty();
        (await IssueAsync(w, "KUP-1")).SprintId.ShouldBeNull("interpreting changes nothing");

        (await PatchAsync(w, "KUP-1", op.Update!)).SprintId.ShouldBe(w.Next);
        (await PatchAsync(w, "KUP-1", op.Undo!)).SprintId.ShouldBeNull();
    }

    [Fact]
    public async Task This_one_is_the_issue_on_screen_in_english_and_danish()
    {
        var w = await WorkspaceAsync();

        var english = await InterpretAsync(w, new VoiceInterpretRequest("this is done", Issue: "KUP-2"));
        var danish = await InterpretAsync(w, new VoiceInterpretRequest("giv den her til Test", Issue: "KUP-2", Language: "da"));

        var done = english.Operations.ShouldHaveSingleItem();
        done.Issue!.Key.ShouldBe("KUP-2");
        done.Update!.State.ShouldBe(IssueState.Done);
        var assign = danish.Operations.ShouldHaveSingleItem();
        assign.Operation.ShouldBe(ProposalOperation.Assignee);
        assign.ValueDisplay.ShouldBe("Test User");
        _interpreter.Languages.ShouldContain("da");
    }

    [Fact]
    public async Task Changes_that_must_wait_say_why()
    {
        var w = await WorkspaceAsync();

        var cancel = (await InterpretAsync(w, new VoiceInterpretRequest("cancel KUP-1"))).Operations.ShouldHaveSingleItem();
        var create = (await InterpretAsync(w, new VoiceInterpretRequest("create an issue for dark mode", Project: "KUP"))).Operations.ShouldHaveSingleItem();
        var vague = (await InterpretAsync(w, new VoiceInterpretRequest("the login is done"))).Operations.ShouldHaveSingleItem();

        cancel.Held.ShouldBe([VoiceHold.Destructive]);
        create.Held.ShouldBe([VoiceHold.CreateIssue]);
        create.Create.ShouldBe(new CreateIssueRequest("KUP", "dark mode", State: IssueState.Todo));
        vague.NeedsIssue.ShouldBeTrue();
        vague.Candidates.Select(c => c.Key).Order().ShouldBe(["KUP-3", "KUP-4"]);
        vague.Held.ShouldContain(VoiceHold.Ambiguous);
        vague.Held.ShouldContain(VoiceHold.LowConfidence);
    }

    [Fact]
    public async Task Navigation_is_a_checked_target_in_this_workspace_only()
    {
        var w = await WorkspaceAsync();
        var other = await WorkspaceAsync("OTH");

        var open = await InterpretAsync(w, new VoiceInterpretRequest("åbn KUP-1", Language: "da"));
        var sprint = await InterpretAsync(w, new VoiceInterpretRequest("show next sprint", Project: "KUP"));
        var board = await InterpretAsync(w, new VoiceInterpretRequest("board for project Kupro"));
        var filtered = await InterpretAsync(w, new VoiceInterpretRequest("filter by Test", Project: "KUP"));
        var elsewhere = await InterpretAsync(other, new VoiceInterpretRequest("open KUP-1"));
        var otherBoard = await InterpretAsync(other, new VoiceInterpretRequest("board for project Kupro"));
        var missing = await InterpretAsync(w, new VoiceInterpretRequest("open KUP-999"));

        open.Navigate.ShouldBe(new VoiceTarget(VoiceView.Issue, "KUP-1", null, null, null, null));
        sprint.Navigate!.View.ShouldBe(VoiceView.Sprints);
        sprint.Navigate.Sprint.ShouldBe(w.Next);
        board.Navigate.ShouldBe(new VoiceTarget(VoiceView.Board, null, "KUP", null, null, null));
        filtered.Navigate!.Assignee!.Name.ShouldBe("Test User");
        elsewhere.Kind.ShouldBe(VoiceKind.None);
        elsewhere.Reason.ShouldBe(VoiceReason.NotFound);
        otherBoard.Reason.ShouldBe(VoiceReason.NotFound);
        missing.Reason.ShouldBe(VoiceReason.NotFound);
    }

    [Fact]
    public async Task Audio_draws_on_the_shared_budget_and_stops_at_it_while_text_falls_back_to_keywords()
    {
        var w = await WorkspaceAsync();
        _speech.Next = "move KUP-1 to next sprint";

        (await AudioAsync(w)).Operations.ShouldHaveSingleItem();
        (await AudioAsync(w)).Operations.ShouldHaveSingleItem();
        await ShouldBeProblemAsync(await PostAudioAsync(w, "interpret/audio"), HttpStatusCode.Conflict, "ai_budget_spent");
        await ShouldBeProblemAsync(await PostAudioAsync(w, "transcribe"), HttpStatusCode.Conflict, "ai_budget_spent");
        var calls = _interpreter.Keys.Count;
        var typed = await InterpretAsync(w, new VoiceInterpretRequest("move KUP-1 to next sprint"));

        typed.Extractor.ShouldBe(ExtractorKind.Keyword);
        typed.BudgetSpent.ShouldBeTrue();
        typed.Operations.ShouldHaveSingleItem();
        _interpreter.Keys.Count.ShouldBe(calls, "no model call once the budget is spent");
        (await GetAsync<MeetingSettingsResponse>(w.Client, $"/api/v1/workspaces/{w.Slug}/meeting-settings")).Usage!.Spent
            .ShouldBeTrue("meetings and voice share one budget");
        var rows = await UsageAsync(w.Slug);
        rows.Single(r => r.Feature == AiFeature.Voice).AudioSeconds.ShouldBe(20);
        rows.Single(r => r.Feature == AiFeature.Meetings).CostCents.ShouldBe(0, "the meetings row only carries the warnings");
    }

    [Fact]
    public async Task Voice_spending_the_budget_warns_billing_once_although_the_month_has_two_usage_rows()
    {
        // Regression: with a meetings row and a voice row, the warning claim matched both rows and no email went out.
        var w = await WorkspaceAsync();
        _speech.Next = "open KUP-1";

        await AudioAsync(w);
        _email.BudgetWarnings().ShouldBeEmpty("50 of 100 cents");
        await AudioAsync(w);
        await ShouldBeProblemAsync(await PostAudioAsync(w, "interpret/audio"), HttpStatusCode.Conflict, "ai_budget_spent");

        _email.BudgetWarnings().ShouldHaveSingleItem().Subject.ShouldContain("used up");
    }

    [Fact]
    public async Task An_own_key_has_no_limit_and_a_refused_one_stops_the_microphone_without_borrowing_kupros_key()
    {
        var w = await WorkspaceAsync();
        _speech.Next = "open KUP-1";
        await AddConnectionAsync(w, "scw-voice-0001");

        for (var i = 0; i < 4; i++)
        {
            (await AudioAsync(w)).Kind.ShouldBe(VoiceKind.Navigate);
        }

        _speech.Keys.Count(k => k == "scw-voice-0001").ShouldBe(4);
        (await UsageAsync(w.Slug)).Single(r => r.PaidBy == AiPaidBy.Workspace && r.Feature == AiFeature.Voice).AudioSeconds.ShouldBe(40);

        var v = await WorkspaceAsync();
        await AddConnectionAsync(v, "revoked-voice-0002");
        var kupro = _speech.Keys.Count(k => k == KuproKey) + _interpreter.Keys.Count(k => k == KuproKey);

        await ShouldBeProblemAsync(await PostAudioAsync(v, "interpret/audio"), HttpStatusCode.Conflict, "ai_connection_rejected");
        await ShouldBeProblemAsync(await PostAudioAsync(v, "transcribe"), HttpStatusCode.Conflict, "ai_connection_rejected");
        var typed = await InterpretAsync(v, new VoiceInterpretRequest("KUP-1 is done"));

        typed.Extractor.ShouldBe(ExtractorKind.Keyword);
        typed.Operations.ShouldHaveSingleItem();
        (_speech.Keys.Count(k => k == KuproKey) + _interpreter.Keys.Count(k => k == KuproKey)).ShouldBe(kupro);
    }

    [Fact]
    public async Task Dictation_inserts_the_words_as_said_and_tidy_up_only_fixes_spelling_and_punctuation()
    {
        var w = await WorkspaceAsync();
        // Three utterances: more than Kupro's budget here, so on the workspace's own key.
        await AddConnectionAsync(w, "scw-dictation-0003");
        _speech.Next = "the login page brakes on safari";

        _interpreter.Tidied = "The login page breaks on Safari.";
        var tidy = await TranscribeAsync(w, tidy: true);
        _interpreter.Tidied = "The sign-in screen fails in Safari.";
        var rewritten = await TranscribeAsync(w, tidy: true);
        var plain = await TranscribeAsync(w, tidy: false);

        tidy.ShouldBe(new VoiceTranscription("the login page brakes on safari", "The login page breaks on Safari."));
        rewritten.Tidied.ShouldBeNull("a suggestion that changes words is dropped");
        plain.Tidied.ShouldBeNull();
    }

    [Fact]
    public async Task Nothing_about_an_utterance_outlives_the_request()
    {
        var w = await WorkspaceAsync();
        _speech.Next = "comment on KUP-1: secret launch date";
        var before = await CountsAsync(w.Slug);

        (await AudioAsync(w)).Operations.ShouldHaveSingleItem().Value.ShouldBe("secret launch date");
        await TranscribeAsync(w, tidy: false);

        (await CountsAsync(w.Slug)).ShouldBe(before, "no transcript, proposal, comment or activity is stored");
    }

    [Fact]
    public async Task The_voice_button_is_a_personal_preference_set_over_the_api_and_the_cli()
    {
        var w = await WorkspaceAsync();
        var me = await GetAsync<MeResponse>(w.Client, "/api/v1/me");
        me.VoiceButton.ShouldBe(VoiceButton.TopBar);
        me.VoiceAutoApply.ShouldBeFalse();

        var thumb = await ReadAsync<MeResponse>(await w.Client.PatchAsJsonAsync("/api/v1/me",
            new UpdateMeRequest(VoiceButton: VoiceButton.Thumb, VoiceAutoApply: true), Json.Options, Ct));
        thumb.VoiceButton.ShouldBe(VoiceButton.Thumb);
        thumb.VoiceAutoApply.ShouldBeTrue();
        thumb.Locale.ShouldBe(me.Locale, "other fields are left alone");

        var token = await NewTokenAsync(w.Client, new Kupro.Api.Features.Tokens.CreateApiTokenRequest("cli", Kupro.Api.Features.Tokens.ApiTokenKind.Personal));
        var (exit, _, error) = await CliAsync(token.Secret, w.Slug, null, "me", "update", "--voice-button", "top-bar");
        exit.ShouldBe(0, error);
        (await GetAsync<MeResponse>(w.Client, "/api/v1/me")).VoiceButton.ShouldBe(VoiceButton.TopBar);
    }

    [Fact]
    public async Task Kupro_say_shows_the_change_asks_and_applies_it()
    {
        var w = await WorkspaceAsync();
        var token = await NewTokenAsync(w.Client, new Kupro.Api.Features.Tokens.CreateApiTokenRequest("cli", Kupro.Api.Features.Tokens.ApiTokenKind.Personal));

        var (declined, shown, _) = await CliAsync(token.Secret, w.Slug, new StringReader("n\n"), "say", "move", "KUP-1", "to", "next", "sprint");
        declined.ShouldBe(0);
        shown.ShouldContain("Sprint 2");
        shown.ShouldContain("Nothing changed.");
        (await IssueAsync(w, "KUP-1")).SprintId.ShouldBeNull();

        var (exit, output, error) = await CliAsync(token.Secret, w.Slug, new StringReader("y\n"), "say", "move KUP-1 to next sprint");
        exit.ShouldBe(0, error);
        output.ShouldContain("Applied.");
        (await IssueAsync(w, "KUP-1")).SprintId.ShouldBe(w.Next);

        var (yes, _, yesError) = await CliAsync(token.Secret, w.Slug, null, "say", "--yes", "comment on KUP-1: shipped behind a flag");
        yes.ShouldBe(0, yesError);
        var feed = await w.Client.GetStringAsync($"/api/v1/workspaces/{w.Slug}/issues/KUP-1/activity", Ct);
        feed.ShouldContain("shipped behind a flag");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Ws(HttpClient Client, string Slug, Guid Next);

    private async Task<Ws> WorkspaceAsync(string key = "KUP")
    {
        var (owner, workspace, _) = await NewWorkspaceAsync();
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;
        var url = $"/api/v1/workspaces/{workspace.Slug}";
        await ReadAsync<ProjectResponse>(await client.PostAsJsonAsync($"{url}/projects", new CreateProjectRequest(key, "Kupro" + (key == "KUP" ? "" : " other")), Json.Options, Ct),
            HttpStatusCode.Created);
        foreach (var title in new[] { "Render delivery plan", "Dark mode for the board", "Login fails on Safari", "Login button is blue" })
        {
            await ReadAsync<IssueDetail>(await client.PostAsJsonAsync($"{url}/issues", new CreateIssueRequest(key, title, State: IssueState.Todo), Json.Options, Ct),
                HttpStatusCode.Created);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var current = await ReadAsync<SprintResponse>(await client.PostAsJsonAsync($"{url}/sprints",
            new CreateSprintRequest(key, "Sprint 1", today, today.AddDays(13)), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<SprintResponse>(await client.PostAsync($"{url}/sprints/{current.Id}/start", null, Ct));
        var next = await ReadAsync<SprintResponse>(await client.PostAsJsonAsync($"{url}/sprints",
            new CreateSprintRequest(key, "Sprint 2", today.AddDays(14), today.AddDays(27)), Json.Options, Ct), HttpStatusCode.Created);
        return new Ws(client, workspace.Slug, next.Id);
    }

    private static async Task<VoiceInterpretation> InterpretAsync(Ws w, VoiceInterpretRequest request) =>
        await ReadAsync<VoiceInterpretation>(await w.Client.PostAsJsonAsync($"/api/v1/workspaces/{w.Slug}/voice/interpret", request, Json.Options, Ct));

    private static Task<HttpResponseMessage> PostAudioAsync(Ws w, string path, string query = "")
    {
        var body = new ByteArrayContent(new byte[64]);
        body.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return w.Client.PostAsync($"/api/v1/workspaces/{w.Slug}/voice/{path}{query}", body, Ct);
    }

    private static async Task<VoiceInterpretation> AudioAsync(Ws w) => await ReadAsync<VoiceInterpretation>(await PostAudioAsync(w, "interpret/audio"));

    private static async Task<VoiceTranscription> TranscribeAsync(Ws w, bool tidy) =>
        await ReadAsync<VoiceTranscription>(await PostAudioAsync(w, "transcribe", tidy ? "?tidy=true" : ""));

    private static Task<IssueDetail> IssueAsync(Ws w, string key) => GetAsync<IssueDetail>(w.Client, $"/api/v1/workspaces/{w.Slug}/issues/{key}");

    private static async Task<IssueDetail> PatchAsync(Ws w, string key, UpdateIssueRequest change) =>
        await ReadAsync<IssueDetail>(await w.Client.PatchAsJsonAsync($"/api/v1/workspaces/{w.Slug}/issues/{key}", change, Json.Options, Ct));

    private static async Task AddConnectionAsync(Ws w, string key) =>
        await ReadAsync<AiConnectionResponse>(await w.Client.PostAsJsonAsync($"/api/v1/workspaces/{w.Slug}/ai/connections",
            new AddAiConnectionRequest(AiProviders.ScalewayId, key, UseFor: [AiCapability.Transcription, AiCapability.VoiceCommands]), Json.Options, Ct),
            HttpStatusCode.Created);

    private async Task<List<MeetingUsage>> UsageAsync(string slug)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        // No request workspace in a test: read past the tenant filter, matching on the slug.
        return await db.MeetingUsage.IgnoreQueryFilters([KuproDbContext.TenantFilter]).AsNoTracking()
            .Where(u => db.Workspaces.Any(x => x.Id == u.WorkspaceId && x.Slug == slug)).ToListAsync(Ct);
    }

    private async Task<(int Segments, int Proposals, int Comments, int Activity)> CountsAsync(string slug)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        // No request workspace in a test: read past the tenant filter, matching on the slug.
        var id = await db.Workspaces.Where(x => x.Slug == slug).Select(x => x.Id).SingleAsync(Ct);
        return (
            await db.TranscriptSegments.IgnoreQueryFilters([KuproDbContext.TenantFilter]).CountAsync(t => t.WorkspaceId == id, Ct),
            await db.MeetingProposals.IgnoreQueryFilters([KuproDbContext.TenantFilter]).CountAsync(p => p.WorkspaceId == id, Ct),
            await db.Comments.IgnoreQueryFilters([KuproDbContext.TenantFilter]).CountAsync(c => c.WorkspaceId == id, Ct),
            await db.Activity.IgnoreQueryFilters([KuproDbContext.TenantFilter]).CountAsync(a => a.WorkspaceId == id, Ct));
    }

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

    private sealed class RecordingEmail : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> _sent = new();

        public List<EmailMessage> BudgetWarnings() => [.. _sent.Where(m => m.Subject.Contains("meeting hours", StringComparison.Ordinal))];

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            _sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSpeech : ISpeechToText
    {
        private readonly ConcurrentQueue<string> _keys = new();

        public string Next { get; set; } = "";

        public List<string> Keys => [.. _keys];

        public Task<Transcription> TranscribeAsync(Stream wav, string language, ModelRoute route, CancellationToken ct)
        {
            if (route.ApiKey!.StartsWith("revoked", StringComparison.Ordinal))
            {
                throw Refused();
            }

            _keys.Enqueue(route.ApiKey);
            return Task.FromResult(new Transcription(Next, 10));
        }
    }

    /// <summary>The keyword rules with a model's metered usage, recording which key and language each call used.</summary>
    private sealed class FakeInterpreter : IVoiceInterpreter
    {
        private static readonly KeywordVoiceInterpreter Keywords = new();
        private readonly ConcurrentQueue<(string Key, string Language)> _calls = new();

        public string Tidied { get; set; } = "";

        public List<string> Keys => [.. _calls.Select(c => c.Key)];
        public List<string> Languages => [.. _calls.Select(c => c.Language)];

        public async Task<VoiceReading> InterpretAsync(VoiceContext context, ModelRoute route, CancellationToken ct)
        {
            Record(route, context.Language);
            return (await Keywords.InterpretAsync(context, ct)) with { Usage = new ModelUsage(800, 0, 60) };
        }

        public Task<TidyResult> TidyAsync(string text, string language, ModelRoute route, CancellationToken ct)
        {
            Record(route, language);
            return Task.FromResult(new TidyResult(Tidied, new ModelUsage(100, 0, 20)));
        }

        private void Record(ModelRoute route, string language)
        {
            if (route.ApiKey!.StartsWith("revoked", StringComparison.Ordinal))
            {
                throw Refused();
            }

            _calls.Enqueue((route.ApiKey, language));
        }
    }

    private sealed class FakeModels : IAiModels
    {
        public Task<IReadOnlyList<string>> ListAsync(string baseUrl, string key, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["whisper-large-v3", "deepseek-v4.1-flash"]);
    }
}
