import { Lock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useIssueTime } from '../../api/time';
import { useUi } from '../../app/UiContext';
import { Button, Kbd } from '../../components/ui';
import { useDuration } from '../../lib/time';

/**
 * Time on the issue: your own, or for Owners the total and each person's share (ADR-021 Who sees what). Locked while the
 * issue is Done or Canceled. `compact` is the narrow panel's one-line strip.
 */
export function IssueTimeBox({ workspace, issueKey, owner, compact = false }: { workspace: string; issueKey: string; owner: boolean; compact?: boolean }) {
  const { t } = useTranslation();
  const ui = useUi();
  const duration = useDuration();
  const { data } = useIssueTime(workspace, issueKey);
  if (!data) return null;
  const total = owner ? (data.totalMinutes ?? 0) : data.myMinutes;
  const log = !data.locked && (
    <Button className="h-7 px-2 text-sm" onClick={() => ui.openLogTime({ issue: issueKey })}>
      {t('time.logTime')} <Kbd>T</Kbd>
    </Button>
  );
  const lock = data.locked && (
    <span className="flex items-center gap-1.5 text-sm text-mute" title={t('time.lockedHint')}>
      <Lock size={14} strokeWidth={1.5} aria-hidden="true" />
      {t('time.locked')}
    </span>
  );

  if (compact) {
    return (
      <div className="flex items-center gap-2" aria-label={t('time.onIssue')} role="group">
        <span className="text-sm text-mute">{t(owner ? 'time.total' : 'time.yours')}</span>
        <span className="font-mono tabular-nums">{duration(total) || '–'}</span>
        {lock}
        {log}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-1.5">
      <span className="font-display text-lg font-semibold tabular-nums">{duration(total) || '–'}</span>
      {owner && data.people && data.people.length > 0 && (
        <dl className="grid grid-cols-[1fr_auto] gap-x-3 gap-y-0.5 text-sm">
          {data.people.map((p) => (
            <div key={p.member.id} className="contents">
              <dt className="truncate">{p.member.name}</dt>
              <dd className="font-mono tabular-nums">{duration(p.minutes)}</dd>
            </div>
          ))}
        </dl>
      )}
      {!owner && <span className="text-xs text-mute">{t('time.privateHint')}</span>}
      {data.locked ? <p className="rounded-r1 bg-sunk px-2 py-1.5 text-sm text-mute">{t('time.lockedHint')}</p> : <div>{log}</div>}
    </div>
  );
}
