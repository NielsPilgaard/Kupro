import { useQuery } from '@tanstack/react-query';
import { useParams, useSearch } from '@tanstack/react-router';
import { Download } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { listTimeEntriesOptions } from '../../api/gen/@tanstack/react-query.gen';
import { exportTime } from '../../api/gen/sdk.gen';
import type { TimeExportFormat } from '../../api/gen/types.gen';
import { useMembers } from '../../api/issues';
import { useTimeRegistration } from '../../api/time';
import { Topbar } from '../../app/AppShell';
import { Button } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { addDays, isCalendarDay, isoWeek, mondayOf, today, useDayFormat, useDuration, weekDays } from '../../lib/time';
import { WeekNav, type TimeSearch } from './MyTimePage';

/**
 * `/w/:workspace/team-time`, Owners only: each person's day totals for a week, and the export (ADR-021 Who sees what).
 * Owners read and export; they don't edit anyone's time. People who have left stay while they have entries.
 */
export function TeamTimePage() {
  const { t } = useTranslation();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const search = useSearch({ strict: false }) as TimeSearch;
  const time = useTimeRegistration(workspace);
  const duration = useDuration();
  const days = useDayFormat();
  const now = today();
  const monday = mondayOf(search.week && search.week <= now ? search.week : now);
  const week = weekDays(monday);
  const { data: members } = useMembers(workspace);
  const { data, isError, refetch } = useQuery({ ...listTimeEntriesOptions({ path: { workspace }, query: { from: monday, to: week[6] } }), enabled: time.owner });
  const [exporting, setExporting] = useState(false);

  const entries = data?.items ?? [];
  const withTime = new Set(entries.map((e) => e.member.id));
  // Everyone who logs time (Owners and Members), plus anyone with entries: placeholders and people who have left.
  const people = (members ?? [])
    .filter((m) => m.kind === 'human' && ((m.status === 'active' && m.role !== 'guest') || withTime.has(m.id)))
    .sort((a, b) => a.name.localeCompare(b.name));
  // The export reaches back up to a year, so it offers anyone who could have entries, not only this week's.
  const exportable = (members ?? [])
    .filter((m) => m.kind === 'human' && m.status !== 'invited' && (m.role !== 'guest' || withTime.has(m.id)))
    .sort((a, b) => a.name.localeCompare(b.name));
  const minutes = (member: string, date?: string) =>
    entries.filter((e) => e.member.id === member && (!date || e.date === date)).reduce((a, e) => a + e.minutes, 0);

  if (time.loaded && !time.owner) {
    return (
      <>
        <Topbar crumbs={[t('nav.teamTime')]} />
        <p className="p-6 text-mute">{t('time.team.ownersOnly')}</p>
      </>
    );
  }

  return (
    <>
      <Topbar
        crumbs={[t('nav.teamTime')]}
        actions={
          <Button onClick={() => setExporting(true)}>
            <Download size={16} strokeWidth={1.5} aria-hidden="true" />
            {t('time.export.open')}
          </Button>
        }
      />
      <main className="flex min-h-0 flex-1 flex-col gap-4 overflow-auto p-6">
        <div className="flex flex-wrap items-end gap-3">
          <div>
            <h2 className="font-display text-2xl font-bold">{t('time.week.title', { week: isoWeek(monday) })}</h2>
            <p className="text-mute">
              {days.range(monday, week[6])} · {t('time.team.hint')}
            </p>
          </div>
          <WeekNav to="/w/$workspace/team-time" workspace={workspace} monday={monday} thisWeek={monday === mondayOf(now)} />
        </div>
        <div className="overflow-x-auto rounded-r2 border border-line bg-panel">
          <table className="w-full min-w-[760px] border-collapse" aria-label={t('nav.teamTime')}>
            <thead>
              <tr className="h-[var(--row)] bg-sunk text-sm text-mute">
                <th className="px-2.5 text-left font-medium">{t('time.team.person')}</th>
                {week.map((d) => (
                  <th key={d} className={`px-2.5 text-right font-medium ${d === now ? 'text-acc-ink' : ''}`}>
                    {days.weekday(d)} {days.dayOfMonth(d)}
                  </th>
                ))}
                <th className="px-2.5 text-right font-medium">{t('time.week.weekCol')}</th>
              </tr>
            </thead>
            <tbody>
              {data && people.map((m) => (
                <tr key={m.id} className="h-[var(--row)] border-t border-line">
                  <td className="px-2.5">
                    {m.name}
                    {m.status === 'deactivated' && <span className="ml-1.5 text-sm text-mute">{t('time.team.left')}</span>}
                  </td>
                  {week.map((d, i) => (
                    <td key={d} className={`px-2.5 text-right font-mono tabular-nums ${i > 4 ? 'text-mute' : ''}`}>
                      {duration(minutes(m.id, d)) || '–'}
                    </td>
                  ))}
                  <td className="px-2.5 text-right font-mono font-semibold tabular-nums">{duration(minutes(m.id)) || '–'}</td>
                </tr>
              ))}
              {(!data || people.length === 0) && (
                <tr>
                  <td colSpan={9} className="p-6 text-center text-mute">
                    {isError ? (
                      <span className="flex items-center justify-center gap-3">
                        {t('time.errors.load')}
                        <Button onClick={() => void refetch()}>{t('common.retry')}</Button>
                      </span>
                    ) : (
                      t('common.loading')
                    )}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <p className="max-w-2xl text-sm text-mute">{t('time.team.note')}</p>
      </main>
      {exporting && <ExportDialog workspace={workspace} people={exportable} onClose={() => setExporting(false)} />}
    </>
  );
}

/** Pick people and a period (up to 366 days), download CSV or JSON: the employer's copy (ADR-021 Retention). */
function ExportDialog({ workspace, people, onClose }: { workspace: string; people: { id: string; name: string; status: string }[]; onClose(): void }) {
  const { t } = useTranslation();
  const toast = useToast();
  const dialog = useRef<HTMLDialogElement>(null);
  const now = today();
  const [member, setMember] = useState('');
  const [from, setFrom] = useState(`${now.slice(0, 7)}-01`);
  const [to, setTo] = useState(now);
  const [format, setFormat] = useState<TimeExportFormat>('csv');
  const [busy, setBusy] = useState(false);
  useEffect(() => dialog.current?.showModal(), []);
  const left = people.filter((p) => p.status === 'deactivated').length;
  // A cleared date input gives '', which date arithmetic can't take.
  const tooLong = !isCalendarDay(from) || !isCalendarDay(to) || from > to || addDays(from, 365) < to;

  const download = async () => {
    setBusy(true);
    try {
      const { data } = await exportTime({
        path: { workspace },
        query: { from, to, format, ...(member ? { member } : {}) },
        parseAs: 'blob',
        throwOnError: true,
      });
      const url = URL.createObjectURL(data as unknown as Blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `kupro-time-${from}-${to}.${format}`;
      a.click();
      URL.revokeObjectURL(url);
      onClose();
    } catch {
      toast.error(t('time.export.failed'));
    } finally {
      setBusy(false);
    }
  };

  const field = 'h-[var(--control)] rounded-r1 border border-line bg-panel px-2';
  return (
    <dialog
      ref={dialog}
      aria-labelledby="export-time-title"
      onClose={onClose}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.preventDefault();
          onClose();
        }
      }}
      className="m-auto mt-[12vh] w-[min(460px,calc(100%-32px))] rounded-r2 border border-line bg-panel p-0 text-ink shadow-float backdrop:bg-scrim"
    >
      <form
        method="dialog"
        className="flex flex-col gap-3 p-4"
        onSubmit={(e) => {
          e.preventDefault();
          if (!tooLong) void download();
        }}
      >
        <h2 id="export-time-title" className="font-display text-lg font-semibold">
          {t('time.export.title')}
        </h2>
        <label className="flex flex-col gap-1">
          <span className="text-xs text-mute">{t('time.export.people')}</span>
          <select value={member} onChange={(e) => setMember(e.target.value)} className={field}>
            <option value="">{left ? t('time.export.everyoneLeft', { count: left, total: people.length }) : t('time.export.everyone', { count: people.length })}</option>
            {people.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </label>
        <div className="flex gap-3">
          <label className="flex flex-1 flex-col gap-1">
            <span className="text-xs text-mute">{t('time.export.from')}</span>
            <input type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} className={field} />
          </label>
          <label className="flex flex-1 flex-col gap-1">
            <span className="text-xs text-mute">{t('time.export.to')}</span>
            <input type="date" value={to} min={from} onChange={(e) => setTo(e.target.value)} className={field} />
          </label>
        </div>
        <fieldset className="flex flex-col gap-1">
          <legend className="mb-1 text-xs text-mute">{t('time.export.format')}</legend>
          <div className="inline-flex w-fit overflow-hidden rounded-r1 border border-line">
            {(['csv', 'json'] as const).map((f) => (
              <button
                key={f}
                type="button"
                aria-pressed={format === f}
                onClick={() => setFormat(f)}
                className="h-[calc(var(--control)-2px)] px-3 uppercase aria-pressed:bg-sel aria-pressed:font-semibold aria-pressed:text-acc-ink"
              >
                {f}
              </button>
            ))}
          </div>
        </fieldset>
        <p className="text-sm text-mute">{t('time.export.hint')}</p>
        {tooLong && (
          <p role="alert" className="text-sm text-bad">
            {t('time.export.tooLong')}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" type="submit" disabled={busy || tooLong}>
            {t('time.export.download')}
          </Button>
        </div>
      </form>
    </dialog>
  );
}
