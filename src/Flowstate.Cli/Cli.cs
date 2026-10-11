using System.CommandLine;
using System.CommandLine.Parsing;
using Flowstate.Cli.Api;
using Flowstate.Cli.Api.Models;
using Flowstate.Cli.Commands;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Serialization.Json;

namespace Flowstate.Cli;

/// <summary>A command that wraps API operations; the parity test (task 006) reads these ids.</summary>
public sealed class ApiCommand(string name, string description, params string[] operationIds) : Command(name, description)
{
    public IReadOnlyList<string> OperationIds { get; } = operationIds;
}

/// <summary>Everything a command handler needs for one invocation.</summary>
public sealed class CommandContext(ParseResult parse, ApiSession session, Output output)
{
    private string? _workspace;

    public ParseResult Parse { get; } = parse;
    public ApiSession Session { get; } = session;
    public Output Out { get; } = output;
    public FlowstateClient Api => _api ??= Session.CreateClient();
    private FlowstateClient? _api;

    public T? Get<T>(Option<T> option) => Parse.GetValue(option);
    public T? Get<T>(Argument<T> argument) => Parse.GetValue(argument);

    /// <summary><c>--workspace</c>, then FLOWSTATE_WORKSPACE / config, then your only workspace.</summary>
    public async Task<string> WorkspaceAsync(CancellationToken ct)
    {
        if (_workspace is not null)
        {
            return _workspace;
        }

        _workspace = Parse.GetValue(Cli.WorkspaceOption) ?? Session.Config.EffectiveWorkspace;
        if (_workspace is null)
        {
            var mine = await Api.Api.V1.Workspaces.GetAsync(cancellationToken: ct) ?? [];
            _workspace = mine.Count == 1
                ? mine[0].Slug
                : throw new CliException(mine.Count == 0
                    ? "You're not in any workspace. Create one with `flowstate workspace create <slug>`."
                    : "Pick a workspace with --workspace or `flowstate workspace use <slug>`.");
        }

        return _workspace!;
    }

    public async Task<Api.Api.V1.Workspaces.Item.WithWorkspaceItemRequestBuilder> InWorkspaceAsync(CancellationToken ct) =>
        Api.Api.V1.Workspaces[await WorkspaceAsync(ct)];
}

/// <summary>Overrides for in-process runs (tests): config, output and HTTP transport.</summary>
public sealed record CliRuntime(
    CliConfig Config, TextWriter Out, TextWriter Error, HttpMessageHandler? Transport, bool Json, TextReader? In = null);

public static class Cli
{
    /// <summary>Per async flow, so parallel tests don't share console or config.</summary>
    public static readonly AsyncLocal<CliRuntime?> Runtime = new();

