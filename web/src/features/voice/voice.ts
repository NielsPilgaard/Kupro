import type { VoiceInterpretation, VoiceOperation, VoiceTarget } from '../../api/gen/types.gen';

/** Codes that turn the microphone off; typing still works (ADR-022 decision 7, ADR-013). */
export const blockingCodes = ['ai_budget_spent', 'ai_connection_rejected', 'transcription_unavailable', 'microphone'];

/** Proposals that may apply at once when the person allows it: sure, one issue, not cancelling, not new (task 036). */
export const isReady = (op: VoiceOperation) => op.held.length === 0 && !op.needsIssue;

/** With "apply without asking" on, a reading applies without the card only when every change is ready. */
export const appliesAtOnce = (reading: VoiceInterpretation, autoApply: boolean) =>
  autoApply && reading.kind === 'operations' && reading.operations.length > 0 && reading.operations.every(isReady);

/** A text field speech goes into: dictation is chosen by the focused field, never guessed (task 036). */
export type DictationField = HTMLTextAreaElement | HTMLInputElement;

export function dictationField(el: Element | null): DictationField | null {
  if (!el || el.closest('[data-voice-ui]')) return null;
  if (el instanceof HTMLTextAreaElement) return el.readOnly || el.disabled ? null : el;
  if (el instanceof HTMLInputElement && ['text', 'search', ''].includes(el.type)) return el.readOnly || el.disabled ? null : el;
  return null;
}

/**
 * Inserts text at the cursor (replacing a selection) with a space on either side where words would touch, and tells
 * React the value changed. Returns where the inserted words ended up, so "tidy up" can replace exactly them.
 */
export function insertAtCursor(field: DictationField, text: string): { start: number; end: number } {
  const start = field.selectionStart ?? field.value.length;
  const end = field.selectionEnd ?? start;
  const before = field.value.slice(0, start);
  const after = field.value.slice(end);
  const lead = before.length > 0 && !/\s$/.test(before) ? ' ' : '';
  const trail = after.length > 0 && !/^\s/.test(after) ? ' ' : '';
  field.setRangeText(lead + text + trail, start, end, 'end');
  field.dispatchEvent(new Event('input', { bubbles: true }));
  return { start: start + lead.length, end: start + lead.length + text.length };
}

/** Swaps the dictated words for the tidied ones if they are still there; false when the person changed them since. */
export function replaceRange(field: DictationField, range: { start: number; end: number }, from: string, to: string): boolean {
  if (field.value.slice(range.start, range.end) !== from) return false;
  field.setRangeText(to, range.start, range.end, 'end');
  field.dispatchEvent(new Event('input', { bubbles: true }));
  return true;
}

export interface DiffPart {
  text: string;
  kind: 'same' | 'removed' | 'added';
}

/** Word-level diff for the "tidy up" suggestion: tidy-up keeps the words, so pairing them in order is enough. */
export function tidyDiff(said: string, tidied: string): DiffPart[] {
  const a = said.split(/\s+/).filter(Boolean);
  const b = tidied.split(/\s+/).filter(Boolean);
  const parts: DiffPart[] = [];
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const x = a[i];
    const y = b[i];
    if (i > 0) parts.push({ text: ' ', kind: 'same' });
    if (x === y) parts.push({ text: x!, kind: 'same' });
    else {
      if (x) parts.push({ text: x, kind: 'removed' });
      if (y) parts.push({ text: y, kind: 'added' });
    }
  }
  return parts;
}

/** Where a navigation goes in the router. The server only returns places in this workspace. */
export function routeFor(workspace: string, target: VoiceTarget) {
  switch (target.view) {
    case 'issue':
      return { to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: target.issue! }) } as const;
    case 'myIssues':
      return { to: '/w/$workspace/my-issues', params: { workspace } } as const;
    case 'list':
    case 'board':
      return {
        to: '/w/$workspace/p/$project',
        params: { workspace, project: target.project! },
        search: { view: target.view, ...(target.assignee ? { assignee: target.assignee.id } : {}) },
      } as const;
    case 'sprints':
      return { to: '/w/$workspace/sprints', params: { workspace }, search: target.project ? { project: target.project } : {} } as const;
    case 'plan':
      return { to: '/w/$workspace/plan', params: { workspace }, search: target.project ? { project: target.project } : {} } as const;
  }
}
