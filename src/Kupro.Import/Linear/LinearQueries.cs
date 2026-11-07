namespace Kupro.Import.Linear;

/// <summary>
/// The GraphQL the connector sends, as constants so a reviewer sees every field read. Linear rejects a query costing
/// over 10,000 complexity points (an object 1, a scalar 0.1, a connection times its <c>first</c>), so page sizes are
/// picked to stay well under it: about 7,000 for a page of issues with their nested history, relations and
/// attachments. Nested connections that can outgrow their page are paged on their own (<see cref="IssueHistory"/> …).
/// Filters are variables, built in code, so an absent filter is left out rather than sent as null.
/// </summary>
internal static class LinearQueries
{
    /// <summary>Issues per page. Each carries up to <see cref="Nested"/> relations and attachments and 20 history entries.</summary>
    public const int IssuePage = 50;

    /// <summary>Projects per page, with their teams, labels, initiatives and milestones.</summary>
    public const int ProjectPage = 25;

    /// <summary>Linear's largest page; used where the nodes are small.</summary>
    public const int MaxPage = 250;

    public const int Nested = 10;

    private const string PageInfo = "pageInfo { hasNextPage endCursor }";

    private const string HistoryFields =
        "id createdAt actorId fromStateId toStateId fromAssigneeId toAssigneeId fromPriority toPriority fromEstimate toEstimate "
        + "fromCycleId toCycleId fromProjectId toProjectId fromParentId toParentId fromTeamId toTeamId addedLabelIds removedLabelIds "
        + "fromTitle toTitle fromDueDate toDueDate updatedDescription";

    private const string RelationFields = "id type relatedIssue { id }";

    private const string AttachmentFields = "id url title subtitle sourceType createdAt creator { id }";

    public const string Organization = "query Organization { organization { id name urlKey } }";

    public const string Teams =
        "query Teams($after: String) { teams(first: 100, after: $after, includeArchived: true) { nodes { id key name archivedAt } " + PageInfo + " } }";

    public const string WorkflowStates =
        "query WorkflowStates($after: String) { workflowStates(first: 250, after: $after, includeArchived: true) { nodes { id name type position team { id } } " + PageInfo + " } }";

    public const string Users =
        "query Users($after: String) { users(first: 250, after: $after, includeDisabled: true) { nodes { id name displayName email active } " + PageInfo + " } }";

    public const string IssueLabels =
        "query IssueLabels($after: String) { issueLabels(first: 250, after: $after, includeArchived: true) { nodes { id name isGroup parent { id } team { id } } " + PageInfo + " } }";

    public const string ProjectStatuses =
        "query ProjectStatuses($after: String) { projectStatuses(first: 50, after: $after) { nodes { id name type position } " + PageInfo + " } }";

    public const string Cycles =
        "query Cycles($filter: CycleFilter, $after: String) { cycles(first: 250, after: $after, includeArchived: true, filter: $filter) { nodes { id number name startsAt endsAt completedAt } " + PageInfo + " } }";

    public const string Projects =
        "query Projects($filter: ProjectFilter, $after: String) { projects(first: 25, after: $after, includeArchived: true, filter: $filter) { nodes { "
        + "id name description content url createdAt updatedAt startDate targetDate completedAt canceledAt priority trashed "
        + "status { id } lead { id } creator { id } "
        + "teams(first: 25) { nodes { id } } labels(first: 25) { nodes { name } } initiatives(first: 25) { nodes { name } } "
        + "projectMilestones(first: 50) { nodes { id name targetDate status } } "
        + "} " + PageInfo + " } }";

    public const string Issues =
        "query Issues($filter: IssueFilter, $after: String) { issues(first: 50, after: $after, includeArchived: true, filter: $filter) { nodes { "
        + "id identifier title description priority estimate dueDate url createdAt updatedAt completedAt canceledAt trashed labelIds "
        + "team { id } state { id } assignee { id } creator { id } parent { id } project { id } projectMilestone { id } cycle { id } "
        + "relations(first: 10) { nodes { " + RelationFields + " } " + PageInfo + " } "
        + "attachments(first: 10) { nodes { " + AttachmentFields + " } " + PageInfo + " } "
        + "history(first: 20) { nodes { " + HistoryFields + " } " + PageInfo + " } "
        + "} " + PageInfo + " } }";

    public const string IssueHistory =
        "query IssueHistory($id: String!, $after: String) { issue(id: $id) { id history(first: 250, after: $after) { nodes { " + HistoryFields + " } " + PageInfo + " } } }";

    public const string IssueRelations =
        "query IssueRelations($id: String!, $after: String) { issue(id: $id) { id relations(first: 250, after: $after) { nodes { " + RelationFields + " } " + PageInfo + " } } }";

    public const string IssueAttachments =
        "query IssueAttachments($id: String!, $after: String) { issue(id: $id) { id attachments(first: 250, after: $after) { nodes { " + AttachmentFields + " } " + PageInfo + " } } }";

    /// <summary>Archived too: Linear archives an issue's comments with it, and closed issues are archived after a while.</summary>
    public const string Comments =
        "query Comments($filter: CommentFilter, $after: String) { comments(first: 250, after: $after, includeArchived: true, filter: $filter) { nodes { "
        + "id body createdAt updatedAt editedAt issueId parentId user { id } } " + PageInfo + " } }";
}
