import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { VoiceInterpretation, VoiceOperation } from '../../api/gen/types.gen';
import i18n from '../../i18n';
import { VoiceCard, type CardState } from './VoiceCard';

const sprint: VoiceOperation = {
  issue: { key: 'KUP-12', title: 'Login fails on Safari', type: 'task', state: 'todo' },
  candidates: [],
  operation: 'sprint',
  value: 's2',
  valueDisplay: 'Sprint 25',
  before: 's1',
  beforeDisplay: 'Sprint 24',
  snippet: null,
  confidence: 0.96,
  needsIssue: false,
  held: [],
  update: { sprint: 's2' },
  undo: { sprint: 's1' },
  create: null,
};

const vague: VoiceOperation = {
  ...sprint,
  issue: null,
  operation: 'state',
  value: 'done',
  valueDisplay: 'done',
  before: null,
  beforeDisplay: null,
  confidence: 0.5,
  needsIssue: true,
  held: ['lowConfidence', 'ambiguous'],
  candidates: [
    { key: 'KUP-12', title: 'Login fails on Safari', type: 'task', state: 'todo' },
    { key: 'KUP-57', title: 'Login page copy is outdated', type: 'task', state: 'backlog' },
  ],
  update: { state: 'done' },
  undo: null,
};

const reading = (operations: VoiceOperation[], extra: Partial<VoiceInterpretation> = {}): CardState => ({
  kind: 'reading',
  reading: { kind: 'operations', text: 'move KUP-12 to next sprint', operations, navigate: null, reason: null, extractor: 'model', budgetSpent: false, ...extra },
});

function show(state: CardState, variant: 'card' | 'sheet' = 'card') {
  const handlers = { onApply: vi.fn(), onType: vi.fn(), onTidy: vi.fn(), onClose: vi.fn() };
  render(<VoiceCard state={state} variant={variant} {...handlers} />);
  return handlers;
}

describe('VoiceCard', () => {
  beforeEach(() => void i18n.changeLanguage('en'));
  afterEach(cleanup);

  it('shows the diff and applies with Enter, since Apply has focus', () => {
    const { onApply } = show(reading([sprint]));

    expect(screen.getByText('Sprint 24')).toHaveClass('line-through');
    expect(screen.getByText('Sprint 25')).toBeInTheDocument();
    const apply = screen.getByRole('button', { name: 'Apply 1 change' });
    expect(apply).toHaveFocus();
    fireEvent.click(apply);
    expect(onApply).toHaveBeenCalledWith([{ op: sprint, key: 'KUP-12' }]);
  });

  it('says why a change waits, and an unclear issue needs one picked before it applies', () => {
    const { onApply } = show(reading([vague], { text: 'the login bug is done' }));

    expect(screen.getByText('Not sure I heard right')).toBeInTheDocument();
    expect(screen.getByText('Which issue?')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply 0 changes' })).toBeDisabled();
    fireEvent.click(screen.getByRole('radio', { name: /KUP-57/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Apply 1 change' }));
    expect(onApply).toHaveBeenCalledWith([{ op: vague, key: 'KUP-57' }]);
  });

  it('a change can be left out', () => {
    const comment: VoiceOperation = { ...sprint, operation: 'comment', value: 'Shipped behind a flag', update: null, undo: null };
    const { onApply } = show(reading([sprint, comment]));

    fireEvent.click(screen.getByRole('checkbox', { name: /Sprint KUP-12/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Apply 1 change' }));
    expect(onApply).toHaveBeenCalledWith([{ op: comment, key: 'KUP-12' }]);
  });

  it('Escape closes it', () => {
    const { onClose } = show(reading([sprint]));
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });

  it('over budget, it says so and typing still works', () => {
    const { onType } = show({ kind: 'blocked', code: 'ai_budget_spent' }, 'sheet');

    expect(screen.getByRole('alert')).toHaveTextContent('This month’s AI hours are used up');
    fireEvent.change(screen.getByRole('textbox', { name: 'Or type it' }), { target: { value: 'open KUP-12' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(onType).toHaveBeenCalledWith('open KUP-12');
  });

  it('a rejected key says the microphone is off until it is replaced', () => {
    show({ kind: 'blocked', code: 'ai_connection_rejected' });
    expect(screen.getByRole('alert')).toHaveTextContent('rejected the workspace’s key');
  });

  it('nothing understood explains what to try, in Danish too', async () => {
    await i18n.changeLanguage('da');
    show(reading([], { kind: 'none', reason: 'notUnderstood', text: 'hmm' }));
    expect(screen.getByText(/Prøv „flyt KUP-12 til næste sprint“/)).toBeInTheDocument();
  });

  it('tidy-up is a suggestion: the person accepts or keeps their words', () => {
    const { onTidy } = show({ kind: 'tidy', said: 'we shud go', tidied: 'We should go.' });

    expect(screen.getByTestId('tidy-diff')).toHaveTextContent('weWe shudshould gogo.');
    expect(screen.getByRole('button', { name: 'Use tidied text' })).toHaveFocus();
    fireEvent.click(screen.getByRole('button', { name: 'Keep as said' }));
    expect(onTidy).toHaveBeenCalledWith(false);
  });
});
