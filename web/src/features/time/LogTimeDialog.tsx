import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useTimeError, useTimeWrites } from '../../api/time';
import { useUi } from '../../app/UiContext';
import { useToast } from '../../components/Toasts';
import { Button, Kbd } from '../../components/ui';
import { addDays, parseDuration, today, useDayFormat, useDuration } from '../../lib/time';
import { IssueSearch } from './IssueSearch';

/**
 * Log time on an issue (`T`, the palette, the issue panel). Opened with a close, it saves the entry and the state change
 * together, and offers the close without time (ADR-021 Locking). Enter logs, Esc closes.
 */
export function LogTimeDialog({ workspace }: { workspace: string }) {
  const ui = useUi();
  const dialog = useRef<HTMLDialogElement>(null);
  const open = !!ui.logTime;
  useEffect(() => {
    if (open) dialog.current?.showModal();
    else dialog.current?.close();
  }, [open]);

  return (
    <dialog
      ref={dialog}
      aria-labelledby="log-time-title"
      onClose={ui.closeLogTime}
      onKeyDown={(e) => {
        // The shell's global Esc handler prevents the default, which would swallow the dialog's own cancel.
        if (e.key === 'Escape') {
          e.preventDefault();
          ui.closeLogTime();
        }
      }}
      className="m-auto mt-[12vh] w-[min(480px,calc(100%-32px))] rounded-r2 border border-line bg-panel p-0 text-ink shadow-float backdrop:bg-scrim"
    >
      {/* Keyed so each opening starts with empty fields. */}
      {ui.logTime && <LogTimeForm key={JSON.stringify({ ...ui.logTime, onSkip: !!ui.logTime.onSkip })} workspace={workspace} />}
    </dialog>
  );
}

function LogTimeForm({ workspace }: { workspace: string }) {
  const { t } = useTranslation();
  const ui = useUi();
  const toast = useToast();
  const duration = useDuration();
  const days = useDayFormat();
  const message = useTimeError();
  const request = ui.logTime!;
  const { log } = useTimeWrites(workspace);
  const [issue, setIssue] = useState(request.issue ?? '');
  const [text, setText] = useState('');
  const now = today();
  const [date, setDate] = useState(request.date ?? now);
  const [note, setNote] = useState('');
  const [error, setError] = useState('');
  const state = request.state;
  const minutes = parseDuration(text);

  const submit = () => {
    if (!issue) return setError(t('time.log.pickIssue'));
    if (!minutes || Number.isNaN(minutes) || minutes > 1440) return setError(t('time.errors.duration'));
    if (date > now) return setError(t('time.errors.future_date'));
    log.mutate(
      { path: { workspace }, body: { issue, minutes, date, note: note.trim() || null, state: state ?? null } },
      {
        onSuccess: () => {
          ui.closeLogTime();
          toast.info(
            state
              ? t('time.log.savedAndClosed', { duration: duration(minutes), key: issue, state: t(`state.${state}`) })
              : t('time.log.saved', { duration: duration(minutes), key: issue, day: days.long(date) }),
          );
        },
        onError: (e) => setError(message(e)),
      },
    );
  };

  const skip = () => {
    ui.closeLogTime();
    request.onSkip?.();
  };

  const quickDays = [addDays(now, -2), addDays(now, -1), now];

  return (
    <form
      method="dialog"
      className="flex flex-col gap-3 p-4"
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
    >
      <h2 id="log-time-title" className="font-display text-lg font-semibold">
        {state ? t('time.log.titleClose', { state: t(`state.${state}`) }) : t('time.log.title')}
      </h2>
      {state && <p className="text-sm text-mute">{t('time.log.closeHint', { key: issue, state: t(`state.${state}`) })}</p>}
      <label className="flex flex-col gap-1">
        <span className="text-xs text-mute">{t('time.log.issue')}</span>
        {request.issue ? (
          <span className="flex h-[var(--control)] items-center rounded-r1 bg-sunk px-2 font-mono text-sm">{request.issue}</span>
        ) : (
          <IssueSearch autoFocus workspace={workspace} value={issue} onPick={(i) => setIssue(i.key)} />
        )}
      </label>
      <div className="flex flex-wrap items-start gap-3">
        <label className="flex min-w-32 flex-1 flex-col gap-1">
          <span className="text-xs text-mute">{t('time.log.duration')}</span>
          <input
            autoFocus={!!request.issue}
            value={text}
            onChange={(e) => {
              setText(e.target.value);
              setError('');
            }}
            placeholder={t('time.log.durationPlaceholder')}
            autoComplete="off"
            className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2 font-mono"
          />
          <span className="h-4 text-xs text-mute" aria-live="polite">
            {minutes > 0 && minutes <= 1440 ? duration(minutes) : ''}
          </span>
        </label>
        <fieldset className="flex flex-col gap-1">
          <legend className="mb-1 text-xs text-mute">{t('time.log.day')}</legend>
          <div className="flex items-center gap-2">
            <div className="inline-flex overflow-hidden rounded-r1 border border-line">
              {quickDays.map((d) => (
                <button
                  key={d}
                  type="button"
                  aria-pressed={date === d}
                  onClick={() => setDate(d)}
                  className="h-[calc(var(--control)-2px)] px-2.5 aria-pressed:bg-sel aria-pressed:font-semibold aria-pressed:text-acc-ink"
                >
                  {d === now ? t('time.today') : days.weekday(d)}
                </button>
              ))}
            </div>
            <input
              type="date"
              value={date}
              max={now}
              aria-label={t('time.log.date')}
              onChange={(e) => e.target.value && setDate(e.target.value)}
              className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2 font-mono text-sm"
            />
          </div>
        </fieldset>
      </div>
      <label className="flex flex-col gap-1">
        <span className="text-xs text-mute">{t('time.log.note')}</span>
        <input value={note} onChange={(e) => setNote(e.target.value)} className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2" />
      </label>
      {error && (
        <p role="alert" className="text-sm text-bad">
          {error}
        </p>
      )}
      <div className="flex flex-wrap items-center justify-end gap-2">
        <span className="mr-auto text-xs text-mute">
          <Kbd>↵</Kbd> {t('time.log.enterHint')}
        </span>
        <Button variant="ghost" onClick={ui.closeLogTime}>
          {t('common.cancel')}
        </Button>
        {state && request.onSkip && <Button onClick={skip}>{t('time.log.skip', { state: t(`state.${state}`) })}</Button>}
        <Button variant="primary" type="submit" disabled={log.isPending}>
          {state ? t('time.log.saveAndClose', { state: t(`state.${state}`) }) : t('time.log.save')}
        </Button>
      </div>
    </form>
  );
}
