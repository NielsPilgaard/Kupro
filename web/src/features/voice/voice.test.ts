import { describe, expect, it } from 'vitest';
import type { VoiceInterpretation, VoiceOperation } from '../../api/gen/types.gen';
import { appliesAtOnce, dictationField, insertAtCursor, replaceRange, routeFor, tidyDiff } from './voice';

const op = (extra: Partial<VoiceOperation> = {}): VoiceOperation => ({
  issue: { key: 'KUP-12', title: 'Login fails on Safari', type: 'task', state: 'todo' },
  candidates: [],
  operation: 'sprint',
  value: 's2',
  valueDisplay: 'Sprint 2',
  before: 'none',
  beforeDisplay: null,
  snippet: 'move KUP-12 to next sprint',
  confidence: 0.95,
  needsIssue: false,
  held: [],
  update: { sprint: 's2' },
  undo: { clear: ['sprint'] },
  create: null,
  ...extra,
});

const reading = (ops: VoiceOperation[]): VoiceInterpretation => ({
  kind: 'operations',
  text: 'move KUP-12 to next sprint',
  operations: ops,
  navigate: null,
  reason: null,
  extractor: 'model',
  budgetSpent: false,
});

describe('dictation', () => {
  it('inserts at the cursor with spaces where words would touch, and tells React', () => {
    const field = document.createElement('textarea');
    field.value = 'Reproduced.Safari too';
    field.setSelectionRange(11, 11);
    let changed = 0;
    field.addEventListener('input', () => changed++);

    const range = insertAtCursor(field, 'also on iOS');

    expect(field.value).toBe('Reproduced. also on iOS Safari too');
    expect(field.value.slice(range.start, range.end)).toBe('also on iOS');
    expect(changed).toBe(1);
  });

  it('replaces a selection, and leaves an empty field without padding', () => {
    const field = document.createElement('textarea');
    field.value = 'TODO';
    field.setSelectionRange(0, 4);
    insertAtCursor(field, 'Steps to reproduce');
    expect(field.value).toBe('Steps to reproduce');
  });

  it('tidy-up replaces only the dictated words, and nothing once they were edited', () => {
    const field = document.createElement('textarea');
    field.value = 'Note: ';
    field.setSelectionRange(6, 6);
    const range = insertAtCursor(field, 'the login page brakes on safari');

    expect(replaceRange(field, range, 'the login page brakes on safari', 'The login page breaks on Safari.')).toBe(true);
    expect(field.value).toBe('Note: The login page breaks on Safari.');
    expect(replaceRange(field, range, 'the login page brakes on safari', 'x')).toBe(false);
  });

  it('is chosen by the focused field, never by the voice controls themselves', () => {
    const input = document.createElement('input');
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    const ui = document.createElement('div');
    ui.setAttribute('data-voice-ui', '');
    const inside = document.createElement('textarea');
    ui.append(inside);
    document.body.append(ui);

    expect(dictationField(input)).toBe(input);
    expect(dictationField(checkbox)).toBeNull();
    expect(dictationField(inside)).toBeNull();
    expect(dictationField(document.body)).toBeNull();
  });

  it('shows tidy-up as a word diff', () => {
    expect(tidyDiff('we shud go', 'We should go.')).toEqual([
      { text: 'we', kind: 'removed' },
      { text: 'We', kind: 'added' },
      { text: ' ', kind: 'same' },
      { text: 'shud', kind: 'removed' },
      { text: 'should', kind: 'added' },
      { text: ' ', kind: 'same' },
      { text: 'go', kind: 'removed' },
      { text: 'go.', kind: 'added' },
    ]);
  });
});

describe('apply without asking', () => {
  it('applies at once only when the person allows it and every change is sure', () => {
    expect(appliesAtOnce(reading([op()]), true)).toBe(true);
    expect(appliesAtOnce(reading([op()]), false)).toBe(false);
    expect(appliesAtOnce(reading([op(), op({ held: ['destructive'] })]), true)).toBe(false);
    expect(appliesAtOnce(reading([op({ needsIssue: true, issue: null })]), true)).toBe(false);
    expect(appliesAtOnce(reading([]), true)).toBe(false);
  });
});

describe('navigation', () => {
  it('goes to structured places in the router, never a URL', () => {
    const target = { issue: null, project: null, assignee: null, sprint: null, sprintName: null };
    expect(routeFor('acme', { ...target, view: 'myIssues' })).toEqual({ to: '/w/$workspace/my-issues', params: { workspace: 'acme' } });
    expect(routeFor('acme', { ...target, view: 'board', project: 'KUP', assignee: { id: 'm1', name: 'Anna', kind: 'human' } })).toEqual({
      to: '/w/$workspace/p/$project',
      params: { workspace: 'acme', project: 'KUP' },
      search: { view: 'board', assignee: 'm1' },
    });
    const issue = routeFor('acme', { ...target, view: 'issue', issue: 'KUP-12' });
    expect(issue.to).toBe('.');
    expect((issue.search as (prev: Record<string, unknown>) => Record<string, unknown>)({ view: 'list' })).toEqual({ view: 'list', issue: 'KUP-12' });
  });
});
