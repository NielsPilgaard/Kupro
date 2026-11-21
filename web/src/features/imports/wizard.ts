import type { ImportMapping, ImportPlan, ImportRunResponse, MemberResponse, PersonTarget, SourceColumn, SourceDescription, SourcePerson } from '../../api/gen/types.gen';

/** The guided steps of a new import (task 023, prototype 11). Hierarchy shows only for four-level sources. */
export type StepId = 'source' | 'projects' | 'columns' | 'hierarchy' | 'types' | 'states' | 'people' | 'fields' | 'effort' | 'review';

const allSteps: StepId[] = ['source', 'projects', 'columns', 'hierarchy', 'types', 'states', 'people', 'fields', 'effort', 'review'];

export function stepsFor(plan: ImportPlan | undefined, description?: SourceDescription): StepId[] {
  const columns = description?.columns?.some((c) => c.roles?.length);
  return allSteps.filter((s) => (s !== 'hierarchy' || plan?.fourLevels) && (s !== 'columns' || columns));
}

/** The roles a board column can fill, in the order the Columns step lists them. */
export const columnRoles = ['status', 'people', 'timeline', 'date', 'estimate', 'points'] as const;
export type ColumnRole = (typeof columnRoles)[number];

/** The column filling each role on a board: the owner's pick ("" for none), else the connector's. */
export function pickedColumns(description: SourceDescription | undefined, mapping: ImportMapping | undefined, project: string) {
  const roles: Partial<Record<ColumnRole, string>> = {};
  for (const c of description?.columns ?? []) if (c.project === project && c.role) roles[c.role as ColumnRole] = c.id;
  for (const [role, column] of Object.entries(mapping?.scope?.columns?.[project] ?? {})) {
    if (column) roles[role as ColumnRole] = column;
    else delete roles[role as ColumnRole];
  }
  return roles;
}

/** The boards the read covers: the picked ones, or all but archived ones. */
function boardsInScope(description: SourceDescription | undefined, mapping: ImportMapping | undefined) {
  const scope = mapping?.scope?.projects ?? [];
  return (description?.projects ?? []).filter((p) => (scope.length === 0 ? !p.archived : scope.includes(p.id)));
}

/** Columns Kupro wasn't sure of, on boards in scope, that the owner hasn't answered for yet. */
export function openQuestions(description: SourceDescription | undefined, mapping: ImportMapping | undefined): SourceColumn[] {
  const boards = new Set(boardsInScope(description, mapping).map((p) => p.id));
  return (description?.columns ?? []).filter((c) => c.ask && c.role && boards.has(c.project) && mapping?.scope?.columns?.[c.project]?.[c.role] === undefined);
}

/**
 * Which statuses and fields the picked columns give, as the snapshot keeps them: a status column not picked gives no
 * statuses, and a column filling a role is no field. Sources without columns keep everything.
 */
export function columnFilter(description: SourceDescription | undefined, mapping: ImportMapping | undefined) {
  const columns = description?.columns ?? [];
  const picked = new Map<string, Partial<Record<ColumnRole, string>>>();
  for (const c of columns) if (!picked.has(c.project)) picked.set(c.project, pickedColumns(description, mapping, c.project));
  const isPicked = (c: SourceColumn) => Object.values(picked.get(c.project) ?? {}).includes(c.id);
  const listed = new Set(columns.flatMap((c) => c.labels ?? []));
  const labels = new Set(columns.filter((c) => picked.get(c.project)?.status === c.id).flatMap((c) => c.labels ?? []));
  const columnFields = new Set(columns.flatMap((c) => (c.field ? [c.field] : [])));
  const fields = new Set(columns.flatMap((c) => (c.field && !isPicked(c) ? [c.field] : [])));
  return {
    status: (id: string) => !listed.has(id) || labels.has(id),
    field: (id: string) => !columnFields.has(id) || fields.has(id),
  };
}

/** Choices with no default: the owner makes them in this session, or made them before the first run. */
export interface Decisions {
  fold: boolean;
  effort: boolean;
}

export type Blocker = 'source' | 'fold' | 'effort' | 'projectKeys' | 'projectsMissing' | 'columns';

/**
 * A re-proposal after a CSV's columns change, laid over the owner's draft: values the new columns bring (types,
 * states, people, projects) come from the proposal, but each one the owner already mapped keeps their choice, and
 * every other choice (effort, scope, fold, fields) stays as it was.
 */
