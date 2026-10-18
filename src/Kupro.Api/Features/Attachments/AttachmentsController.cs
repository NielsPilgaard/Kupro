using Kupro.Api.Common;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Attachments;

/// <summary>Files for descriptions and comments: create, upload to storage, complete, then reference as <c>attachment:ID</c>.</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/attachments")]
public sealed class AttachmentsController(AttachmentService attachments) : ControllerBase
{
    /// <summary>Start an upload: returns a presigned URL to PUT the file to, valid for 10 minutes.</summary>
    [HttpPost(Name = "createAttachment")]
    [WorkspaceRole(WorkspaceRole.Guest)]
    [NoAgentSurface(AgentSurface.Mcp, "MCP clients can't PUT file bytes to a URL; agents attach files with `kupro issue attach`.")]
    [ProducesResponseType<AttachmentUpload>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AttachmentUpload>> Create(CreateAttachmentRequest request, CancellationToken ct) =>
        this.ToResult(await attachments.CreateAsync(request, ct), StatusCodes.Status201Created);

    /// <summary>Finish an upload once the file is in storage.</summary>
    [HttpPost("{id:guid}/complete", Name = "completeAttachment")]
    [WorkspaceRole(WorkspaceRole.Guest)]
    [NoAgentSurface(AgentSurface.Mcp, "Second half of the upload above; agents use `kupro issue attach`.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AttachmentResponse>> Complete(Guid id, CancellationToken ct) =>
        this.ToResult(await attachments.CompleteAsync(id, ct));

    /// <summary>Attachment details with a download URL valid for 5 minutes.</summary>
    [HttpGet("{id:guid}", Name = "getAttachment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AttachmentResponse>> Get(Guid id, CancellationToken ct) =>
        this.ToResult(await attachments.GetAsync(id, ct));
}
