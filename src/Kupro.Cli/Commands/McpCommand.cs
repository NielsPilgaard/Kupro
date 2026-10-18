using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kupro.Cli.Commands;

/// <summary>
/// <c>kupro mcp</c>: a local stdio MCP server that forwards every JSON-RPC message to the remote <c>/mcp</c>
/// endpoint with the CLI's credentials (ADR-001). For MCP clients that can't do remote OAuth, or to reuse a CLI login.
/// </summary>
internal static class McpCommand
{
    private const int NotSignedIn = -32001;

    public static Command Build() =>
        new Command("mcp", "Serve Kupro's MCP tools over stdio, using this CLI's login or token.").Handle(async (c, ct) =>
        {
            using var http = c.Session.CreateHttpClient();
            var endpoint = new Uri(c.Session.Config.EffectiveUrl + "/mcp");
            var input = Cli.Runtime.Value?.In ?? Console.In;
            var output = c.Out.Writer;
            var writeLock = new SemaphoreSlim(1, 1);
            var inFlight = new List<Task>();

            while (await input.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Requests run concurrently (clients pipeline them); writes are serialized, one message per line.
                inFlight.Add(ForwardAsync(c.Session, http, endpoint, line, output, writeLock, ct));
                inFlight.RemoveAll(t => t.IsCompleted);
            }

            await Task.WhenAll(inFlight);
        });

    private static async Task ForwardAsync(
        ApiSession session, HttpClient http, Uri endpoint, string message, TextWriter output, SemaphoreSlim writeLock,
        CancellationToken ct)
    {
        var replies = new List<string>();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(message, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (await session.GetAccessTokenAsync(ct) is { } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                replies.Add(Error(message, NotSignedIn, "Not signed in to Kupro. Run `kupro login` or `kupro token set <token>`."));
            }
            else if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                while (await reader.ReadLineAsync(ct) is { } sse)
                {
                    if (sse.StartsWith("data:", StringComparison.Ordinal))
                    {
                        replies.Add(sse[5..].Trim());
                    }
                }
            }
            else if (response.Content.Headers.ContentLength != 0)
            {
                var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
                if (body.Length > 0)
                {
                    replies.Add(body.ReplaceLineEndings(" "));
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or CliException)
        {
            replies.Add(Error(message, NotSignedIn, e.Message));
        }

        replies.RemoveAll(string.IsNullOrEmpty);
        if (replies.Count == 0)
        {
            return;
        }

        await writeLock.WaitAsync(ct);
        try
        {
            foreach (var reply in replies)
            {
                await output.WriteLineAsync(reply);
            }

            await output.FlushAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>A JSON-RPC error for the request's id; notifications (no id) get no reply.</summary>
    private static string Error(string request, int code, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(request);
            if (!doc.RootElement.TryGetProperty("id", out var id))
            {
                return "";
            }

            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteString("jsonrpc", "2.0");
                json.WritePropertyName("id");
                id.WriteTo(json);
                json.WriteStartObject("error");
                json.WriteNumber("code", code);
                json.WriteString("message", text);
                json.WriteEndObject();
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
