import type { IssuePriority, IssueState, IssueType, ProjectColor } from '../api/gen/types.gen';

/** Fixed workflow order (ADR-008); board columns and list groups follow it. */
export const states: readonly IssueState[] = ['backlog', 'todo', 'inProgress', 'inReview', 'done', 'canceled'];
export const boardStates: readonly IssueState[] = ['backlog', 'todo', 'inProgress', 'inReview', 'done'];
export const priorities: readonly IssuePriority[] = ['urgent', 'high', 'medium', 'low', 'none'];
export const types: readonly IssueType[] = ['task', 'feature', 'epic'];

export const isClosed = (state: IssueState) => state === 'done' || state === 'canceled';

/** Same ordering the API uses: lower rank first, ties by key number. */
export function compareRank(a: { rank: number; key: string }, b: { rank: number; key: string }) {
  return a.rank - b.rank || keyNumber(a.key) - keyNumber(b.key);
}

export function keyNumber(key: string) {
  return Number(key.slice(key.lastIndexOf('-') + 1)) || 0;
}

export function projectKeyOf(issueKey: string) {
  return issueKey.slice(0, issueKey.lastIndexOf('-'));
}

/** Tailwind class for a project's colour token (no hex in components). */
export const projectColorClass: Record<ProjectColor, string> = {
  verdigris: 'bg-proj-verdigris',
  copper: 'bg-proj-copper',
  plum: 'bg-proj-plum',
  moss: 'bg-proj-moss',
  ochre: 'bg-proj-ochre',
  brick: 'bg-proj-brick',
  slate: 'bg-proj-slate',
  fjord: 'bg-proj-fjord',
};

/** The CLI command for an action, shown in the palette footer: parity made visible (design guide). */
export function cliFor(action: string, key?: string, value?: string) {
  const dashed = value?.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`);
  switch (action) {
    case 'state':
      return `kupro issue update ${key} --state ${dashed}`;
    case 'priority':
      return `kupro issue update ${key} --priority ${dashed}`;
    case 'assign':
      return `kupro issue update ${key} --assignee ${value ?? 'me'}`;
    case 'label':
      return `kupro issue update ${key} --add-label ${value}`;
    case 'create':
      return 'kupro issue create --project <KEY> --title "..."';
    case 'close':
      return `kupro issue close ${key}`;
    default:
      return 'kupro --help';
  }
}
