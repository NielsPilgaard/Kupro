import { useQuery } from '@tanstack/react-query';
import { Link, useParams, useSearch } from '@tanstack/react-router';
import { ChevronLeft, ChevronRight, Lock, Trash2 } from 'lucide-react';
import { useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { listMyTimeOptions, listProjectsOptions } from '../../api/gen/@tanstack/react-query.gen';
import type { IssueRef, TimeEntryResponse } from '../../api/gen/types.gen';
import { closed, useTimeError, useTimeRegistration, useTimeWrites } from '../../api/time';
import { Topbar } from '../../app/AppShell';
import { useToast } from '../../components/Toasts';
import { Button, Kbd, StatusRing } from '../../components/ui';
import { projectKeyOf } from '../../lib/issues';
import { addDays, isoWeek, mondayOf, parseDuration, today, useDayFormat, useDuration, weekDays } from '../../lib/time';
import { IssueSearch } from './IssueSearch';

export interface TimeSearch {
  /** Any day of the week to show; this week when absent. */
  week?: string;
  issue?: string;
  meeting?: string;
}

interface Row {
  issue: IssueRef;
  projectKey: string;
  locked: boolean;
}

/**
 * `/w/:workspace/time`: My time, the week grid (task 034, prototype 01). Rows per issue grouped by project with
 * subtotals, locked issues in their own group, Monday to Sunday with today highlighted, day totals, and a panel for the
 * selected cell's entries.
 */
export function MyTimePage() {
  const { t } = useTranslation();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const search = useSearch({ strict: false }) as TimeSearch;
  const time = useTimeRegistration(workspace);
  const duration = useDuration();
  const days = useDayFormat();
  const now = today();
  const monday = mondayOf(search.week && search.week <= now ? search.week : now);
  const week = weekDays(monday);
  const thisWeek = monday === mondayOf(now);
  const { data } = useQuery(listMyTimeOptions({ path: { workspace }, query: { from: monday, to: week[6] } }));
  const { data: projects } = useQuery(listProjectsOptions({ path: { workspace } }));
  // Rows added by hand stay for this visit, per week, until time is logged on them.
  const [added, setAdded] = useState<Record<string, Row[]>>({});
  const [selected, setSelected] = useState<{ key: string; date: string } | null>(null);

  const entries = data?.entries ?? [];
  const rows = new Map<string, Row>();
  for (const e of entries) rows.set(e.issue.key, { issue: e.issue, projectKey: e.projectKey, locked: e.locked });
  for (const r of added[monday] ?? []) if (!rows.has(r.issue.key)) rows.set(r.issue.key, r);
  const all = [...rows.values()].sort((a, b) => a.issue.key.localeCompare(b.issue.key, undefined, { numeric: true }));
  const projectName = (key: string) => projects?.find((p) => p.key === key)?.name ?? key;
  const groups = [...new Set(all.filter((r) => !r.locked).map((r) => r.projectKey))]
    .sort((a, b) => projectName(a).localeCompare(projectName(b)))
    .map((p) => ({ id: p, name: projectName(p), rows: all.filter((r) => !r.locked && r.projectKey === p) }));
  const locked = all.filter((r) => r.locked);
  if (locked.length) groups.push({ id: 'locked', name: t('time.week.lockedGroup'), rows: locked });

  const cell = (key: string, date: string) => entries.filter((e) => e.issue.key === key && e.date === date);
  const sum = (list: TimeEntryResponse[]) => list.reduce((a, e) => a + e.minutes, 0);
  const dayTotal = (date: string) => sum(entries.filter((e) => e.date === date));
  const rowTotal = (key: string) => sum(entries.filter((e) => e.issue.key === key));
  const weekTotal = data?.totalMinutes ?? 0;
  const gaps = week.slice(0, 5).filter((d) => d <= now && dayTotal(d) === 0).length;
  const lockedMinutes = sum(entries.filter((e) => e.locked));
  const sel = selected && rows.has(selected.key) && week.includes(selected.date) ? selected : null;

  const addRow = (issue: IssueRef) => {
    setAdded((a) => ({ ...a, [monday]: [...(a[monday] ?? []), { issue, projectKey: projectKeyOf(issue.key), locked: closed(issue.state) }] }));
    const date = thisWeek ? now : week[0];
    setSelected({ key: issue.key, date });
    requestAnimationFrame(() => document.querySelector<HTMLInputElement>(`input[data-key="${issue.key}"][data-day="${date}"]`)?.focus());
  };

  return (
    <>
      <Topbar crumbs={[t('nav.myTime')]} />
      <main className="flex min-h-0 flex-1 flex-col gap-4 overflow-auto p-6">
        <div className="flex flex-wrap items-end gap-3">
          <div>
            <h2 className="font-display text-2xl font-bold">{t('time.week.title', { week: isoWeek(monday) })}</h2>
            <p className="text-mute">{days.range(monday, week[6])}</p>
          </div>
          <WeekNav to="/w/$workspace/time" workspace={workspace} monday={monday} thisWeek={thisWeek} />
        </div>

        {time.loaded && !time.on && <p className="rounded-r2 bg-sunk p-3 text-sm text-mute">{t('time.week.off')}</p>}

        <div className="grid grid-cols-4 gap-3 max-lg:grid-cols-2">
          <Tile label={t('time.week.tileWeek')} value={duration(weekTotal) || '0'} sub={t('time.week.onIssues', { count: new Set(entries.map((e) => e.issue.key)).size })} />
          {thisWeek ? (
            <Tile
              label={t('time.week.tileToday', { day: days.weekday(now) })}
              value={duration(dayTotal(now)) || '0'}
              sub={t('time.week.entries', { count: entries.filter((e) => e.date === now).length })}
            />
          ) : (
            <Tile label={t('time.week.tileDays')} value={String(new Set(entries.map((e) => e.date)).size)} sub={t('time.week.daysWithTime')} />
          )}
          <Tile label={t('time.week.tileGaps')} value={String(gaps)} sub={t(gaps ? 'time.week.gapsHint' : 'time.week.noGaps')} warn={gaps > 0} />
          <Tile label={t('time.locked')} value={duration(lockedMinutes) || '0'} sub={t('time.week.lockedHint')} />
        </div>

        <div className="overflow-x-auto rounded-r2 border border-line bg-panel">
          <table className="w-full min-w-[980px] border-collapse text-md" aria-label={t('time.week.grid')}>
            <colgroup>
              <col className="w-[38%]" />
              <col span={5} />
              <col span={2} className="w-[6%]" />
              <col className="w-[9%]" />
            </colgroup>
            <thead>
              <tr className="h-14 bg-sunk text-sm text-mute">
                <th className="px-3.5 text-left font-medium">{t('time.week.issue')}</th>
                {week.map((d, i) => (
                  <th
                    key={d}
                    scope="col"
                    aria-current={d === now ? 'date' : undefined}
                    className={`border-l px-2 text-center font-medium ${i === 0 || i === 5 ? 'border-l-2 border-l-mute/40' : 'border-l-line'} ${d === now ? 'bg-acc/15 text-acc-ink' : ''}`}
                  >
                    <span className="block text-xs tracking-wide uppercase">{days.weekday(d)}</span>
                    <b className={`font-display text-lg ${d === now ? 'text-acc-ink' : 'text-ink'}`}>{days.dayOfMonth(d)}</b>
                  </th>
                ))}
                <th className="border-l-2 border-l-mute/40 bg-sunk px-3.5 text-right font-medium">{t('time.week.weekCol')}</th>
              </tr>
            </thead>
            <tbody>
              {groups.map((g) => (
                <Group key={g.id} name={g.name} total={duration(g.rows.reduce((a, r) => a + rowTotal(r.issue.key), 0))}>
                  {g.rows.map((r) => (
                    <tr key={r.issue.key} className={`h-12 border-t border-line ${r.locked ? 'bg-sunk' : 'hover:bg-sunk/50'}`}>
                      <td className="px-3.5 leading-tight">
                        <span className="inline-block min-w-16 font-mono text-sm text-mute">{r.issue.key}</span> {r.issue.title}
                        <span className="ml-1.5 inline-flex items-center gap-1 rounded-full bg-sunk px-2 align-middle text-xs text-mute">
                          {r.locked ? <Lock size={11} strokeWidth={1.5} aria-hidden="true" /> : <StatusRing state={r.issue.state} />}
                          {t(`state.${r.issue.state}`)}
                        </span>
                      </td>
                      {week.map((d, i) => (
                        <TimeCell
                          key={d}
                          workspace={workspace}
                          row={r}
                          date={d}
                          entries={cell(r.issue.key, d)}
                          disabled={r.locked || d > now || !time.canLog}
                          dayOther={dayTotal(d) - sum(cell(r.issue.key, d))}
                          today={d === now}
                          edge={i === 0 || i === 5}
                          weekend={i > 4}
                          onFocus={() => setSelected({ key: r.issue.key, date: d })}
                        />
                      ))}
                      <td className="border-l-2 border-l-mute/40 bg-sunk/70 px-3.5 text-right font-mono font-semibold tabular-nums">
                        {duration(rowTotal(r.issue.key)) || '–'}
                      </td>
                    </tr>
                  ))}
                </Group>
              ))}
              {groups.length === 0 && (
                <tr>
                  <td colSpan={9} className="p-6 text-center text-mute">
                    {data ? t('time.week.empty') : t('common.loading')}
                  </td>
                </tr>
              )}
            </tbody>
            <tfoot>
              <tr className="h-13 border-t border-line bg-sunk font-mono font-semibold">
                <td className="px-3.5 font-sans">{t('time.week.dayTotal')}</td>
                {week.map((d, i) => {
                  const m = dayTotal(d);
                  const gap = !m && i < 5 && d < now;
                  return (
                    <td key={d} className={`border-l px-3.5 text-right tabular-nums ${i === 0 || i === 5 ? 'border-l-2 border-l-mute/40' : 'border-l-line'} ${gap ? 'font-sans text-xs font-medium text-warn' : ''}`}>
                      {m ? duration(m) : gap ? t('time.week.nothing') : '–'}
                    </td>
                  );
                })}
                <td className="border-l-2 border-l-mute/40 px-3.5 text-right tabular-nums">{duration(weekTotal) || '–'}</td>
              </tr>
            </tfoot>
          </table>
        </div>

        <div className="grid grid-cols-2 gap-3.5 max-lg:grid-cols-1">
          <section className="rounded-r2 border border-line bg-panel p-4" aria-live="polite">
            {sel ? (
              <CellPanel workspace={workspace} row={rows.get(sel.key)!} date={sel.date} entries={cell(sel.key, sel.date)} canEdit={time.canLog} />
            ) : (
              <p className="text-sm text-mute">{t('time.week.selectHint')}</p>
            )}
          </section>
          <section className="flex flex-col gap-3 rounded-r2 border border-line bg-panel p-4">
            {time.canLog && (
              <>
                <h3 className="font-display font-semibold">{t('time.week.addTitle')}</h3>
                <IssueSearch workspace={workspace} exclude={[...rows.keys()]} onPick={addRow} />
              </>
            )}
            <h3 className="font-display font-semibold">{t('time.week.keys')}</h3>
            <dl className="grid grid-cols-[auto_1fr] gap-x-3.5 gap-y-1.5 text-sm">
              <dt className="flex gap-1">
                <Kbd>1h30</Kbd> <Kbd>90m</Kbd> <Kbd>1,5</Kbd> <Kbd>1:30</Kbd>
              </dt>
              <dd className="text-mute">{t('time.week.keyDurations')}</dd>
              <dt className="flex gap-1">
                <Kbd>Tab</Kbd> <Kbd>←</Kbd>
                <Kbd>↑</Kbd>
                <Kbd>↓</Kbd>
                <Kbd>→</Kbd>
              </dt>
              <dd className="text-mute">{t('time.week.keyMove')}</dd>
              <dt>
                <Kbd>↵</Kbd>
              </dt>
              <dd className="text-mute">{t('time.week.keySave')}</dd>
              <dt className="text-mute">{t('time.week.keyEmpty')}</dt>
              <dd className="text-mute">{t('time.week.keyEmptyHint')}</dd>
            </dl>
          </section>
        </div>
      </main>
    </>
  );
}

/** Previous week, this week, next week; never past this week, since time can't be logged ahead (ADR-021). */
export function WeekNav({ to, workspace, monday, thisWeek }: { to: '/w/$workspace/time' | '/w/$workspace/team-time'; workspace: string; monday: string; thisWeek: boolean }) {
  const { t } = useTranslation();
  const cls = 'inline-flex h-[var(--control)] items-center gap-1 rounded-r1 border border-line bg-panel px-3 whitespace-nowrap hover:bg-sel';
  const off = `${cls} pointer-events-none opacity-50`;
  const prev = addDays(monday, -7);
  const next = addDays(monday, 7);
  return (
    <nav className="ml-auto flex items-center gap-2" aria-label={t('time.week.nav')}>
      <Link to={to} params={{ workspace }} search={{ week: prev }} className={cls}>
        <ChevronLeft size={16} strokeWidth={1.5} aria-hidden="true" />
        {t('time.week.short', { week: isoWeek(prev) })}
      </Link>
      {thisWeek ? (
        <span className={off} aria-disabled="true">
          {t('time.week.this')}
        </span>
      ) : (
        <Link to={to} params={{ workspace }} search={{}} className={cls}>
          {t('time.week.this')}
        </Link>
      )}
      {thisWeek ? (
        <span className={off} aria-disabled="true">
          {t('time.week.short', { week: isoWeek(next) })}
          <ChevronRight size={16} strokeWidth={1.5} aria-hidden="true" />
        </span>
      ) : (
        <Link to={to} params={{ workspace }} search={{ week: next }} className={cls}>
          {t('time.week.short', { week: isoWeek(next) })}
          <ChevronRight size={16} strokeWidth={1.5} aria-hidden="true" />
        </Link>
      )}
    </nav>
  );
}

function Tile({ label, value, sub, warn = false }: { label: string; value: string; sub: string; warn?: boolean }) {
  return (
    <div className="rounded-r2 border border-line bg-panel px-4 py-3.5">
      <div className="text-sm text-mute">{label}</div>
      <div className={`mt-0.5 font-display text-2xl font-semibold ${warn ? 'text-warn' : ''}`}>{value}</div>
      <div className="text-sm text-mute">{sub}</div>
    </div>
  );
}

function Group({ name, total, children }: { name: string; total: string; children: React.ReactNode }) {
  return (
    <>
      <tr className="h-9.5 border-t border-line bg-bg">
        <th colSpan={9} scope="rowgroup" className="px-3.5 text-left text-sm font-semibold tracking-wide uppercase">
          {name}
          <span className="float-right font-mono font-medium tracking-normal text-mute normal-case">{total}</span>
        </th>
      </tr>
      {children}
    </>
  );
}

/** Moves focus between enabled cells: ↑↓ in the column, ←→ at the text's edges. */
function move(e: KeyboardEvent<HTMLInputElement>) {
  const input = e.currentTarget;
  const table = input.closest('table');
  if (!table) return;
  const all = [...table.querySelectorAll<HTMLInputElement>('input[data-day]:not(:disabled)')];
  const column = all.filter((x) => x.dataset.day === input.dataset.day);
  const row = all.filter((x) => x.dataset.key === input.dataset.key);
  let target: HTMLInputElement | undefined;
  if (e.key === 'ArrowDown') target = column[column.indexOf(input) + 1];
  if (e.key === 'ArrowUp') target = column[column.indexOf(input) - 1];
  if (e.key === 'ArrowRight' && input.selectionStart === input.value.length) target = row[row.indexOf(input) + 1];
  if (e.key === 'ArrowLeft' && input.selectionStart === 0) target = row[row.indexOf(input) - 1];
  if (target) {
    e.preventDefault();
    target.focus();
    target.select();
  }
}

/**
 * One issue on one day. Typing a duration logs it, changing it edits the entry, emptying it deletes it. A day with
 * several entries is edited in the panel below, so no entry's note is lost.
 */
function TimeCell(props: {
  workspace: string;
  row: Row;
  date: string;
  entries: TimeEntryResponse[];
  disabled: boolean;
  /** The day's minutes on other issues, for the 24-hour check. */
  dayOther: number;
  today: boolean;
  edge: boolean;
  weekend: boolean;
  onFocus(): void;
}) {
  const { workspace, row, date, entries, disabled, dayOther } = props;
  const { t } = useTranslation();
  const toast = useToast();
  const message = useTimeError();
  const duration = useDuration();
  const { log, update, remove } = useTimeWrites(workspace);
  const minutes = entries.reduce((a, e) => a + e.minutes, 0);
  const shown = duration(minutes);
  const [text, setText] = useState(shown);
  // A saved value (or someone else's tab) replaces the text once it arrives.
  const [base, setBase] = useState(shown);
  if (base !== shown) {
    setBase(shown);
    setText(shown);
  }
  const several = entries.length > 1;
  const hasNote = entries.some((e) => e.note);

  const commit = () => {
    const m = parseDuration(text);
    if (m === minutes) return setText(shown);
    const fail = (msg: string) => {
      toast.error(msg);
      setText(shown);
    };
    if (Number.isNaN(m) || m > 1440) return fail(t('time.errors.duration'));
    if (dayOther + m > 1440) return fail(t('time.errors.day_full'));
    const onError = (e: unknown) => fail(message(e));
    const [entry] = entries;
    if (!entry) log.mutate({ path: { workspace }, body: { issue: row.issue.key, minutes: m, date } }, { onError });
    else if (m === 0) remove.mutate({ path: { workspace, id: entry.id } });
    else update.mutate({ path: { workspace, id: entry.id }, body: { minutes: m } });
  };

  return (
    <td
      className={`relative border-l p-0 ${props.edge ? 'border-l-2 border-l-mute/40' : 'border-l-line'} ${props.today ? 'bg-sel' : props.weekend ? 'bg-sunk/55' : ''}`}
    >
      <input
        data-key={row.issue.key}
        data-day={date}
        aria-label={t('time.week.cellLabel', { key: row.issue.key, day: date })}
        value={text}
        disabled={disabled}
        readOnly={several}
        title={several ? t('time.week.severalHint') : undefined}
        placeholder={disabled ? '' : '–'}
        onChange={(e) => setText(e.target.value)}
        onFocus={(e) => {
          props.onFocus();
          e.currentTarget.select();
        }}
        onBlur={() => !several && commit()}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault();
            if (!several) commit();
          } else if (e.key === 'Escape') {
            setText(shown);
          } else move(e);
        }}
        className="h-12 w-full bg-transparent px-3.5 text-right font-mono text-ink tabular-nums placeholder:text-line focus:bg-sel focus:outline-2 focus:-outline-offset-2 focus:outline-acc disabled:cursor-not-allowed disabled:text-mute"
      />
      {hasNote && <span className="pointer-events-none absolute top-1.5 right-1.5 size-1.5 rounded-full bg-prog" aria-hidden="true" />}
    </td>
  );
}

