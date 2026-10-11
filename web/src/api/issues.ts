import { useMutation, useQuery, useQueryClient, type Query, type QueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useToast } from '../components/Toasts';
import {
  createIssueMutation,
  getIssueOptions,
  getIssueQueryKey,
  listIssueActivityQueryKey,
  listIssuesOptions,
  listMembersOptions,
  moveIssueMutation,
  updateIssueMutation,
} from './gen/@tanstack/react-query.gen';
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

type IssueFilters = NonNullable<ListIssuesData['query']>;

export function useIssues(workspace: string, query: IssueFilters) {
  return useQuery(listIssuesOptions({ path: { workspace }, query: { limit: 200, ...query } }));
}

export function useIssue(workspace: string, key: string | null) {
  return useQuery({ ...getIssueOptions({ path: { workspace, key: key ?? '' } }), enabled: !!key });
}

export function useMembers(workspace: string) {
  return useQuery({ ...listMembersOptions({ path: { workspace } }), staleTime: 60_000 });
}

const isIssueList = (q: Query) => (q.queryKey[0] as { _id?: string } | undefined)?._id === 'listIssues';

/** Snapshot of every cached issue list plus one detail, for rollback. */
type Snapshot = { lists: [readonly unknown[], IssuePage | undefined][]; detail?: IssueDetail; detailKey: readonly unknown[] };

async function snapshot(qc: QueryClient, workspace: string, key: string): Promise<Snapshot> {
  const detailKey = getIssueQueryKey({ path: { workspace, key } });
  await qc.cancelQueries({ predicate: isIssueList });
  await qc.cancelQueries({ queryKey: detailKey });
  return {
    lists: qc.getQueriesData<IssuePage>({ predicate: isIssueList }),
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
  for (const [queryKey, page] of snap.lists) {
    if (!page) continue;
    qc.setQueryData<IssuePage>(queryKey, { ...page, items: page.items.map((i) => (i.key === key ? patch(i) : i)) });
  }
  // The detail shares every summary field except parentKey, which a patch never changes.
  if (snap.detail) qc.setQueryData<IssueDetail>(snap.detailKey, { ...snap.detail, ...patch(snap.detail as unknown as IssueSummary) });
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
        key ? snap.lists.flatMap(([, page]) => page?.items ?? []).find((i) => i.key === key)?.rank : undefined;
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
