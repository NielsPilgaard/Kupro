using System.ComponentModel;
using Flowstate.Api.Common;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Users;
using Flowstate.Api.Features.Workspaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Flowstate.Api.Mcp;

/// <summary>
/// Coarse MCP tools (ADR-001): few tools, bulk where it matters, issue keys everywhere. Each tool is a thin adapter over
/// the same feature service the REST controller uses.
/// </summary>
[McpServerToolType]
public sealed class FlowstateTools(
    McpWorkspace scope,
    WorkspaceService workspaces,
    ProjectService projects,
    IssueService issueService,
    LabelService labels,
    UserService users)
{
    private const string WorkspaceHelp = "Workspace slug. Optional when your token is limited to one workspace or you are in only one.";

    [McpServerTool(Name = "get_me", ReadOnly = true), Description("Who you are: your user, and the agent name if you use an agent token.")]
    [CoversOperations("getMe")]
    public async Task<MeResponse> GetMe(CancellationToken ct = default) =>
        await users.GetMeAsync(scope.UserId, scope.AgentName, ct) ?? throw new McpException("user_not_found");

    [McpServerTool(Name = "list_workspaces", ReadOnly = true), Description("Workspaces you can work in, with your role. Pass a slug to get one workspace's details (seats, hours per week).")]
    [CoversOperations("listWorkspaces", "getWorkspace")]
    public async Task<object> ListWorkspaces([Description("Optional workspace slug for details.")] string? workspace = null, CancellationToken ct = default)
    {
        if (workspace is null)
        {
            return await workspaces.ListMineAsync(scope.UserId, ct);
        }

        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return await workspaces.GetAsync(ct);
    }

    [McpServerTool(Name = "list_projects", ReadOnly = true), Description("Projects in a workspace (key, name, issue count). Issue keys are PROJECTKEY-NUMBER.")]
    [CoversOperations("listProjects", "getProject")]
    public async Task<IReadOnlyList<ProjectResponse>> ListProjects([Description(WorkspaceHelp)] string? workspace = null, CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return await projects.ListAsync(ct);
    }

    [McpServerTool(Name = "create_project"), Description("Create a project. key: 2-10 uppercase letters/digits, starting with a letter.")]
    [CoversOperations("createProject")]
    public async Task<ProjectResponse> CreateProject(
        [Description("Project key, e.g. FLOW.")] string key,
        [Description("Project name.")] string name,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
        var (result, project) = await projects.CreateAsync(new CreateProjectRequest(key.ToUpperInvariant(), name), ct);
        return result == ProjectChangeResult.Ok ? project! : throw new McpException($"project_key_taken: {key} exists.");
    }

    [McpServerTool(Name = "list_members", ReadOnly = true), Description("People and agents in a workspace, with member ids for assigning issues.")]
    [CoversOperations("listMembers")]
    public async Task<IReadOnlyList<MemberResponse>> ListMembers([Description(WorkspaceHelp)] string? workspace = null, CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return await workspaces.ListMembersAsync(ct);
    }

    [McpServerTool(Name = "list_labels", ReadOnly = true), Description("Labels in a workspace with usage counts. New labels are created by using them.")]
    [CoversOperations("listLabels")]
    public async Task<IReadOnlyList<LabelResponse>> ListLabels([Description(WorkspaceHelp)] string? workspace = null, CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return await labels.ListAsync(ct);
    }

    [McpServerTool(Name = "search_issues", ReadOnly = true), Description(
        "Find issues. Filters combine with AND; list values combine with OR. Returns up to `limit` rows and a nextCursor for the next page.")]
    [CoversOperations("listIssues")]
    public async Task<IssuePage> SearchIssues(
        [Description(WorkspaceHelp)] string? workspace = null,
        [Description("Project keys.")] string[]? project = null,
        [Description("States: backlog, todo, inProgress, inReview, done, canceled.")] IssueState[]? state = null,
        [Description("Types: epic, feature, task.")] IssueType[]? type = null,
        [Description("Member id, `me`, or `none`.")] string? assignee = null,
        [Description("Label name, e.g. bug.")] string? label = null,
        [Description("Parent key: list its children.")] string? parent = null,
        [Description("updated (default), created, priority or rank.")] IssueSort? sort = null,
        [Description("Page size 1-200, default 50.")] int? limit = null,
        [Description("nextCursor from the previous call.")] string? cursor = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        var result = await issueService.ListAsync(new IssueQuery
        {
            Project = project,
            State = state,
            Type = type,
            Assignee = assignee,
            Label = label,
            Parent = parent,
            Sort = sort ?? IssueSort.Updated,
            Limit = Math.Clamp(limit ?? 50, 1, 200),
            Cursor = cursor,
        }, ct);
        return Unwrap(result);
    }

    [McpServerTool(Name = "get_issue", ReadOnly = true), Description("One issue by key (e.g. FLOW-12): description, children, relations, labels, effort.")]
    [CoversOperations("getIssue")]
    public async Task<IssueDetail> GetIssue([Description("Issue key.")] string key, [Description(WorkspaceHelp)] string? workspace = null, CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return await issueService.GetAsync(key, ct) ?? throw new McpException(Message(IssueError.NotFound(key)));
    }

    [McpServerTool(Name = "create_issues"), Description(
        "Create 1-100 issues, all-or-nothing. Hierarchy: epic > feature > task (a task may sit directly under an epic); " +
        "create parents first and pass their key as `parent`. Effort is in hours. Labels are created on first use.")]
    [CoversOperations("createIssues", "createIssue")]
    public async Task<IReadOnlyList<IssueSummary>> CreateIssues(
        [Description("Issues to create.")] CreateIssueRequest[] issues,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
        return Unwrap(await issueService.CreateManyAsync(issues, ct));
    }

    [McpServerTool(Name = "update_issues"), Description(
        "Update 1-100 issues, all-or-nothing. Only fields you set change; list fields in `clear` to empty them. " +
        "Close an issue with state=done. Labels: `labels` replaces, `addLabels`/`removeLabels` edit.")]
    [CoversOperations("updateIssues", "updateIssue")]
    public async Task<IReadOnlyList<IssueSummary>> UpdateIssues(
        [Description("Updates: { key, changes }.")] IssueUpdate[] items,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
        return Unwrap(await issueService.UpdateManyAsync(items, ct));
    }

    [McpServerTool(Name = "relate_issues"), Description("Relate two issues: blocks, blockedBy, relates, duplicates, duplicatedBy. Returns the relation id.")]
    [CoversOperations("addIssueRelation")]
    public async Task<IssueRelationResponse> RelateIssues(
        [Description("Issue key.")] string key,
        [Description("How `key` relates to `target`.")] RelationKind type,
        [Description("Other issue key.")] string target,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
        return Unwrap(await issueService.AddRelationAsync(key, new AddRelationRequest(type, target), ct));
    }

    [McpServerTool(Name = "unrelate_issues", Destructive = true), Description("Remove a relation by id (from get_issue).")]
    [CoversOperations("removeIssueRelation")]
    public async Task<string> UnrelateIssues(
        [Description("Issue key.")] string key,
        [Description("Relation id.")] Guid relationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
        return await issueService.RemoveRelationAsync(key, relationId, ct) is { } error ? throw new McpException(Message(error)) : "removed";
    }

    private static T Unwrap<T>(IssueResult<T> result) =>
        result.Error is { } error ? throw new McpException(Message(error)) : result.Value!;

    private static string Message(IssueError error) => $"{error.Code}: {error.Detail}";
}