/** The selected cell's entries: each one's duration and note, editable, and deletable, unless the issue is locked. */
function CellPanel({ workspace, row, date, entries, canEdit }: { workspace: string; row: Row; date: string; entries: TimeEntryResponse[]; canEdit: boolean }) {
  const { t } = useTranslation();
  const duration = useDuration();
  const days = useDayFormat();
  const total = entries.reduce((a, e) => a + e.minutes, 0);
  return (
    <div className="flex flex-col gap-2">
      <h3 className="font-display font-semibold">
        <span className="font-mono text-sm text-mute">{row.issue.key}</span> {row.issue.title}
      </h3>
      <p className="text-sm text-mute">
        {days.long(date)} · {duration(total) || t('time.week.noTime')}
      </p>
      {entries.length > 0 && (
        <ul className="flex flex-col">
          {entries.map((e) => (
            <EntryRow key={e.id} workspace={workspace} entry={e} editable={canEdit && !row.locked} />
          ))}
        </ul>
      )}
      {row.locked ? (
        <p className="flex items-start gap-2 rounded-r1 bg-sunk px-2.5 py-2 text-sm text-mute">
          <Lock size={14} strokeWidth={1.5} aria-hidden="true" className="mt-0.5 shrink-0" />
          {t('time.week.lockedLine', { state: t(`state.${row.issue.state}`) })}
        </p>
      ) : (
        entries.length === 0 && <p className="text-sm text-mute">{t('time.week.typeHint')}</p>
      )}
    </div>
  );
}

