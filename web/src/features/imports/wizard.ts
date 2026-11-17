import type { ImportMapping, ImportPlan, ImportRunResponse, MemberResponse, PersonTarget, SourcePerson } from '../../api/gen/types.gen';

/** The guided steps of a new import (task 023, prototype 11). Hierarchy shows only for four-level sources. */
export type StepId = 'source' | 'projects' | 'hierarchy' | 'types' | 'states' | 'people' | 'fields' | 'effort' | 'review';

const allSteps: StepId[] = ['source', 'projects', 'hierarchy', 'types', 'states', 'people', 'fields', 'effort', 'review'];

export function stepsFor(plan: ImportPlan | undefined): StepId[] {
  return plan?.fourLevels ? allSteps : allSteps.filter((s) => s !== 'hierarchy');
}

/** Choices with no default: the owner makes them in this session, or made them before the first run. */
export interface Decisions {
  fold: boolean;
  effort: boolean;
}

export type Blocker = 'source' | 'fold' | 'effort' | 'projectKeys' | 'projectsMissing';

/** A CSV without a points column has nothing to convert, so Effort needs no answer. */
export function effortNeeded(mapping: ImportMapping | undefined) {
  return !(mapping?.csv && !mapping.csv.points);
}

/** Why a step can't be left yet, or null. The footer shows it next to the disabled primary. */
export function blockerOf(
  step: StepId,
  ctx: { connected: boolean; mapping?: ImportMapping; plan?: ImportPlan; decided: Decisions },
): Blocker | null {
  switch (step) {
    case 'source':
      return ctx.connected ? null : 'source';
    case 'projects':
      if (ctx.plan?.projectKeysTaken?.length) return 'projectKeys';
      if (ctx.plan?.projectsMissing?.length) return 'projectsMissing';
      return null;
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
