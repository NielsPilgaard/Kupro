using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kupro.Api.Features.Meetings;
using Microsoft.Extensions.Options;

namespace Kupro.Tests.Features;

/// <summary>
/// The LLM extractor against a canned chat completion. Failure modes found in the ADR-015 extraction model spike: a
/// model padding the JSON schema with whitespace until the timeout, and values wrapped in quotes.
/// </summary>
public sealed class ProposalExtractorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ExtractionContext Context = new("en", MeetingType.Standup, [], "Dark mode is two days.",
        [new IssueHint("FLOW-15", "Dark mode for the board", "backlog", null)], ["Sofie Larsen"], ["Sprint 15"]);

    [Fact]
    public async Task Request_caps_output_and_names_the_meeting_type()
    {
        var model = new FakeModel("""{"actions":[]}""");

        await Extractor(model).ExtractAsync(Context, Ct);

        model.Request!["max_tokens"]!.GetValue<int>().ShouldBeGreaterThan(0);
        var context = JsonNode.Parse(model.Request["messages"]![1]!["content"]!.GetValue<string>())!;
        context["type"]!.GetValue<string>().ShouldBe("standup");
    }

    [Fact]
    public async Task Output_cut_off_at_the_token_cap_gives_no_proposals()
    {
        var model = new FakeModel("""{"actions":[{"confidence":1,"issue":"FLOW-15","operation":"estimate","snippet":"two days","value":     """);

        (await Extractor(model).ExtractAsync(Context, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Quoted_values_are_unwrapped()
    {
        var model = new FakeModel("""{"actions":[{"confidence":0.9,"issue":"FLOW-15","operation":"state","snippet":"done","value":"\"done\""}]}""");

        var action = (await Extractor(model).ExtractAsync(Context, Ct)).ShouldHaveSingleItem();

        action.Operation.ShouldBe(ProposalOperation.State);
        action.Value.ShouldBe("done");
    }

    private static OpenAiProposalExtractor Extractor(FakeModel model) =>
        new(new HttpClient(model), Options.Create(new MeetingAgentOptions
        {
            Extraction = new ModelRoute { Endpoint = "https://llm.test/v1", Model = "test-model", ApiKey = "test" },
        }));

    private sealed class FakeModel(string content) : HttpMessageHandler
    {
        public JsonNode? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var completion = new JsonObject
            {
                ["choices"] = new JsonArray { new JsonObject { ["message"] = new JsonObject { ["content"] = content } } },
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(completion.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