function EntryRow({ workspace, entry, editable }: { workspace: string; entry: TimeEntryResponse; editable: boolean }) {
  const { t } = useTranslation();
  const toast = useToast();
  const duration = useDuration();
  const { update, remove } = useTimeWrites(workspace);
  const [note, setNote] = useState(entry.note ?? '');
  const [minutes, setMinutes] = useState(duration(entry.minutes));
  const field = 'h-[var(--control)] rounded-r1 border border-line bg-panel px-2';
  if (!editable) {
    return (
      <li className="flex gap-2.5 border-t border-line py-1.5">
        <span className="min-w-16 font-mono">{duration(entry.minutes)}</span>
        <span className="text-mute">{entry.note || t('time.week.noNote')}</span>
      </li>
    );
  }
  return (
    <li className="flex items-center gap-2 border-t border-line py-1.5">
      <input
        value={minutes}
        aria-label={t('time.log.duration')}
        onChange={(e) => setMinutes(e.target.value)}
        onBlur={() => {
          const m = parseDuration(minutes);
          if (m === entry.minutes) return;
          if (!m || Number.isNaN(m) || m > 1440) {
            toast.error(t('time.errors.duration'));
            return setMinutes(duration(entry.minutes));
          }
          update.mutate({ path: { workspace, id: entry.id }, body: { minutes: m } });
        }}
        className={`${field} w-24 text-right font-mono`}
      />
      <input
        value={note}
        aria-label={t('time.log.note')}
        placeholder={t('time.week.notePlaceholder')}
        onChange={(e) => setNote(e.target.value)}
        onBlur={() => note !== (entry.note ?? '') && update.mutate({ path: { workspace, id: entry.id }, body: { note } })}
        className={`${field} min-w-0 flex-1`}
      />
      <Button variant="ghost" className="px-2" aria-label={t('time.week.deleteEntry')} onClick={() => remove.mutate({ path: { workspace, id: entry.id } })}>
        <Trash2 size={14} strokeWidth={1.5} aria-hidden="true" />
      </Button>
    </li>
  );
}
