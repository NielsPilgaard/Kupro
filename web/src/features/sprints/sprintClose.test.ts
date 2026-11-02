import { describe, expect, it } from 'vitest';
import type { SprintReviewIssue } from '../../api/gen/types.gen';
import { canSplit, closeItems, closeTotals, daysBetween, hoursLeftOf, nextSprint, planIsValid, stepPath } from './sprintClose';

const issue = (key: string, effort: number | null, hasChildren = false): SprintReviewIssue => ({
  key,
  title: key,
  type: 'task',
  state: 'inProgress',
  assignee: null,
  effort,
  hasChildren,
  outcome: null,
  continuedIn: null,
});

describe('hoursLeftOf', () => {
  it.each([
    ['4', 4],
    ['4,5', 4.5],
    ['0.25', 0.25],
    ['', null],
    ['0', null],
    ['8', null],
    ['9', null],
    ['abc', null],
    ['1.555', null],
  ])('reads "%s" on an 8 h issue as %s', (text, expected) => {
    expect(hoursLeftOf({ effort: 8 }, { outcome: 'split', hoursLeft: text })).toBe(expected);
  });
});

describe('canSplit', () => {
  it('needs an effort and no children', () => {
    expect(canSplit(issue('A', 8))).toBe(true);
    expect(canSplit(issue('B', null))).toBe(false);
    expect(canSplit(issue('C', 8, true))).toBe(false);
  });
});

describe('the close plan', () => {
  const issues = [issue('A', 8), issue('B', 4), issue('C', 2), issue('D', null)];
  const plans = {
    B: { outcome: 'split' as const, hoursLeft: '1' },
    C: { outcome: 'backlog' as const, hoursLeft: '' },
    D: { outcome: 'done' as const, hoursLeft: '' },
  };

  it('counts rows by outcome, with unlisted rows carrying', () => {
    const totals = closeTotals(issues, plans);
    expect(totals.carry).toEqual({ count: 1, hours: 8 });
    expect(totals.split).toEqual({ count: 1, hoursDone: 3 });
    expect(totals.backlog).toEqual({ count: 1, hours: 2 });
    expect(totals.done).toEqual({ count: 1, hours: 0 });
  });

  it('sends every row as an item', () => {
    expect(closeItems(issues, plans)).toEqual([
      { issue: 'A', outcome: 'carry' },
      { issue: 'B', outcome: 'split', hoursLeft: 1 },
      { issue: 'C', outcome: 'backlog' },
      { issue: 'D', outcome: 'done' },
    ]);
  });

  it('is invalid while a split row has no valid hours left', () => {
    expect(planIsValid(issues, plans)).toBe(true);
    expect(planIsValid(issues, { B: { outcome: 'split', hoursLeft: '' } })).toBe(false);
    expect(planIsValid(issues, { B: { outcome: 'split', hoursLeft: '4' } })).toBe(false);
  });
});

describe('nextSprint', () => {
  it('keeps the length and starts the day after', () => {
    expect(nextSprint({ name: 'Sprint 14', startDate: '2027-01-04', endDate: '2027-01-15' })).toEqual({
      name: 'Sprint 15',
      startDate: '2027-01-16',
      endDate: '2027-01-27',
    });
  });

  it('crosses a month and numbers a name without one', () => {
    expect(nextSprint({ name: 'Autumn', startDate: '2027-01-25', endDate: '2027-01-31' })).toEqual({
      name: 'Autumn 2',
      startDate: '2027-02-01',
      endDate: '2027-02-07',
    });
  });
});

describe('daysBetween', () => {
  it('counts across daylight saving and months', () => {
    expect(daysBetween('2027-03-27', '2027-03-29')).toBe(2);
    expect(daysBetween('2027-01-31', '2027-02-01')).toBe(1);
  });
});

describe('stepPath', () => {
  const x = (edge: number) => edge * 10;
  const y = (hours: number) => hours;

  it('holds each value to the end of its day and steps there', () => {
    expect(stepPath([8, 6, 6], x, y)).toBe('M0 8H10H20V6H30V6');
  });

  it('starts at the first day with a value and stops at the last', () => {
    expect(stepPath([null, 4, 2, null], x, y)).toBe('M10 4H20H30V2');
  });

  it('draws nothing without values', () => {
    expect(stepPath([null, null], x, y)).toBe('');
  });
});
