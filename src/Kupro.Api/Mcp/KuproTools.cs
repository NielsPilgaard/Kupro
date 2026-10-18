using System.ComponentModel;
using Kupro.Api.Common;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Plan;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Users;
using Kupro.Api.Features.Workspaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Kupro.Api.Mcp;

/// <summary>
/// Coarse MCP tools (ADR-001): few tools, bulk where it matters, issue keys everywhere. Each tool is a thin adapter over
/// the same feature service the REST controller uses.
/// </summary>
[McpServerToolType]
public sealed partial class KuproTools(
    McpWorkspace scope,
    WorkspaceService workspaces,
    ProjectService projects,
    IssueService issueService,
    LabelService labels,
    SprintService sprints,
    PlanService plan,
    CommentService comments,
    ActivityFeed feed,
    AttachmentService attachments,
    UserService users,
    MeetingService meetings)
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
        "Find issues by text and filters. `query` takes free text (typo tolerant) plus filters, e.g. " +
        "`drag epics type:feature state:todo,inProgress assignee:me sprint:current label:bug`. Filters combine with AND; " +
        "list values combine with OR. Returns up to `limit` rows (best match first when searching) and a nextCursor.")]
    [CoversOperations("listIssues")]
    public async Task<IssuePage> SearchIssues(
        [Description("Search text with optional key:value filters.")] string? query = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        [Description("Project keys.")] string[]? project = null,
        [Description("States: backlog, todo, inProgress, inReview, done, canceled.")] IssueState[]? state = null,
        [Description("Types: epic, feature, task.")] IssueType[]? type = null,
        [Description("Member id, `me`, or `none`.")] string? assignee = null,
        [Description("Label name, e.g. bug.")] string? label = null,
        [Description("Parent key: list its children.")] string? parent = null,
        [Description("Sprint id, `active`, or `none`.")] string? sprint = null,
        [Description("relevance (default when searching), updated (default otherwise), created, priority or rank.")] IssueSort? sort = null,
        [Description("Page size 1-200, default 50.")] int? limit = null,
        [Description("nextCursor from the previous call.")] string? cursor = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        var result = await issueService.ListAsync(new IssueQuery
        {
            Q = query,
            Project = project,
            State = state,
            Type = type,
            Assignee = assignee,
            Label = label,
            Parent = parent,
            Sprint = sprint,
            Sort = sort,
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

    [McpServerTool(Name = "list_sprints", ReadOnly = true), Description(
        "Sprints with dates, state and effort totals (hours, done hours). Put issues in a sprint with update_issues " +
        "(`sprint`: id or `active`); find a sprint's issues with search_issues `sprint`.")]
    [CoversOperations("listSprints", "getSprint")]
    public async Task<IReadOnlyList<SprintResponse>> ListSprints(
        [Description("Project key; all projects when omitted.")] string? project = null,
        [Description("States: planned, active, closed.")] SprintState[]? state = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return Unwrap(await sprints.ListAsync(project, state, ct));
    }

    [McpServerTool(Name = "manage_sprint"), Description(
        "Create, start or close a sprint. create needs project, name, startDate, endDate. close moves unfinished " +
        "issues to the next planned sprint (default) or to the backlog (moveUnfinishedTo=backlog).")]
    [CoversOperations("createSprint", "startSprint", "closeSprint")]
    public async Task<object> ManageSprint(
        [Description("create, start or close.")] string action,
        [Description("Sprint id (start, close).")] Guid? id = null,
        [Description("Project key (create).")] string? project = null,
        [Description("Name (create).")] string? name = null,
        [Description("First day, YYYY-MM-DD (create).")] DateOnly? startDate = null,
        [Description("Last day, YYYY-MM-DD (create).")] DateOnly? endDate = null,
        [Description("Goal (create).")] string? goal = null,
        [Description("next or backlog (close).")] UnfinishedDestination? moveUnfinishedTo = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
        return action switch
        {
            "create" when project is not null && name is not null && startDate is { } start && endDate is { } end =>
                Unwrap(await sprints.CreateAsync(new CreateSprintRequest(project, name, start, end, goal), ct)),
            "start" when id is { } startId => Unwrap(await sprints.StartAsync(startId, ct)),
            "close" when id is { } closeId => Unwrap(await sprints.CloseAsync(closeId,
                new CloseSprintRequest(moveUnfinishedTo ?? UnfinishedDestination.Next), ct)),
            _ => throw new McpException("invalid_arguments: create needs project, name, startDate, endDate; start and close need id."),
        };
    }

    [McpServerTool(Name = "get_plan", ReadOnly = true), Description(
        "The delivery plan: per project and in total, per month, demand (hours of dated epic/feature effort spread " +
        "evenly over each card's days) vs supply (people x hours per week), as hours and people; gapPeople > 0 means " +
        "short of people. Also cards, milestones, cross-project dependencies (violated = blocker due after the blocked " +
        "card starts) and plan-health flags. Use it to answer 'do we need to hire for Q2 2027?'.")]
    [CoversOperations("getPlan", "listMilestones", "getProjectCapacity")]
    public async Task<PlanResponse> GetPlan(
        [Description("First month, YYYY-MM-DD (any day). Default: this month.")] DateOnly? from = null,
        [Description("Last month. Default: December next year.")] DateOnly? to = null,
        [Description("Project keys; all when omitted.")] string[]? project = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return Unwrap(await plan.GetPlanAsync(from, to, project, ct));
    }

    [McpServerTool(Name = "set_capacity"), Description(
        "Set how many people work on a project from which month: the full step function, e.g. " +
        "[{fromMonth: 2027-01-01, people: 3}, {fromMonth: 2027-07-01, people: 5}]. Replaces existing steps.")]
    [CoversOperations("setProjectCapacity")]
    public async Task<IReadOnlyList<CapacityEntry>> SetCapacity(
        [Description("Project key.")] string project,
        [Description("Steps: fromMonth (first of month) and people.")] CapacityEntry[] entries,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
        return Unwrap(await plan.SetCapacityAsync(project, new SetCapacityRequest(entries), ct));
    }

    [McpServerTool(Name = "manage_milestone"), Description(
        "Create, update or delete a milestone (a dated marker on the plan; workspace-wide unless `project` is set).")]
    [CoversOperations("createMilestone", "updateMilestone", "deleteMilestone")]
    public async Task<object> ManageMilestone(
        [Description("create, update or delete.")] string action,
        [Description("Milestone id (update, delete).")] Guid? id = null,
        [Description("Name (create, update).")] string? name = null,
        [Description("Date, YYYY-MM-DD (create, update).")] DateOnly? date = null,
        [Description("Project key (create).")] string? project = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
        return action switch
        {
            "create" when name is not null && date is { } d => Unwrap(await plan.CreateMilestoneAsync(new CreateMilestoneRequest(name, d, project), ct)),
            "update" when id is { } u => Unwrap(await plan.UpdateMilestoneAsync(u, new UpdateMilestoneRequest(name, date), ct)),
            "delete" when id is { } x => await plan.DeleteMilestoneAsync(x, ct) ? "deleted" : throw new McpException("milestone_not_found"),
            _ => throw new McpException("invalid_arguments: create needs name and date; update and delete need id."),
        };
    }

    [McpServerTool(Name = "get_issue_activity", ReadOnly = true), Description(
        "An issue's history (created, field changes with from/to, relations) and comments, oldest first, with who did " +
        "it and via which surface. Comment bodies are markdown; `attachment:ID` links resolve with get_attachment.")]
    [CoversOperations("listIssueActivity")]
    public async Task<IReadOnlyList<FeedItem>> GetIssueActivity(
        [Description("Issue key.")] string key, [Description(WorkspaceHelp)] string? workspace = null, CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return await feed.GetAsync(key, ct) ?? throw new McpException(Message(IssueError.NotFound(key)));
    }

    [McpServerTool(Name = "comment"), Description(
        "Add a markdown comment to an issue, or edit or delete one of your own comments.")]
    [CoversOperations("addComment", "updateComment", "deleteComment")]
    public async Task<object> Comment(
        [Description("add, edit or delete.")] string action,
        [Description("Issue key (add).")] string? key = null,
        [Description("Comment id (edit, delete).")] Guid? id = null,
        [Description("Markdown body (add, edit).")] string? body = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return action switch
        {
            "add" when key is not null && !string.IsNullOrWhiteSpace(body) => Unwrap(await comments.AddAsync(key, new CommentRequest(body), ct)),
            "edit" when id is { } e && !string.IsNullOrWhiteSpace(body) => Unwrap(await comments.UpdateAsync(e, new CommentRequest(body), ct)),
            "delete" when id is { } d => await comments.DeleteAsync(d, ct) is { } error ? throw new McpException(Message(error)) : "deleted",
            _ => throw new McpException("invalid_arguments: add needs key and body; edit needs id and body; delete needs id."),
        };
    }

    [McpServerTool(Name = "get_attachment", ReadOnly = true), Description(
        "An attachment referenced as `attachment:ID` in a description or comment: name, type, size and a download URL " +
        "valid for 5 minutes.")]
    [CoversOperations("getAttachment")]
    public async Task<AttachmentResponse> GetAttachment(
        [Description("Attachment id.")] Guid id, [Description(WorkspaceHelp)] string? workspace = null, CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Guest, ct);
        return Unwrap(await attachments.GetAsync(id, ct));
    }

    private static T Unwrap<T>(IssueResult<T> result) =>
        result.Error is { } error ? throw new McpException(Message(error)) : result.Value!;

    private static string Message(IssueError error) => $"{error.Code}: {error.Detail}";
}
