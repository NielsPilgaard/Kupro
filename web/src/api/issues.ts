import {
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
  type InfiniteData,
  type Query,
  type QueryClient,
  type QueryKey,
} from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useToast } from '../components/Toasts';
import { projectKeyOf } from '../lib/issues';
import {
  createIssueMutation,
  getIssueOptions,
  getIssueQueryKey,
  listIssueActivityQueryKey,
  listIssuesInfiniteOptions,
  listIssuesOptions,
  listMembersOptions,
  moveIssueMutation,
  updateIssueMutation,
} from './gen/@tanstack/react-query.gen';
import { listIssues } from './gen/sdk.gen';
import type {
  IssueDetail,
  IssuePage,
  IssueState,
  IssueSummary,
  ListIssuesData,
  MemberRef,
  MoveIssueRequest,
  UpdateIssueRequest,
} from './gen/types.gen';

export type IssueFilters = NonNullable<ListIssuesData['query']>;

export function useIssues(workspace: string, query: IssueFilters) {
  return useQuery(listIssuesOptions({ path: { workspace }, query: { limit: 200, ...query } }));
}

/**
 * Every page, for views whose totals must cover all matches (My Issues). Same key and shape as a single page, so list
 * invalidation and optimistic updates apply unchanged.
 */
export function useAllIssues(workspace: string, query: IssueFilters) {
  const options = { path: { workspace }, query: { limit: 200, ...query } };
  return useQuery({
    ...listIssuesOptions(options),
    queryFn: async ({ signal }): Promise<IssuePage> => {
      const items: IssueSummary[] = [];
      let cursor: string | undefined;
      do {
        const { data } = await listIssues({ ...options, query: { ...options.query, cursor }, signal, throwOnError: true });
        items.push(...data.items);
        cursor = data.nextCursor ?? undefined;
      } while (cursor);
      return { items, nextCursor: null };
    },
  });
}

/** Options for one state's issues, a page at a time; also the key a handler reads the loaded pages back from. */
export const stateIssuesOptions = (workspace: string, query: IssueFilters, state: IssueState) => ({
  ...listIssuesInfiniteOptions({ path: { workspace }, query: { limit: 50, ...query, state: [state] } }),
  // An object page param merges into the query; the first page simply has no cursor.
  initialPageParam: { path: { workspace }, query: {} },
  getNextPageParam: (last: IssuePage) => last.nextCursor ?? undefined,
});

/** Board columns and list groups page per state, so each one is complete up to where it has scrolled. */
export function useStateIssues(workspace: string, query: IssueFilters, state: IssueState) {
  return useInfiniteQuery(stateIssuesOptions(workspace, query, state));
}

/** Loaded items of a state's pages, read outside render (drag and keyboard handlers). */
export function loadedStateIssues(qc: QueryClient, workspace: string, query: IssueFilters, state: IssueState) {
  return listItems(qc.getQueryData(stateIssuesOptions(workspace, query, state).queryKey)).filter((i) => i.state === state);
}

export function useIssue(workspace: string, key: string | null) {
  return useQuery({ ...getIssueOptions({ path: { workspace, key: key ?? '' } }), enabled: !!key });
}

export function useMembers(workspace: string) {
  return useQuery({ ...listMembersOptions({ path: { workspace } }), staleTime: 60_000 });
}

export const isIssueList = (q: Query) => (q.queryKey[0] as { _id?: string } | undefined)?._id === 'listIssues';

/** A cached issue list: one page (`useIssues`) or the loaded pages of an infinite list (`useStateIssues`). */
export type IssueList = IssuePage | InfiniteData<IssuePage>;

export const listItems = (list: IssueList | undefined): IssueSummary[] =>
  !list ? [] : 'pages' in list ? list.pages.flatMap((p) => p.items) : list.items;

export function mapList(list: IssueList, map: (items: IssueSummary[]) => IssueSummary[]): IssueList {
  return 'pages' in list ? { ...list, pages: list.pages.map((p) => ({ ...p, items: map(p.items) })) } : { ...list, items: map(list.items) };
}

/** Snapshot of every cached issue list plus one detail, for rollback. */
type Snapshot = { lists: [QueryKey, IssueList | undefined][]; detail?: IssueDetail; detailKey: readonly unknown[] };

async function snapshot(qc: QueryClient, workspace: string, key: string): Promise<Snapshot> {
  const detailKey = getIssueQueryKey({ path: { workspace, key } });
  await qc.cancelQueries({ predicate: isIssueList });
  await qc.cancelQueries({ queryKey: detailKey });
  return {
    lists: qc.getQueriesData<IssueList>({ predicate: isIssueList }),
    detail: qc.getQueryData<IssueDetail>(detailKey),
    detailKey,
  };
}

function restore(qc: QueryClient, snap: Snapshot | undefined) {
  if (!snap) return;
  for (const [key, data] of snap.lists) qc.setQueryData(key, data);
  qc.setQueryData(snap.detailKey, snap.detail);
}

/** Applies a change to every cached copy of an issue, so the new state appears in the same frame (ADR-003). */
function patchEverywhere(qc: QueryClient, snap: Snapshot, key: string, patch: (i: IssueSummary) => IssueSummary) {
  const original = snap.lists.flatMap(([, list]) => listItems(list)).find((i) => i.key === key);
  const patched = original && patch(original);
  for (const [queryKey, list] of snap.lists) {
    if (!list) continue;
    const filter = (queryKey[0] as { query?: IssueFilters }).query ?? {};
    qc.setQueryData<IssueList>(queryKey, place(mapList(list, (items) => items.map((i) => (i.key === key ? patch(i) : i))), filter, patched));
  }
  // The detail shares every summary field except parentKey, which a patch never changes.
  if (snap.detail) qc.setQueryData<IssueDetail>(snap.detailKey, { ...snap.detail, ...patch(snap.detail as unknown as IssueSummary) });
}

