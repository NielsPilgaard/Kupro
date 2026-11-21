import { describe, expect, it } from 'vitest';
import type { ImportMapping, ImportPlan, ImportRunResponse, MemberResponse, SourceDescription } from '../../api/gen/types.gen';
import {
  blockerOf,
  columnFilter,
  delta,
  effortNeeded,
  formatBytes,
  matchOf,
  mergeProposal,
  openDecisions,
  openQuestions,
  phaseOf,
  pickedColumns,
  sparkBars,
  stepsFor,
} from './wizard';

const count = (n: number) => ({ count: n, samples: [] });
const plan = (over: Partial<ImportPlan> = {}): ImportPlan => ({
  items: 10,
  create: count(10),
  update: count(0),
  skippedByScope: count(0),
  reparented: count(0),
  promoted: count(0),
  foldedChecklists: count(0),
  statusesToReview: count(0),
  parentLoops: count(0),
  comments: 0,
  attachments: 0,
  attachmentBytes: 0,
  attachmentsOfUnknownSize: 0,
  changes: 0,
  effortHours: 0,
  fourLevels: false,
  ...over,
});
const none = { fold: false, effort: false };

describe('import wizard steps', () => {
  it('shows Hierarchy only for a four-level source', () => {
    expect(stepsFor(plan())).not.toContain('hierarchy');
    expect(stepsFor(plan({ fourLevels: true }))).toContain('hierarchy');
  });

  it('blocks the fold and effort until the owner chooses; neither has a default', () => {
    const ctx = { connected: true, mapping: {}, plan: plan({ fourLevels: true }), decided: none };
    expect(blockerOf('hierarchy', ctx)).toBe('fold');
    expect(blockerOf('effort', ctx)).toBe('effort');
    expect(openDecisions(stepsFor(ctx.plan), ctx)).toEqual(['hierarchy', 'effort']);
    expect(openDecisions(stepsFor(ctx.plan), { ...ctx, decided: { fold: true, effort: true } })).toEqual([]);
  });

  it('needs no effort answer for a CSV without a points column', () => {
    expect(effortNeeded({ csv: { title: 'Title' } })).toBe(false);
    expect(effortNeeded({ csv: { points: 'Story Points' } })).toBe(true);
    expect(blockerOf('effort', { connected: true, mapping: { csv: {} }, decided: none })).toBeNull();
  });

  it('blocks Projects while a key is taken or a target is missing', () => {
    const decided = { fold: true, effort: true };
    expect(blockerOf('projects', { connected: true, plan: plan({ projectKeysTaken: ['WEB'] }), decided })).toBe('projectKeys');
    expect(blockerOf('projects', { connected: true, plan: plan({ projectsMissing: ['OPS'] }), decided })).toBe('projectsMissing');
    expect(blockerOf('source', { connected: false, decided })).toBe('source');
  });
});

describe('import wizard helpers', () => {
  it('reports a change only when both values are known and differ', () => {
    expect(delta(12, 10)).toBe(2);
    expect(delta(10, 10)).toBeNull();
    expect(delta(10, undefined)).toBeNull();
  });

  it('builds one sparkline bar per commit, oldest first', () => {
    const run = (createdAt: string, kind: 'commit' | 'dryRun', state: ImportRunResponse['state'], issues = 0): ImportRunResponse => ({
      id: createdAt,
      kind,
      state,
      counts: { issues, updated: 1 },
      error: null,
      createdAt,
      startedAt: null,
      completedAt: null,
    });
    const bars = sparkBars([run('2026-11-02', 'commit', 'failed'), run('2026-11-01', 'commit', 'succeeded', 40), run('2026-11-03', 'dryRun', 'succeeded')]);
    expect(bars).toEqual([
      { n: 1, created: 40, updated: 1, failed: false },
      { n: 2, created: 0, updated: 1, failed: true },
    ]);
  });

  it('places a commit in its phase from its counts', () => {
    expect(phaseOf({ state: 'queued', counts: null })).toBe('read');
    expect(phaseOf({ state: 'running', counts: { issues: 300 } })).toBe('issues');
    expect(phaseOf({ state: 'running', counts: { issues: 500, comments: 20 } })).toBe('comments');
    expect(phaseOf({ state: 'running', counts: { issues: 500, comments: 20, changes: 4 } })).toBe('history');
  });

  it('tells an email match from a name match', () => {
    const member = { id: 'm1', email: 'mette@contoso.dk' } as MemberResponse;
    const target = { action: 'member' as const, memberId: 'm1' };
    expect(matchOf({ id: 'p', name: 'Mette', email: 'Mette@contoso.dk' }, target, [member])).toBe('email');
    expect(matchOf({ id: 'p', name: 'Mette', email: 'mette@other.dk' }, target, [member])).toBe('name');
    expect(matchOf({ id: 'p', name: 'Build', bot: true }, target, [member])).toBe('bot');
    expect(matchOf({ id: 'p', name: 'Anna' }, { action: 'placeholder' }, [member])).toBe('none');
  });

  it('formats bytes in the locale', () => {
    expect(formatBytes(3_400_000_000, 'en')).toBe('3.4 GB');
    expect(formatBytes(3_400_000_000, 'da')).toBe('3,4 GB');
    expect(formatBytes(512, 'en')).toBe('512 B');
  });

  it("keeps the owner's choices when changed CSV columns are re-proposed", () => {
    const current = {
      csv: { title: 'Name', type: 'Kind' },
      types: { Bug: { type: 'bug' }, Chore: { type: 'task' } },
      states: { Open: 'todo', Weird: 'inProgress' },
      statusesToReview: [],
      hoursPerPoint: 6,
    } as unknown as ImportMapping;
    const proposed = {
      csv: { title: 'Name', type: 'Type' },
      types: { Bug: { type: 'task' }, Story: { type: 'story' } },
      states: { Open: 'backlog' },
      statusesToReview: ['Weird', 'New'],
      hoursPerPoint: null,
    } as unknown as ImportMapping;
    const merged = mergeProposal(current, proposed);
    expect(merged.csv).toEqual(proposed.csv);
    expect(merged.types).toEqual({ Bug: { type: 'bug' }, Story: { type: 'story' } });
    expect(merged.states).toEqual({ Open: 'todo', Weird: 'inProgress' });
    expect(merged.statusesToReview).toEqual(['New']);
    expect(merged.hoursPerPoint).toBe(6);
  });
});