    public static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Print JSON (default when output is piped).",
        Recursive = true,
    };

    public static readonly Option<string?> WorkspaceOption = new("--workspace", "-w")
    {
        Description = "Workspace slug (default: FLOWSTATE_WORKSPACE, `workspace use`, or your only workspace).",
        Recursive = true,
    };

    public static readonly Option<string?> UrlOption = new("--url")
    {
        Description = "API base URL (default: FLOWSTATE_URL or the configured URL).",
        Recursive = true,
    };

    public static RootCommand BuildRoot()
    {
        // JSON in/out (--file, --json) must work before any API client has registered Kiota's factories.
        ApiClientBuilder.RegisterDefaultSerializer<JsonSerializationWriterFactory>();
        ApiClientBuilder.RegisterDefaultDeserializer<JsonParseNodeFactory>();

        var root = new RootCommand("Flowstate CLI: the issue tracker your agents can use too.");
        root.Options.Add(JsonOption);
        root.Options.Add(WorkspaceOption);
        root.Options.Add(UrlOption);
        foreach (var command in AccountCommands.Build()
                     .Concat(WorkspaceCommands.Build())
                     .Concat(ProjectCommands.Build())
                     .Concat(IssueCommands.Build())
                     .Concat(SprintCommands.Build())
                     .Concat(PlanCommands.Build())
                     .Append(McpCommand.Build()))
        {
            root.Subcommands.Add(command);
        }

        return root;
    }

    /// <summary>Every operation id some command covers.</summary>
    public static IReadOnlySet<string> CoveredOperations()
    {
        static IEnumerable<Command> Walk(Command c) => c.Subcommands.SelectMany(Walk).Prepend(c);
        return Walk(BuildRoot()).OfType<ApiCommand>().SelectMany(c => c.OperationIds).ToHashSet();
    }

    public static Task<int> RunAsync(string[] args) => BuildRoot().Parse(args).InvokeAsync();

    /// <summary>Wires a handler with shared error handling and exit codes (0 ok, 1 error, 2 usage).</summary>
    public static T Handle<T>(this T command, Func<CommandContext, CancellationToken, Task> handler) where T : Command
    {
        command.SetAction(async (parse, ct) =>
        {
            var runtime = Runtime.Value;
            var config = runtime?.Config ?? CliConfig.Load();
            if (parse.GetValue(UrlOption) is { } url)
            {
                config.Url = url;
            }

            var json = parse.GetValue(JsonOption) || (runtime?.Json ?? Console.IsOutputRedirected);
            var error = runtime?.Error ?? Console.Error;
            var context = new CommandContext(parse, new ApiSession(config, runtime?.Transport),
                new Output(runtime?.Out ?? Console.Out, json));
            try
            {
                await handler(context, ct);
                return 0;
            }
            catch (ProblemDetails problem)
            {
                var code = problem.AdditionalData.TryGetValue("code", out var c) ? c?.ToString() : null;
                await error.WriteLineAsync($"error: {code ?? problem.Title}{(problem.Detail is { } d ? $": {d}" : "")}");
                if (problem.AdditionalData.TryGetValue("errors", out var errors) && errors is not null)
                {
                    await error.WriteLineAsync(errors.ToString());
                }

                return 1;
            }
            catch (ApiException api)
            {
                await error.WriteLineAsync(api.ResponseStatusCode == 401
                    ? "error: not signed in or token rejected. Run `flowstate login` or `flowstate token set <token>`."
                    : $"error: the API answered {api.ResponseStatusCode}.");
                return 1;
            }
            catch (CliException e)
            {
                await error.WriteLineAsync($"error: {e.Message}");
                return 1;
            }
            catch (HttpRequestException e)
            {
                await error.WriteLineAsync($"error: can't reach {config.EffectiveUrl} ({e.Message}).");
                return 1;
            }
        });
        return command;
    }

    /// <summary>Enum option that accepts any case and dashes: <c>in-progress</c>, <c>inProgress</c>, <c>IN_PROGRESS</c>.</summary>
    public static Option<T?> EnumOption<T>(string name, string description) where T : struct, Enum =>
        new(name)
        {
            Description = $"{description} ({string.Join(", ", Enum.GetNames<T>().Select(Dashed))}).",
            CustomParser = result => ParseEnum<T>(result, result.Tokens.Single().Value),
        };

    public static Option<T[]> EnumListOption<T>(string name, string description) where T : struct, Enum =>
        new(name)
        {
            Description = $"{description}; repeat for several ({string.Join(", ", Enum.GetNames<T>().Select(Dashed))}).",
            AllowMultipleArgumentsPerToken = true,
            CustomParser = result => result.Tokens.Select(t => ParseEnum<T>(result, t.Value)).OfType<T>().ToArray(),
        };

    public static Argument<T> EnumArgument<T>(string name, string description) where T : struct, Enum =>
        new(name)
        {
            Description = $"{description} ({string.Join(", ", Enum.GetNames<T>().Select(Dashed))}).",
            CustomParser = result => ParseEnum<T>(result, result.Tokens.Single().Value) ?? default,
        };

    public static string Dashed(string pascal) =>
        string.Concat(pascal.Select((ch, i) => char.IsUpper(ch) && i > 0 ? "-" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));

    private static T? ParseEnum<T>(SymbolResult result, string value) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value.Replace("-", "").Replace("_", ""), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        result.AddError($"'{value}' is not one of: {string.Join(", ", Enum.GetNames<T>().Select(Dashed))}.");
        return null;
    }
}
