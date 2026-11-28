import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { IssueState, VoiceInterpretation, VoiceOperation } from '../../api/gen/types.gen';
import { Button } from '../../components/ui';
import { tidyDiff } from './voice';

/** What the card shows: one utterance's way through listening, thinking and its result (task 036). */
export type CardState =
  | { kind: 'listening'; dictating: boolean }
  | { kind: 'thinking' }
  | { kind: 'reading'; reading: VoiceInterpretation }
  | { kind: 'blocked'; code: string }
  | { kind: 'tidy'; said: string; tidied: string };

/** One change to apply; `key` is the issue picked for a proposal with candidates. */
export interface Selection {
  op: VoiceOperation;
  key?: string;
}

interface Props {
  state: CardState;
  /** `card` drops down under the top bar (prototype 01); `sheet` is the thumb button's bottom sheet (prototype 03). */
  variant: 'card' | 'sheet';
  busy?: boolean;
  onApply(selections: Selection[]): void;
  onType(text: string): void;
  onTidy(accept: boolean): void;
  onClose(): void;
}

/** The confirmation card or bottom sheet: the diff, why a change waits, Enter applies, Escape closes. */
export function VoiceCard({ state, variant, busy, onApply, onType, onTidy, onClose }: Props) {
  const { t } = useTranslation();
  const sheet = variant === 'sheet';
  const frame = sheet
    ? 'fixed inset-x-0 bottom-0 z-40 max-h-[80vh] overflow-auto rounded-t-[var(--r2)] border-t border-line bg-panel p-4 pb-6 text-lg shadow-float'
    : 'fixed top-[calc(var(--topbar)+8px)] left-1/2 z-40 max-h-[70vh] w-[min(560px,calc(100vw-24px))] -translate-x-1/2 overflow-auto rounded-r2 border border-line bg-panel p-4 shadow-float';

  return (
    <div
      role="dialog"
      aria-label={t('voice.talk')}
      data-voice-ui=""
      className={frame}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.stopPropagation();
          onClose();
        }
      }}
    >
      {state.kind === 'listening' && <Status text={t(state.dictating ? 'voice.status.dictating' : 'voice.status.listening')} live />}
      {state.kind === 'thinking' && <Status text={t('voice.status.thinking')} />}
      {state.kind === 'reading' && (
        <Reading reading={state.reading} sheet={sheet} busy={busy} onApply={onApply} onType={onType} onClose={onClose} />
      )}
      {state.kind === 'blocked' && (
        <>
          <Blocked code={state.code} />
          <TypeInstead sheet={sheet} onType={onType} />
          <Footer sheet={sheet} onClose={onClose} />
        </>
      )}
      {state.kind === 'tidy' && <Tidy said={state.said} tidied={state.tidied} sheet={sheet} onTidy={onTidy} />}
    </div>
  );
}

function Status({ text, live }: { text: string; live?: boolean }) {
  return (
    <div className="flex items-center gap-3">
      {live ? (
        <span className="inline-flex h-5 items-center gap-[3px]" aria-hidden="true">
          {[0, 1, 2, 3, 4].map((i) => (
            <i key={i} className="h-2.5 w-[3px] animate-pulse rounded-full bg-acc" style={{ animationDelay: `${i * 100}ms` }} />
          ))}
        </span>
      ) : (
        <span className="size-3.5 animate-spin rounded-full border-2 border-line border-t-acc" aria-hidden="true" />
      )}
      <span className="font-semibold">{text}</span>
    </div>
  );
}