/** Filters a state-filtered list can check on a summary; a list with any other filter waits for the refetch instead. */
const checkable = new Set(['state', 'project', 'sprint', 'sort', 'limit']);

/** A state change moves the issue between state-filtered lists (board columns, list groups) in the same frame. */
function place(list: IssueList, filter: IssueFilters, issue: IssueSummary | undefined): IssueList {
  if (!issue || !filter.state) return list;
  const fits = filter.state.includes(issue.state);
  const present = listItems(list).some((i) => i.key === issue.key);
  if (present && !fits) return mapList(list, (items) => items.filter((i) => i.key !== issue.key));
  if (present || !fits || !Object.keys(filter).every((k) => checkable.has(k))) return list;
  if (filter.project && !filter.project.includes(projectKeyOf(issue.key))) return list;
  if (filter.sprint && filter.sprint !== issue.sprintId) return list;
  // Prepending to the first page is enough: columns and groups sort what they show.
  return 'pages' in list
    ? { ...list, pages: list.pages.map((p, i) => (i === 0 ? { ...p, items: [issue, ...p.items] } : p)) }
    : { ...list, items: [issue, ...list.items] };
}

function applyChanges(issue: IssueSummary, c: UpdateIssueRequest, members: MemberRef[] | undefined, me: string | undefined): IssueSummary {
  const clear = new Set(c.clear ?? []);
  let labels = c.labels ?? issue.labels;
  if (c.addLabels) labels = [...new Set([...labels, ...c.addLabels.map((l) => l.trim().toLowerCase())])];
  if (c.removeLabels) labels = labels.filter((l) => !c.removeLabels!.includes(l));
  const assigneeId = c.assignee === 'me' ? me : c.assignee;
  return {
    ...issue,
    title: c.title ?? issue.title,
    type: c.type ?? issue.type,
    state: c.state ?? issue.state,
    priority: c.priority ?? issue.priority,
    effort: clear.has('effort') ? null : (c.effort ?? issue.effort),
    startDate: clear.has('startDate') ? null : (c.startDate ?? issue.startDate),
    dueDate: clear.has('dueDate') ? null : (c.dueDate ?? issue.dueDate),
    assignee: clear.has('assignee')
      ? null
      : assigneeId
        ? (members?.find((m) => m.id === assigneeId) ?? issue.assignee)
        : issue.assignee,
    labels: [...labels].sort(),
  };
}

export function useUpdateIssue(workspace: string, me?: string) {
  const qc = useQueryClient();
  const toast = useToast();
  const { t } = useTranslation();
  const { data: members } = useMembers(workspace);
  const base = updateIssueMutation();
  return useMutation({
    ...base,
    onMutate: async (vars) => {
      const key = vars.path.key;
      const snap = await snapshot(qc, workspace, key);
      const refs = members?.map((m) => ({ id: m.id, name: m.name, kind: m.kind }));
      patchEverywhere(qc, snap, key, (i) => applyChanges(i, vars.body, refs, me));
      return snap;
    },
    onError: (_error, vars, snap) => {
      restore(qc, snap);
      toast.error(t('errors.updateFailed', { key: vars.path.key }));
    },
    onSettled: (_data, _error, vars) => {
      void qc.invalidateQueries({ predicate: isIssueList });
      void qc.invalidateQueries({ queryKey: getIssueQueryKey({ path: { workspace, key: vars.path.key } }) });
      void qc.invalidateQueries({ queryKey: listIssueActivityQueryKey({ path: { workspace, key: vars.path.key } }) });
    },
  });
}

/** Board move: the card lands in its new column and slot immediately; rank uses the server's midpoint rule. */
export function useMoveIssue(workspace: string) {
  const qc = useQueryClient();
  const toast = useToast();
  const { t } = useTranslation();
  return useMutation({
    ...moveIssueMutation(),
    onMutate: async (vars: { path: { workspace: string; key: string }; body: MoveIssueRequest }) => {
      const snap = await snapshot(qc, workspace, vars.path.key);
      const rankOf = (key: string | null | undefined) =>
        key ? snap.lists.flatMap(([, list]) => listItems(list)).find((i) => i.key === key)?.rank : undefined;
      const a = rankOf(vars.body.after);
      const b = rankOf(vars.body.before);
      patchEverywhere(qc, snap, vars.path.key, (i) => ({
        ...i,
        state: (vars.body.state ?? i.state) as IssueState,
        rank: a !== undefined && b !== undefined ? (a + b) / 2 : a !== undefined ? a + 1 : b !== undefined ? b - 1 : i.rank,
      }));
      return snap;
    },
    onError: (_e, vars, snap) => {
      restore(qc, snap);
      toast.error(t('errors.moveFailed', { key: vars.path.key }));
    },
    onSettled: () => void qc.invalidateQueries({ predicate: isIssueList }),
  });
}

export function useCreateIssue(workspace: string) {
  const qc = useQueryClient();
  const toast = useToast();
  const { t } = useTranslation();
  return useMutation({
    ...createIssueMutation(),
    onSuccess: (issue) => {
      toast.info(t('issue.created', { key: issue.key }));
      void qc.invalidateQueries({ predicate: isIssueList });
    },
    onError: () => toast.error(t('errors.createFailed')),
    meta: { workspace },
  });
}