export function mergeProposal(current: ImportMapping, proposed: ImportMapping): ImportMapping {
  const review = proposed.statusesToReview ?? [];
  const states = { ...keepChosen(current.states, proposed.states) };
  for (const id of review) if (current.states?.[id]) states[id] = current.states[id];
  return {
    ...current,
    csv: proposed.csv,
    projects: keepChosen(current.projects, proposed.projects),
    people: keepChosen(current.people, proposed.people),
    types: keepChosen(current.types, proposed.types),
    priorities: keepChosen(current.priorities, proposed.priorities),
    states,
    statusesToReview: review.filter((id) => !current.states?.[id]),
  };
}

/** The proposal's keys (what the source holds now), each with the owner's value where they had one. */
function keepChosen<T>(chosen: Record<string, T> | undefined, proposed: Record<string, T> | undefined): Record<string, T> {
  return Object.fromEntries(Object.entries(proposed ?? {}).map(([id, value]) => [id, chosen?.[id] ?? value]));
}

/** A CSV without a points column has nothing to convert, so Effort needs no answer. */
export function effortNeeded(mapping: ImportMapping | undefined) {
  return !(mapping?.csv && !mapping.csv.points);
}

/** Why a step can't be left yet, or null. The footer shows it next to the disabled primary. */
export function blockerOf(
  step: StepId,
  ctx: { connected: boolean; mapping?: ImportMapping; plan?: ImportPlan; decided: Decisions; description?: SourceDescription },
): Blocker | null {
  switch (step) {
    case 'source':
      return ctx.connected ? null : 'source';
    case 'projects':
      if (ctx.plan?.projectKeysTaken?.length) return 'projectKeys';
      if (ctx.plan?.projectsMissing?.length) return 'projectsMissing';
      return null;
    case 'columns':
      return openQuestions(ctx.description, ctx.mapping).length ? 'columns' : null;
    case 'hierarchy':
      return ctx.decided.fold ? null : 'fold';
    case 'effort':
      return ctx.decided.effort || !effortNeeded(ctx.mapping) ? null : 'effort';
    default:
      return null;
  }
}

/** Steps that still need the owner, in step order: the review step and the rail list them as links. */
export function openDecisions(steps: StepId[], ctx: Parameters<typeof blockerOf>[1]) {
  return steps.filter((s) => s !== 'source' && blockerOf(s, ctx) !== null);
}

/** Change since the last edit for the rail's third column; null when unchanged or unknown. */
export function delta(current: number | undefined, previous: number | undefined) {
  if (current === undefined || previous === undefined || current === previous) return null;
  return current - previous;
}

/** One bar per commit run, oldest first: created, updated, and whether it failed (design guide: sparkline). */
export interface SparkBar {
  n: number;
  created: number;
  updated: number;
  failed: boolean;
}

export function sparkBars(runs: ImportRunResponse[], max = 14): SparkBar[] {
  return runs
    .filter((r) => r.kind === 'commit')
    .sort((a, b) => a.createdAt.localeCompare(b.createdAt))
    .map((r, i) => ({ n: i + 1, created: r.counts?.issues ?? 0, updated: r.counts?.updated ?? 0, failed: r.state === 'failed' }))
    .slice(-max);
}

export const runPhases = ['read', 'issues', 'comments', 'attachments', 'history'] as const;
export type RunPhase = (typeof runPhases)[number];

/** Where a commit is, from its counts: the pipeline writes issues, then comments, attachments and history. */
export function phaseOf(run: Pick<ImportRunResponse, 'state' | 'counts'>): RunPhase {
  const c = run.counts;
  if (!c || run.state === 'queued') return 'read';
  if (c.changes || c.sourceChanges) return 'history';
  if (c.attachments || c.attachmentsMissing) return 'attachments';
  if (c.comments) return 'comments';
  return c.issues || c.alreadyImported ? 'issues' : 'read';
}

export type Match = 'email' | 'name' | 'none' | 'bot';

/** How the proposed target was found, for the People step's chip. */
export function matchOf(person: SourcePerson, target: PersonTarget | undefined, members: MemberResponse[]): Match {
  if (person.bot) return 'bot';
  if (target?.action !== 'member') return 'none';
  const member = members.find((m) => m.id === target.memberId);
  const email = person.email?.toLowerCase();
  return email && member?.email?.toLowerCase() === email ? 'email' : 'name';
}

/** Counts of what the People step decided, for the rail and the review. */
export function peopleCounts(mapping: ImportMapping | undefined) {
  const targets = Object.values(mapping?.people ?? {});
  return {
    placeholders: targets.filter((p) => p.action === 'placeholder').length,
    invites: targets.filter((p) => p.action === 'invite').length,
    skipped: targets.filter((p) => p.action === 'skip').length,
  };
}

/** Bytes in the user's locale: "3.4 GB". */
export function formatBytes(bytes: number, locale: string) {
  const units = ['B', 'kB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit++;
  }
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: unit ? 1 : 0 }).format(value)} ${units[unit]}`;
}
