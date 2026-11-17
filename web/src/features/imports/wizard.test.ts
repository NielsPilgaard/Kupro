import { describe, expect, it } from 'vitest';
import type { ImportPlan, ImportRunResponse, MemberResponse } from '../../api/gen/types.gen';
import { blockerOf, delta, effortNeeded, formatBytes, matchOf, openDecisions, phaseOf, sparkBars, stepsFor } from './wizard';

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
});
