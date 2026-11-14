using System.ComponentModel;
using Kupro.Api.Common;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Workspaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Kupro.Api.Mcp;

/// <summary>What the <c>imports</c> tool returns: the part the action asked for.</summary>
public sealed record ImportsView(
    IReadOnlyList<ImportResponse>? Imports = null,
    IReadOnlyList<ImportRunResponse>? Runs = null,
    ImportRunDetail? Run = null,
    IReadOnlyList<ImportConflictResponse>? Conflicts = null,
    ImportMappingResponse? Mapping = null);

public enum ImportsAction
{
    List,
    Runs,
    Run,
    Conflicts,
    Mapping,
}

/// <summary>
/// One read-only tool for imports (ADR-019 Who and where): an agent can help triage plans, reports and conflicts.
/// Starting runs, resolving conflicts and source tokens stay with people, so secrets never enter a model's context.
/// </summary>
public sealed partial class KuproTools
{
    [McpServerTool(Name = "imports", ReadOnly = true, UseStructuredContent = false), Description(
        "Imports (owners), read-only: list; runs, conflicts, mapping by import id; run (plan or report) by run id.")]
    [CoversOperations("listImports", "listImportRuns", "getImportRun", "listImportConflicts", "getImportMapping")]
    public async Task<ImportsView> Imports(
        ImportsAction action,
        Guid? id = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Owner, ct);
        return (action, id) switch
        {
            (ImportsAction.List, _) => new ImportsView(Imports: await importService.ListAsync(ct)),
            (ImportsAction.Runs, { } i) => new ImportsView(Runs: Unwrap(await importService.ListRunsAsync(i, ct))),
            (ImportsAction.Run, { } r) => new ImportsView(Run: Unwrap(await importService.GetRunAsync(null, r, ct))),
            (ImportsAction.Conflicts, { } i) => new ImportsView(Conflicts: Unwrap(await importService.ListConflictsAsync(i, all: false, after: null, ct))),
            (ImportsAction.Mapping, { } i) => new ImportsView(Mapping: Unwrap(await importService.GetMappingAsync(i, ct))),
            _ => throw new McpException("invalid_arguments: every action but list needs id: an import's, or for run a run's."),
        };
    }
}
