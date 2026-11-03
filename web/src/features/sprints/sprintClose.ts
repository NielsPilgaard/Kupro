import type { BurndownPoint, CloseOutcome, CloseSprintItem, SprintReviewIssue } from '../../api/gen/types.gen';

/** One row's choice in the close review. The hours left stay text until the user has typed a number (ADR-020 OQ-1). */
export interface RowPlan {
  outcome: CloseOutcome;
  hoursLeft: string;
}

export const defaultPlan: RowPlan = { outcome: 'carry', hoursLeft: '' };

const parseHours = (text: string) => {
  const value = Number(text.trim().replace(',', '.'));
  return text.trim() !== '' && Number.isFinite(value) ? value : null;
};

/** Mirrors the API's split rules, so the Close button can be disabled before a request fails. */
export function hoursLeftOf(issue: Pick<SprintReviewIssue, 'effort'>, plan: RowPlan): number | null {
  const left = parseHours(plan.hoursLeft);
  const effort = issue.effort ?? 0;
  return left !== null && left > 0 && left < effort && Math.round(left * 100) / 100 === left ? left : null;
}

export const canSplit = (issue: Pick<SprintReviewIssue, 'effort' | 'hasChildren'>) => !!issue.effort && !issue.hasChildren;

/** What the sticky footer counts. */
export function closeTotals(issues: SprintReviewIssue[], plans: Record<string, RowPlan>) {
  const totals = { carry: { count: 0, hours: 0 }, split: { count: 0, hoursDone: 0 }, backlog: { count: 0, hours: 0 }, cancel: { count: 0, hours: 0 }, done: { count: 0, hours: 0 } };
  for (const issue of issues) {
    const plan = plans[issue.key] ?? defaultPlan;
    const effort = issue.effort ?? 0;
    if (plan.outcome === 'split') {
      totals.split.count++;
      const left = hoursLeftOf(issue, plan);
      if (left !== null) totals.split.hoursDone += effort - left;
    } else {
      totals[plan.outcome].count++;
      totals[plan.outcome].hours += effort;
    }
  }

  return totals;
}

export function closeItems(issues: SprintReviewIssue[], plans: Record<string, RowPlan>): CloseSprintItem[] {
  return issues.map((issue) => {
    const plan = plans[issue.key] ?? defaultPlan;
    return plan.outcome === 'split'
      ? { issue: issue.key, outcome: 'split', hoursLeft: hoursLeftOf(issue, plan) }
      : { issue: issue.key, outcome: plan.outcome };
  });
}

/** A split row without valid hours left blocks the close. */
export const planIsValid = (issues: SprintReviewIssue[], plans: Record<string, RowPlan>) =>
  issues.every((issue) => (plans[issue.key]?.outcome ?? 'carry') !== 'split' || (canSplit(issue) && hoursLeftOf(issue, plans[issue.key] ?? defaultPlan) !== null));

const addDays = (iso: string, days: number) => {
  const date = new Date(`${iso}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
};

const dayNumber = (iso: string) => Date.UTC(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10))) / 86_400_000;

/** Whole days from one ISO date to the next. */
export const daysBetween = (from: string, to: string) => dayNumber(to) - dayNumber(from);

/** Same length, starting the day after this one ends; "Sprint 14" becomes "Sprint 15". */
export function nextSprint(sprint: { name: string; startDate: string; endDate: string }) {
  const startDate = addDays(sprint.endDate, 1);
  const number = /^(.*?)(\d+)\s*$/.exec(sprint.name);
  return {
    name: number ? `${number[1]}${Number(number[2]) + 1}` : `${sprint.name} 2`,
    startDate,
    endDate: addDays(startDate, daysBetween(sprint.startDate, sprint.endDate)),
  };
}

export const isWeekend = (iso: string) => {
  const day = new Date(`${iso}T00:00:00Z`).getUTCDay();
  return day === 0 || day === 6;
};

/**
 * SVG path of a step line: each day holds the previous value and steps to its own at the day's end, since a point
 * is the value as of the end of that day. Days without a value (not started, still to come) leave the line off.
 */
export function stepPath(values: Array<number | null | undefined>, x: (dayEdge: number) => number, y: (hours: number) => number) {
  let path = '';
  let previous: number | null = null;
  values.forEach((value, day) => {
    if (value == null) return;
    path += previous === null ? `M${x(day)} ${y(value)}` : `H${x(day + 1)}V${y(value)}`;
    if (previous === null) path += `H${x(day + 1)}`;
    previous = value;
  });
  return path;
}

/** Highest value any line reaches; shared across small multiples so their heights compare. */
export const highest = (series: BurndownPoint[][]) =>
  Math.max(1, ...series.flat().flatMap((p) => [p.scope ?? 0, p.remaining ?? 0]));
