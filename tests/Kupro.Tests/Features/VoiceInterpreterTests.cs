using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Voice;

namespace Kupro.Tests.Features;

/// <summary>
/// Task 036 without a database: the offline reader for clear English and Danish commands, the model's answer parsed into
/// changes and places, and the guard that keeps "tidy up" to spelling and punctuation.
/// </summary>
public sealed class VoiceInterpreterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VoiceContext Context(string utterance, string? current = null, string language = "en") => new(
        language, ["KUP: Kupro"], ["Anna Holm"], ["Sprint 15"], [new IssueHint("KUP-12", "Login fails on Safari", "todo", null)],
        "KUP", current is null ? null : new IssueHint(current, "On screen", "todo", null), utterance);

    [Theory]
    [InlineData("open KUP-12", VoiceView.Issue, "KUP-12", "", "", "")]
    [InlineData("åbn KUP-12", VoiceView.Issue, "KUP-12", "", "", "")]
    [InlineData("show my issues", VoiceView.MyIssues, "", "", "", "")]
    [InlineData("vis mine opgaver", VoiceView.MyIssues, "", "", "", "")]
    [InlineData("show next sprint", VoiceView.Sprints, "", "", "", "next")]
    [InlineData("vis næste sprint", VoiceView.Sprints, "", "", "", "next")]
    [InlineData("board for project Kupro", VoiceView.Board, "", "Kupro", "", "")]
    [InlineData("tavlen for KUP", VoiceView.Board, "", "KUP", "", "")]
    [InlineData("filter by Anna", VoiceView.List, "", "", "Anna", "")]
    [InlineData("filtrer på Anna", VoiceView.List, "", "", "Anna", "")]
    [InlineData("show the plan", VoiceView.Plan, "", "", "", "")]
    public async Task Clear_places_are_read_without_a_model(string said, VoiceView view, string issue, string project, string assignee, string sprint)
    {
        var reading = await new KeywordVoiceInterpreter().InterpretAsync(Context(said), Ct);

        reading.Kind.ShouldBe(VoiceKind.Navigate);
        reading.Navigate.ShouldBe(new HeardTarget(view, issue, project, assignee, sprint));
        reading.Usage.ShouldBeNull();
    }

    [Theory]
    [InlineData("move this one to next sprint", "en", ProposalOperation.Sprint)]
    [InlineData("this is done", "en", ProposalOperation.State)]
    [InlineData("den her er færdig", "da", ProposalOperation.State)]
    [InlineData("giv den her til Anna", "da", ProposalOperation.Assignee)]
    public async Task This_one_is_the_issue_on_screen(string said, string language, ProposalOperation operation)
    {
        var reading = await new KeywordVoiceInterpreter().InterpretAsync(Context(said, current: "KUP-7", language), Ct);

        reading.Kind.ShouldBe(VoiceKind.Operations);
        var action = reading.Actions.ShouldHaveSingleItem();
        action.Operation.ShouldBe(operation);
        action.IssueReference.ShouldBe("KUP-7");
    }

    [Fact]
    public async Task This_sprint_is_not_the_issue_on_screen()
    {
        var reading = await new KeywordVoiceInterpreter().InterpretAsync(Context("move KUP-12 to this sprint", current: "KUP-7"), Ct);

        var action = reading.Actions.ShouldHaveSingleItem();
        action.IssueReference.ShouldBe("KUP-12");
        action.Value.ShouldBe("current");
    }

    [Fact]
    public async Task Chat_is_nothing()
    {
        var reading = await new KeywordVoiceInterpreter().InterpretAsync(Context("what a lovely day"), Ct);

        reading.Kind.ShouldBe(VoiceKind.None);
    }

    [Fact]
    public async Task The_models_answer_becomes_changes_or_a_place()
    {
        var model = new FakeModel("""
            {"kind":"navigate","actions":[],"navigate":{"view":"board","issue":"","project":"KUP","assignee":"Anna","sprint":""}}
            """, JsonNode.Parse("""{"prompt_tokens":900,"completion_tokens":40}"""));

        var reading = await new OpenAiVoiceInterpreter(new OneClient(model)).InterpretAsync(Context("Annas tavle"), Route, Ct);

        reading.Kind.ShouldBe(VoiceKind.Navigate);
        reading.Navigate.ShouldBe(new HeardTarget(VoiceView.Board, "", "KUP", "Anna", ""));
        reading.Usage.ShouldBe(new ModelUsage(900, 0, 40));
        var sent = JsonNode.Parse(model.Request!["messages"]![1]!["content"]!.GetValue<string>())!;
        sent["project"]!.GetValue<string>().ShouldBe("KUP");
        sent["utterance"]!.GetValue<string>().ShouldBe("Annas tavle");
        model.Request["response_format"]!["json_schema"]!["schema"]!["properties"]!["navigate"].ShouldNotBeNull();
    }

    [Fact]
    public async Task A_cut_off_answer_is_nothing_rather_than_an_error()
    {
        var model = new FakeModel("""{"kind":"operations","actions":[{"operation":"sta""");

        var reading = await new OpenAiVoiceInterpreter(new OneClient(model)).InterpretAsync(Context("move KUP-12"), Route, Ct);

        reading.Kind.ShouldBe(VoiceKind.None);
        reading.Usage.ShouldNotBeNull("the call still costs tokens");
    }

    [Theory]
    [InlineData("the login page brakes on safari", "The login page breaks on Safari.", true)]
    [InlineData("login siden virker ikk på safari", "Login-siden virker ikke på Safari.", true)]
    [InlineData("we shud fix it tomorow", "We should fix it tomorrow.", true)]
    [InlineData("the login page brakes on safari", "The sign-in screen fails in Safari.", false)]
    [InlineData("fix it now", "Fix it now, please.", false)]
    [InlineData("fix it now please", "Fix it now.", false)]
    [InlineData("Anna takes KUP-12", "KUP-12 is taken by Anna.", false)]
    public void Tidy_up_may_fix_spelling_and_punctuation_and_nothing_else(string said, string tidied, bool allowed) =>
        VoiceService.OnlySpellingAndPunctuation(said, tidied).ShouldBe(allowed);

    private static readonly ModelRoute Route = new() { Endpoint = "https://llm.test/v1", Model = "test-model", ApiKey = "test" };

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeModel(string content, JsonNode? usage = null) : HttpMessageHandler
    {
        public JsonNode? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var completion = new JsonObject
            {
                ["choices"] = new JsonArray { new JsonObject { ["message"] = new JsonObject { ["content"] = content } } },
                ["usage"] = usage,
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(completion.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
