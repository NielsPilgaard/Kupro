using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kupro.Api.Common;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kupro.Api.Features.Time;

/// <summary>
/// Time registration (ADR-021): log time on issues, your own timesheet, and for Owners everyone's entries and the
/// export. Off until an Owner turns it on for the workspace.
/// </summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}")]
public sealed class TimeController(TimeService time, IOptions<JsonOptions> json) : ControllerBase
{
    /// <summary>
    /// Log time on an issue. Optionally close it in the same save with <c>state</c> (done or canceled): both commit or
    /// neither does. Refused while the issue is done or canceled, past 24 hours in a day, or for a future day.
    /// </summary>
    [HttpPost("time", Name = "logTime")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<TimeEntryResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TimeEntryResponse>> Log(LogTimeRequest request, CancellationToken ct) =>
        this.ToResult(await time.LogAsync(request, ct), StatusCodes.Status201Created);

    /// <summary>Change the duration, day or note of your own entry. Refused while its issue is done or canceled.</summary>
    [HttpPatch("time/{id:guid}", Name = "updateTimeEntry")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TimeEntryResponse>> Update(Guid id, UpdateTimeEntryRequest request, CancellationToken ct) =>
        this.ToResult(await time.UpdateAsync(id, request, ct));

    /// <summary>Delete your own entry. Its history is kept. Refused while its issue is done or canceled.</summary>
    [HttpDelete("time/{id:guid}", Name = "deleteTimeEntry")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await time.DeleteAsync(id, ct) is { } error ? this.ToProblem(error) : NoContent();

    /// <summary>
    /// Your entries and day totals for a range of up to 93 days. Without dates: this week, Monday to Sunday. With only
    /// <c>to</c>: the week it is in.
    /// </summary>
    /// <param name="from">First day, YYYY-MM-DD.</param>
    /// <param name="to">Last day, inclusive; default 6 days after <c>from</c>.</param>
    /// <param name="ct">Cancelled when the request is.</param>
    [HttpGet("time/mine", Name = "listMyTime")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MyTime>> Mine([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        this.ToResult(await time.MineAsync(from, to, ct));

    /// <summary>Time on an issue: yours, and for Owners the total and each person's share.</summary>
    [HttpGet("issues/{key}/time", Name = "getIssueTime")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IssueTime>> ForIssue(string key, CancellationToken ct) =>
        this.ToResult(await time.ForIssueAsync(key, ct));

    /// <summary>
    /// History of time entries, newest first: every create, edit and delete with who made it. Yours, or for Owners any
    /// member's.
    /// </summary>
    /// <param name="member">Member id; default you. Owners only for someone else.</param>
    /// <param name="before">Id of the oldest change you have; returns the ones before it.</param>
    /// <param name="limit">Changes per page, 1-200.</param>
    /// <param name="ct">Cancelled when the request is.</param>
    [HttpGet("time/changes", Name = "listTimeChanges")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [NoAgentSurface(AgentSurface.Mcp, "An audit trail for people checking records; agents log and correct time through the time tool.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<TimeEntryChangeResponse>>> Changes(
        [FromQuery] Guid? member, [FromQuery] Guid? before, [FromQuery, Range(1, 200)] int limit = 100, CancellationToken ct = default) =>
        this.ToResult(await time.ChangesAsync(member, before, limit, ct));

    /// <summary>Everyone's entries for up to 93 days, or one member's. Owners only.</summary>
    /// <param name="from">First day, YYYY-MM-DD.</param>
    /// <param name="to">Last day, inclusive.</param>
    /// <param name="member">Member id; everyone when omitted.</param>
    /// <param name="ct">Cancelled when the request is.</param>
    [HttpGet("time/entries", Name = "listTimeEntries")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [NoAgentSurface(AgentSurface.Mcp, "Personal data in bulk; Owners read it in the web app or the CLI (ADR-021).")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TimeEntryList>> List(
        [FromQuery, Required] DateOnly from, [FromQuery, Required] DateOnly to, [FromQuery] Guid? member, CancellationToken ct) =>
        this.ToResult(await time.ListAsync(from, to, member, ct));

    /// <summary>
    /// The employer's copy of everyone's time for up to 366 days, or one member's: CSV (default) or JSON, one row per
    /// entry, members who left included. Owners only.
    /// </summary>
    /// <param name="from">First day, YYYY-MM-DD.</param>
    /// <param name="to">Last day, inclusive.</param>
    /// <param name="member">Member id; everyone when omitted.</param>
    /// <param name="format">csv or json.</param>
    /// <param name="ct">Cancelled when the request is.</param>
    [HttpGet("time/export", Name = "exportTime")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [NoAgentSurface(AgentSurface.Mcp, "Personal data in bulk; Owners export it in the web app or the CLI (ADR-021).")]
    [Produces("text/csv", "application/json")]
    [ProducesResponseType<IReadOnlyList<TimeExportRow>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Export(
        [FromQuery, Required] DateOnly from, [FromQuery, Required] DateOnly to, [FromQuery] Guid? member,
        [FromQuery] TimeExportFormat format = TimeExportFormat.Csv, CancellationToken ct = default)
    {
        var rows = time.Export(from, to, member, ct);
        if (rows.Error is { } error)
        {
            return this.ToProblem(error);
        }

        var name = $"time-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.{(format == TimeExportFormat.Csv ? "csv" : "json")}";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        if (format == TimeExportFormat.Json)
        {
            Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(Response.Body, rows.Value!, json.Value.JsonSerializerOptions, ct);
            return new EmptyResult();
        }

        Response.ContentType = "text/csv; charset=utf-8";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("date,member_id,member,email,issue,issue_title,minutes,hours,note,deactivated".AsMemory(), ct);
        await foreach (var row in rows.Value!.WithCancellation(ct))
        {
            await writer.WriteLineAsync(string.Join(',',
                row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.MemberId.ToString(),
                Csv(row.Member),
                Csv(row.Email),
                Csv(row.Issue),
                Csv(row.IssueTitle),
                row.Minutes.ToString(CultureInfo.InvariantCulture),
                (row.Minutes / 60m).ToString("0.##", CultureInfo.InvariantCulture),
                Csv(row.Note),
                row.Deactivated ? "true" : "false").AsMemory(), ct);
        }

        return new EmptyResult();
    }

    /// <summary>RFC 4180 quoting; a leading formula character is escaped so a spreadsheet shows it as text.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
