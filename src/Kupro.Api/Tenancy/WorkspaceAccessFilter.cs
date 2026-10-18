using Kupro.Api.Auth;
using Kupro.Api.Common;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Workspaces;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Kupro.Api.Tenancy;

/// <summary>Minimum workspace role for an action or controller. Without it, any active member (Guest+) may call.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class WorkspaceRoleAttribute(WorkspaceRole minimum) : Attribute
{
    public WorkspaceRole Minimum { get; } = minimum;
}

/// <summary>
/// Global filter: any route with a <c>{workspace}</c> segment is resolved to a workspace the caller is an active member
/// of, or answered with 404 so other tenants' workspaces are indistinguishable from missing ones.
/// </summary>
public sealed class WorkspaceAccessFilter(WorkspaceService workspaces, WorkspaceContext workspaceContext)
    : IAsyncActionFilter
{
    public const string RouteKey = "workspace";

    /// <summary>Which client sent the request: <c>web</c>, <c>cli</c> or <c>mcp</c>; default <c>api</c>.</summary>
    public const string ClientHeader = "X-Kupro-Client";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.RouteData.Values[RouteKey] is not string slug)
        {
            await next();
            return;
        }

        var http = context.HttpContext;
        var userId = http.User.GetUserId();
        var agentName = http.User.GetAgentName();
        var access = await workspaces.ResolveAccessAsync(
            slug, userId, agentName, http.User.GetTokenWorkspaceId(), http.RequestAborted);

        if (access.Outcome == WorkspaceAccessOutcome.NotFound)
        {
            context.Result = Problems.Create(http, StatusCodes.Status404NotFound, "workspace_not_found");
            return;
        }

        if (access.Outcome == WorkspaceAccessOutcome.Deactivated)
        {
            context.Result = Problems.Create(http, StatusCodes.Status403Forbidden, "member_deactivated");
            return;
        }

        var required = context.ActionDescriptor.EndpointMetadata.OfType<WorkspaceRoleAttribute>().LastOrDefault();
        if (required is not null && access.Role < required.Minimum)
        {
            context.Result = Problems.Create(http, StatusCodes.Status403Forbidden, "insufficient_role",
                $"Requires the {required.Minimum} role.");
            return;
        }

        workspaceContext.Set(access, slug, userId, agentName);
        if (Enum.TryParse<Surface>(http.Request.Headers[ClientHeader], ignoreCase: true, out var surface))
        {
            workspaceContext.Surface = surface;
        }

        await next();
    }
}
