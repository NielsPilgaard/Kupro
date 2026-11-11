using Kupro.Api.Common;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Import.Snapshot;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// Imports from other trackers (ADR-019): create, upload or connect, map, plan, run, resolve conflicts, undo or
/// finish. Owners only, and people only: an import keeps a source token and creates members.
/// </summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/imports")]
[WorkspaceRole(WorkspaceRole.Owner)]
[PeopleOnly]
public sealed class ImportsController(ImportService imports) : ControllerBase
{
    private const string NotOnMcp = "Starting runs, resolving conflicts and source tokens stay off MCP, so secrets never enter a model's context (ADR-019); the read-only `imports` tool covers the rest.";

    /// <summary>Start an import. With a token Kupro reads the source itself; without one it reads a file you upload.</summary>
    [HttpPost(Name = "createImport")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportResponse>> Create(CreateImportRequest request, CancellationToken ct)
    {
        var created = await imports.CreateAsync(request, ct);
        return created.Error is { } error ? this.ToProblem(error) : this.ToResult(await imports.GetAsync(created.Value!.Id, ct), StatusCodes.Status201Created);
    }

    /// <summary>The workspace's imports, newest first, each with its latest run.</summary>
    [HttpGet(Name = "listImports")]
    [ProducesResponseType<IReadOnlyList<ImportResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ImportResponse>>> List(CancellationToken ct) => Ok(await imports.ListAsync(ct));

    [HttpGet("{id:guid}", Name = "getImport")]
    [NoAgentSurface(AgentSurface.Mcp, "The `imports` tool's list returns every import with its latest run.")]
    [ProducesResponseType<ImportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImportResponse>> Get(Guid id, CancellationToken ct) => this.ToResult(await imports.GetAsync(id, ct));

    /// <summary>
    /// Upload what the import reads, as the request body: a snapshot ZIP (docs/import-format.md), a CSV file, or a Kupro
    /// export ZIP, as the import's source kind says. Replaces an earlier upload; the next run reads this one.
    /// </summary>
    [HttpPut("{id:guid}/source", Name = "uploadImportSource")]
    [DisableRequestSizeLimit]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportUpload>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ImportUpload>> Upload(Guid id, CancellationToken ct)
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = System.IO.File.Create(path))
            {
                await Request.Body.CopyToAsync(file, ct);
            }

            return this.ToResult(await imports.UploadAsync(id, path, ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Projects, people, types, statuses and fields, read live from the source with the import's token.</summary>
    [HttpGet("{id:guid}/source", Name = "describeImportSource")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<SourceDescription>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SourceDescription>> Describe(Guid id, CancellationToken ct) => this.ToResult(await imports.DescribeAsync(id, ct));

    /// <summary>The saved mapping, or the one Kupro proposes (people matched by email and name) until one is saved.</summary>
    [HttpGet("{id:guid}/mapping", Name = "getImportMapping")]
    [ProducesResponseType<ImportMappingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImportMappingResponse>> GetMapping(Guid id, CancellationToken ct) => this.ToResult(await imports.GetMappingAsync(id, ct));

    /// <summary>Save the mapping. It applies from the next run; issues already imported keep their shape.</summary>
    [HttpPut("{id:guid}/mapping", Name = "setImportMapping")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportMapping>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImportMapping>> SetMapping(Guid id, ImportMapping mapping, CancellationToken ct) =>
        this.ToResult(await imports.SetMappingAsync(id, mapping, ct));

    /// <summary>Plan: a dry run over the stored snapshot. Writes nothing; poll the run for its plan.</summary>
    [HttpPost("{id:guid}/plan", Name = "planImport")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportRunResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ImportRunResponse>> Plan(Guid id, CancellationToken ct) => await QueueAsync(id, ImportRunKind.DryRun, ct);

    /// <summary>Run the import (also "Sync now"). One runs at a time per workspace; poll the run for progress and report.</summary>
    [HttpPost("{id:guid}/runs", Name = "runImport")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportRunResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ImportRunResponse>> Run(Guid id, CancellationToken ct) => await QueueAsync(id, ImportRunKind.Commit, ct);

    /// <summary>The import's runs, newest first (the last 50).</summary>
    [HttpGet("{id:guid}/runs", Name = "listImportRuns")]
    [ProducesResponseType<IReadOnlyList<ImportRunResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ImportRunResponse>>> Runs(Guid id, CancellationToken ct) => this.ToResult(await imports.ListRunsAsync(id, ct));

    /// <summary>A run with its plan (dry run) or report (commit): counts with sample items, conflicts, deletions.</summary>
    [HttpGet("{id:guid}/runs/{runId:guid}", Name = "getImportRun")]
    [ProducesResponseType<ImportRunDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImportRunDetail>> GetRun(Guid id, Guid runId, CancellationToken ct) => this.ToResult(await imports.GetRunAsync(id, runId, ct));

    /// <summary>Turn hourly auto-sync on or off. Needs a token: an uploaded file has nothing to fetch.</summary>
    [HttpPut("{id:guid}/auto-sync", Name = "setImportAutoSync")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportResponse>> SetAutoSync(Guid id, AutoSyncRequest request, CancellationToken ct)
    {
        var set = await imports.SetAutoSyncAsync(id, request.Enabled, ct);
        return set.Error is { } error ? this.ToProblem(error) : this.ToResult(await imports.GetAsync(id, ct));
    }

    /// <summary>Replace the source token, after the source rejected the old one. The next run continues from the saved cursor.</summary>
    [HttpPut("{id:guid}/token", Name = "replaceImportToken")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportResponse>> ReplaceToken(Guid id, ImportTokenRequest request, CancellationToken ct)
    {
        var replaced = await imports.ReplaceTokenAsync(id, request.Token, ct);
        return replaced.Error is { } error ? this.ToProblem(error) : this.ToResult(await imports.GetAsync(id, ct));
    }

    /// <summary>Fields both sides changed: open ones, or with <c>all</c> resolved ones too. 200 at a time; <c>after</c> continues.</summary>
    [HttpGet("{id:guid}/conflicts", Name = "listImportConflicts")]
    [ProducesResponseType<IReadOnlyList<ImportConflictResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ImportConflictResponse>>> Conflicts(Guid id, [FromQuery] bool all = false, [FromQuery] Guid? after = null, CancellationToken ct = default) =>
        this.ToResult(await imports.ListConflictsAsync(id, all, after, ct));

    /// <summary>Keep Kupro's value (closes the conflict) or use the source's (applies it). It comes back only if the source changes again.</summary>
    [HttpPost("{id:guid}/conflicts/{conflictId:guid}/resolve", Name = "resolveImportConflict")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportConflict>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportConflict>> Resolve(Guid id, Guid conflictId, ResolveConflictRequest request, CancellationToken ct) =>
        this.ToResult(await imports.ResolveConflictAsync(id, conflictId, request.UseSource, ct));

    /// <summary>What Undo would lose (issues edited in Kupro since) and what stands in its way.</summary>
    [HttpGet("{id:guid}/undo", Name = "checkImportUndo")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportUndoCheck>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ImportUndoCheck>> CheckUndo(Guid id, CancellationToken ct) => this.ToResult(await imports.CheckUndoAsync(id, ct));

    /// <summary>
    /// Delete everything the import created. Refused (<c>undone: false</c>, with the list) while records it didn't
    /// create point at its issues.
    /// </summary>
    [HttpPost("{id:guid}/undo", Name = "undoImport")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportUndoResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ImportUndoResult>> Undo(Guid id, CancellationToken ct) => this.ToResult(await imports.UndoAsync(id, ct));

    /// <summary>Finish the migration: auto-sync stops and the token is deleted. Aliases and history stay; Undo is gone.</summary>
    [HttpPost("{id:guid}/finish", Name = "finishImport")]
    [NoAgentSurface(AgentSurface.Mcp, NotOnMcp)]
    [ProducesResponseType<ImportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ImportResponse>> Finish(Guid id, CancellationToken ct)
    {
        var finished = await imports.FinishAsync(id, ct);
        return finished.Error is { } error ? this.ToProblem(error) : this.ToResult(await imports.GetAsync(id, ct));
    }

    private async Task<ActionResult<ImportRunResponse>> QueueAsync(Guid id, ImportRunKind kind, CancellationToken ct)
    {
        var queued = await imports.QueueAsync(id, kind, ct);
        return queued.Error is { } error ? this.ToProblem(error)
            : StatusCode(StatusCodes.Status202Accepted, new ImportRunResponse(queued.Value!.Id, kind, queued.Value.State, null, null, queued.Value.CreatedAt, null, null));
    }
}
