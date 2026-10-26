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

    private static readonly ExtractionContext Context = new("en", MeetingType.Standup,
        [new IssueHint("KUP-15", "Dark mode for the board", "backlog", null)], ["Sofie Larsen"], ["Sprint 15"], [], "Dark mode is two days.");

    [Fact]
    public async Task Request_caps_output_and_names_the_meeting_type()
    {
        var model = new FakeModel("""{"actions":[]}""");

        await Extractor(model).ExtractAsync(Context, Ct);

        model.Request!["max_tokens"]!.GetValue<int>().ShouldBeGreaterThan(0);
        model.Request.AsObject().ContainsKey("reasoning_effort").ShouldBeFalse();
        var context = JsonNode.Parse(model.Request["messages"]![1]!["content"]!.GetValue<string>())!;
        context["type"]!.GetValue<string>().ShouldBe("standup");
    }

    [Fact]
    public async Task Configured_reasoning_effort_is_sent_and_a_language_route_inherits_it()
    {
        var model = new FakeModel("""{"actions":[]}""");
        var route = new ModelRoute
        {
            Endpoint = "https://llm.test/v1",
            Model = "test-model",
            ApiKey = "test",
            ReasoningEffort = "low",
            Languages = { ["en"] = new ModelRoute { Model = "english-model" } },
        };

        await new OpenAiProposalExtractor(new HttpClient(model), Options.Create(new MeetingAgentOptions { Extraction = route }))
            .ExtractAsync(Context, Ct);

        model.Request!["model"]!.GetValue<string>().ShouldBe("english-model");
        model.Request["reasoning_effort"]!.GetValue<string>().ShouldBe("low");
    }

    [Fact]
    public async Task Output_cut_off_at_the_token_cap_gives_no_proposals()
    {
        var model = new FakeModel("""{"actions":[{"confidence":1,"issue":"KUP-15","operation":"estimate","snippet":"two days","value":     """);

        (await Extractor(model).ExtractAsync(Context, Ct)).Actions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Quoted_values_are_unwrapped()
    {
        var model = new FakeModel("""{"actions":[{"confidence":0.9,"issue":"KUP-15","operation":"state","snippet":"done","value":"\"done\""}]}""");

        var action = (await Extractor(model).ExtractAsync(Context, Ct)).Actions.ShouldHaveSingleItem();

        action.Operation.ShouldBe(ProposalOperation.State);
        action.Value.ShouldBe("done");
    }

    private static OpenAiProposalExtractor Extractor(FakeModel model) =>
        new(new HttpClient(model), Options.Create(new MeetingAgentOptions
        {
            Extraction = new ModelRoute { Endpoint = "https://llm.test/v1", Model = "test-model", ApiKey = "test" },
        }));

    [Fact]
    public async Task Token_usage_comes_from_the_response_including_cached_input()
    {
        var model = new FakeModel("""{"actions":[]}""",
            JsonNode.Parse("""{"prompt_tokens":1969,"completion_tokens":257,"prompt_tokens_details":{"cached_tokens":750}}"""));

        var usage = (await Extractor(model).ExtractAsync(Context, Ct)).Usage.ShouldNotBeNull();

        usage.ShouldBe(new ModelUsage(1969, 750, 257));
    }

    [Fact]
    public async Task Without_usage_in_the_response_tokens_are_estimated_from_the_text()
    {
        var model = new FakeModel("""{"actions":[]}""");

        var usage = (await Extractor(model).ExtractAsync(Context, Ct)).Usage.ShouldNotBeNull();

        usage.InputTokens.ShouldBeGreaterThan(100);
        usage.OutputTokens.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Audio_length_is_read_from_the_wav_header()
    {
        // 16 kHz mono 16-bit, the browser's format: 32,000 bytes a second. 1.5 s of silence.
        var samples = 24_000;
        var wav = new byte[44 + samples * 2];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + samples * 2).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(16_000).CopyTo(wav, 24);
        BitConverter.GetBytes(32_000).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(samples * 2).CopyTo(wav, 40);

        Wav.Seconds(wav).ShouldBe(1.5);
        Wav.Seconds(new byte[64_000]).ShouldBe(2, "no header: the browser's byte rate");
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