function Reading({
  reading,
  sheet,
  busy,
  onApply,
  onType,
  onClose,
}: {
  reading: VoiceInterpretation;
  sheet: boolean;
  busy?: boolean;
  onApply(selections: Selection[]): void;
  onType(text: string): void;
  onClose(): void;
}) {
  const { t } = useTranslation();
  const ops = reading.operations;
  const [checked, setChecked] = useState(() => ops.map((op) => !op.needsIssue));
  const [picked, setPicked] = useState<(string | undefined)[]>(() => ops.map(() => undefined));
  const apply = useRef<HTMLButtonElement>(null);
  const selections = ops
    .map((op, i) => ({ op, key: picked[i] ?? op.issue?.key, on: checked[i] }))
    .filter((s) => s.on && (s.key || s.op.operation === 'createIssue'))
    .map(({ op, key }) => ({ op, key }));

  // Enter applies: the Apply button has focus as soon as the card shows.
  useEffect(() => apply.current?.focus(), []);

  return (
    <div className="flex flex-col gap-3">
      {reading.text && (
        <p className="text-mute italic">
          <span className="not-italic">{t('voice.said')}:</span> “{reading.text}”
        </p>
      )}
      {reading.kind === 'none' && <p>{t(`voice.none.${reading.reason ?? 'notUnderstood'}`)}</p>}
      {ops.length > 0 && (
        <ul className="flex flex-col">
          {ops.map((op, i) => (
            <li key={i} className="flex items-start gap-3 border-t border-line py-2 first:border-t-0">
              <input
                type="checkbox"
                className={`mt-1 ${sheet ? 'size-5' : ''}`}
                checked={checked[i]}
                disabled={op.needsIssue && !picked[i]}
                onChange={(e) => setChecked((c) => c.map((v, j) => (j === i ? e.target.checked : v)))}
                aria-label={`${t(`meeting.op.${op.operation}`)} ${op.issue?.key ?? picked[i] ?? ''}`}
              />
              <div className="flex min-w-0 flex-1 flex-col gap-1">
                <span className="truncate">
                  {op.operation === 'createIssue' ? (
                    <span className="font-semibold">{t('voice.newIssue')}</span>
                  ) : (
                    op.issue && (
                      <>
                        <span className="font-mono text-mute">{op.issue.key}</span> {op.issue.title}
                      </>
                    )
                  )}
                </span>
                <Diff op={op} />
                {op.held.filter((h) => h !== 'ambiguous').map((h) => (
                  <span key={h} className="text-sm text-warn">
                    {t(`voice.held.${h}`)}
                  </span>
                ))}
                {op.needsIssue && op.candidates.length > 0 && (
                  <fieldset className="flex flex-col gap-1">
                    <legend className="text-sm text-warn">{t('voice.pick')}</legend>
                    {op.candidates.map((c) => (
                      <label key={c.key} className={`flex items-center gap-2 ${sheet ? 'min-h-11' : ''}`}>
                        <input
                          type="radio"
                          name={`pick-${i}`}
                          checked={picked[i] === c.key}
                          onChange={() => {
                            setPicked((p) => p.map((v, j) => (j === i ? c.key : v)));
                            setChecked((x) => x.map((v, j) => (j === i ? true : v)));
                          }}
                        />
                        <span className="font-mono text-mute">{c.key}</span>
                        <span className="truncate">{c.title}</span>
                      </label>
                    ))}
                  </fieldset>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      {reading.budgetSpent && <p className="text-sm text-mute">{t('voice.keyword')}</p>}
      {reading.kind === 'none' && <TypeInstead sheet={sheet} onType={onType} />}
      <div className={`flex flex-wrap items-center gap-2 ${sheet ? '[&>button]:h-12 [&>button]:flex-1 [&>button]:justify-center' : ''}`}>
        {ops.length > 0 && (
          <Button ref={apply} variant="primary" disabled={busy || selections.length === 0} onClick={() => onApply(selections)}>
            {t('voice.applyCount', { count: selections.length })}
          </Button>
        )}
        <Button ref={ops.length > 0 ? undefined : apply} onClick={onClose}>
          {t('voice.close')}
        </Button>
      </div>
    </div>
  );
}

function Diff({ op }: { op: VoiceOperation }) {
  const { t } = useTranslation();
  const show = (value: string | null | undefined, display: string | null | undefined) => {
    if (value == null || value === 'none') return t('feed.none');
    if (op.operation === 'state') return t(`state.${value as IssueState}`);
    if (op.operation === 'estimate') return `${value} h`;
    return display ?? value;
  };
  if (op.operation === 'comment' || op.operation === 'createIssue') {
    return (
      <span>
        <span className="text-mute">{t(`meeting.op.${op.operation}`)}:</span> {op.value}
      </span>
    );
  }
  return (
    <span>
      <span className="text-mute">{t(`meeting.op.${op.operation}`)}:</span>{' '}
      {!op.needsIssue && <span className="text-mute line-through">{show(op.before, op.beforeDisplay)}</span>}
      {!op.needsIssue && ' → '}
      <span className="font-semibold">{show(op.value, op.valueDisplay)}</span>
    </span>
  );
}

function Blocked({ code }: { code: string }) {
  const { t } = useTranslation();
  const keys: Record<string, 'budget' | 'rejected' | 'unavailable' | 'microphone'> = {
    ai_budget_spent: 'budget',
    ai_connection_rejected: 'rejected',
    transcription_unavailable: 'unavailable',
    microphone: 'microphone',
  };
  const key = keys[code];
  if (!key) return <p role="alert">{t('voice.blocked.failed')}</p>;
  return (
    <div role="alert" className={`flex flex-col gap-1 rounded-r2 border p-3 ${code === 'ai_connection_rejected' ? 'border-bad' : 'border-warn'}`}>
      <span className="font-semibold">{t(`voice.blocked.${key}Title`)}</span>
      <span className="text-mute">{t(`voice.blocked.${key}`)}</span>
    </div>
  );
}

/** Typed text when talking didn't work: read the same way, with keyword matching when the model is off. */
function TypeInstead({ sheet, onType }: { sheet: boolean; onType(text: string): void }) {
  const { t } = useTranslation();
  const [text, setText] = useState('');
  return (
    <form
      className="mt-3 flex gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        if (text.trim()) onType(text.trim());
      }}
    >
      <input
        value={text}
        onChange={(e) => setText(e.target.value)}
        aria-label={t('voice.typeLabel')}
        placeholder={t('voice.typePlaceholder')}
        className={`min-w-0 flex-1 rounded-r1 border border-line bg-panel px-2 ${sheet ? 'h-12' : 'h-[var(--control)]'}`}
      />
      <Button type="submit" disabled={!text.trim()} className={sheet ? 'h-12!' : ''}>
        {t('voice.send')}
      </Button>
    </form>
  );
}

function Footer({ sheet, onClose }: { sheet: boolean; onClose(): void }) {
  const { t } = useTranslation();
  return (
    <div className="mt-3 flex">
      <Button onClick={onClose} className={sheet ? 'h-12! flex-1 justify-center' : 'ml-auto'}>
        {t('voice.close')}
      </Button>
    </div>
  );
}

/** "Tidy up?": the dictated words with spelling and punctuation fixed, as a suggestion. Nothing changes unless accepted. */
function Tidy({ said, tidied, sheet, onTidy }: { said: string; tidied: string; sheet: boolean; onTidy(accept: boolean): void }) {
  const { t } = useTranslation();
  const accept = useRef<HTMLButtonElement>(null);
  useEffect(() => accept.current?.focus(), []);
  return (
    <div className="flex flex-col gap-2">
      <span className="font-semibold">{t('voice.tidy.title')}</span>
      <span className="text-sm text-mute">{t('voice.tidy.hint')}</span>
      <p className="rounded-r1 border border-line bg-sunk p-2" data-testid="tidy-diff">
        {tidyDiff(said, tidied).map((part, i) =>
          part.kind === 'removed' ? (
            <del key={i} className="text-bad">
              {part.text}
            </del>
          ) : part.kind === 'added' ? (
            <ins key={i} className="text-ok no-underline">
              {part.text}
            </ins>
          ) : (
            <span key={i}>{part.text}</span>
          ),
        )}
      </p>
      <div className={`flex flex-wrap gap-2 ${sheet ? '[&>button]:h-12 [&>button]:flex-1 [&>button]:justify-center' : ''}`}>
        <Button ref={accept} variant="primary" onClick={() => onTidy(true)}>
          {t('voice.tidy.accept')}
        </Button>
        <Button onClick={() => onTidy(false)}>{t('voice.tidy.keep')}</Button>
      </div>
    </div>
  );
}
