using System.Collections.Concurrent;
using System.Globalization;
using Kupro.Import;
using Kupro.Import.AzureDevOps;
using Kupro.Import.GitHub;
using Kupro.Import.Jira;
using Kupro.Import.Linear;
using Kupro.Import.Snapshot;
using Kupro.Import.Trello;
using Kupro.Import.YouTrack;
using Microsoft.Extensions.Options;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// The connectors the server runs itself, by source kind (ADR-019 Source access). Each connector task registers its
/// own; a kind without one is imported from a snapshot only (CLI <c>--local</c> or an upload). Singleton.
/// </summary>
public sealed class ImportSources
{
    /// <summary>Eventually consistent source searches may show a change late, so a delta starts this far before the cursor (ADR-019 Mode).</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Func<WorkspaceImport, string, ISourceConnector>> _connectors = new(StringComparer.Ordinal);

    public ImportSources(IHttpClientFactory http, IOptions<ImportOptions> options, TimeProvider clock)
    {
        HttpClient Client() => http.CreateClient(nameof(ImportSources));
        Register(SourceKinds.AzureDevOps, (import, token) =>
            new AzureDevOpsConnector(Client(), import.BaseUrl ?? throw new ImportFailedException("An Azure DevOps import needs the organisation URL."), token, clock));
        Register(SourceKinds.Jira, (import, token) =>
            new JiraConnector(Client(), import.BaseUrl ?? throw new ImportFailedException("A Jira import needs the site URL."), token, clock));
        Register(SourceKinds.GitHub, (import, token) => new GitHubConnector(Client(), import.BaseUrl, token, clock));
        Register(SourceKinds.YouTrack, (import, token) =>
            new YouTrackConnector(Client(), import.BaseUrl ?? throw new ImportFailedException("A YouTrack import needs its address."), token, clock));
        Register(SourceKinds.Linear, (_, token) => new LinearConnector(Client(), token, LinearAuth.ApiKey, clock));
        if (options.Value.TrelloKey is { Length: > 0 } key)
        {
            // Trello pairs Kupro's own API key with the token the user authorized (ADR-019 Source access).
            Register(SourceKinds.Trello, (_, token) => new TrelloConnector(Client(), key, token, clock));
        }
    }

    public void Register(string kind, Func<WorkspaceImport, string, ISourceConnector> create) => _connectors[kind] = create;

    public bool Supports(string kind) => _connectors.ContainsKey(kind);

    public ISourceConnector Create(WorkspaceImport import, string token) =>
        _connectors.TryGetValue(import.SourceKind, out var create)
            ? create(import, token)
            : throw new ImportFailedException($"Kupro can't read {ImportDescription.SourceName(import.SourceKind)} itself. Upload a snapshot instead.");

    /// <summary>The cursor a delta reads from: a time moves back by <see cref="Overlap"/>; anything else is the connector's own and passes as is.</summary>
    public static string? WithOverlap(string? cursor) =>
        cursor is not null && DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? (at - Overlap).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            : cursor;

    /// <summary>What the connector reads: the mapping's projects, and closed items no older than its cutoff.</summary>
    public static ReadScope Scope(ImportMapping mapping, DateTimeOffset now) => new()
    {
        Projects = mapping.Scope.Projects,
        ResolvedAfter = mapping.Scope.ClosedOlderThanMonths is { } months ? now.AddMonths(-months) : null,
    };
}