describe('board columns', () => {
  // Bugs: Severity and Workflow both fit the status, so Kupro asks; Hours is the estimate by name.
  const description: SourceDescription = {
    source: { kind: 'monday' },
    projects: [{ id: 'b', name: 'Bugs' }, { id: 'w', name: 'Website' }, { id: 'o', name: 'Old', archived: true }],
    statuses: [{ id: '(none)', name: 'No status' }, { id: 'Critical', name: 'Critical' }, { id: 'New', name: 'New' }],
    fields: [{ id: 'severity:select', name: 'Severity', kind: 'select' }, { id: 'workflow:select', name: 'Workflow', kind: 'select' }, { id: 'notes:text', name: 'Notes', kind: 'text' }],
    columns: [
      { project: 'b', id: 'severity', name: 'Severity', roles: ['status'], role: 'status', ask: true, labels: ['Critical'], field: 'severity:select' },
      { project: 'b', id: 'workflow', name: 'Workflow', roles: ['status'], labels: ['New'], field: 'workflow:select' },
      { project: 'b', id: 'hours', name: 'Hours', roles: ['estimate', 'points'], role: 'estimate' },
      { project: 'o', id: 'stage', name: 'Stage', roles: ['status'], role: 'status', ask: true },
    ],
  };
  const answered = (column: string): ImportMapping => ({ scope: { columns: { b: { status: column } } } });

  it('shows Columns only for a source with board columns', () => {
    expect(stepsFor(plan(), description)).toContain('columns');
    expect(stepsFor(plan(), { ...description, columns: [] })).not.toContain('columns');
  });

  it('waits on Columns until every question on a board in scope is answered', () => {
    const ctx = { connected: true, decided: none, description };
    expect(openQuestions(description, {}).map((c) => c.id)).toEqual(['severity']);
    expect(openQuestions(description, { scope: { projects: ['o'] } }).map((c) => c.id)).toEqual(['stage']);
    expect(blockerOf('columns', { ...ctx, mapping: {} })).toBe('columns');
    expect(blockerOf('columns', { ...ctx, mapping: answered('workflow') })).toBeNull();
    expect(blockerOf('columns', { ...ctx, mapping: answered('') })).toBeNull();
    expect(blockerOf('columns', { ...ctx, mapping: { scope: { projects: ['w'] } } })).toBeNull();
  });

  it("lists only the picked status column's labels, and no field for a column filling a role", () => {
    const guessed = columnFilter(description, {});
    expect(['(none)', 'Critical', 'New'].filter(guessed.status)).toEqual(['(none)', 'Critical']);
    expect(['severity:select', 'workflow:select', 'notes:text'].filter(guessed.field)).toEqual(['workflow:select', 'notes:text']);

    const picked = columnFilter(description, answered('workflow'));
    expect(['(none)', 'Critical', 'New'].filter(picked.status)).toEqual(['(none)', 'New']);
    expect(['severity:select', 'workflow:select'].filter(picked.field)).toEqual(['severity:select']);
    expect(pickedColumns(description, answered(''), 'b')).toEqual({ estimate: 'hours' });
  });
});
