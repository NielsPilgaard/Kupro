import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { ChevronDown, ChevronRight, Crosshair, Layers } from 'lucide-react';
import { memo, useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import {
  createIssueMutation,
  createMilestoneMutation,
  getPlanOptions,
  getPlanQueryKey,
  updateIssueMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import type { IssueType, PlanCard, PlanMonth, PlanProject, PlanResponse } from '../../api/gen/types.gen';
import { Topbar } from '../../app/AppShell';
import { useUi } from '../../app/UiContext';
import { Picker } from '../../components/Picker';
import { Button, StatusRing } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { useHours } from '../../lib/format';
import { CapacityEditor } from './CapacityEditor';
import { dayOf, isoOf, lastDayOf, monthsBetween, monthsFor, sumMonths, timeline } from './demand';

export type Zoom = 'month' | 'quarter' | 'year';
const monthWidths: Record<Zoom, number> = { month: 160, quarter: 72, year: 34 };
const LABEL_W = 300;
const ROW_H = 30;
const PROJECT_H = 76;
const BAND_H = 40;
const PAD = 8;
const MONTH_ROW_H = 28;
// Milestone labels get their own strip under the month names, so they never cover them.
const MILESTONE_ROW_H = 20;
/** Picker value for "all epics": cmdk needs a non-empty value. */
const ALL = 'all';

type Override = { key: string; startDate: string; dueDate: string };
type Draft = { project: string; parent: string | null; type: IssueType; startDate: string; dueDate: string; top: number };
type ProjectView = PlanProject & { months: PlanMonth[] };

/** One line of the plan: a project (with its capacity band), an epic, the "no epic" group, or a feature. */
type Row =
  | { kind: 'project'; key: string; project: ProjectView }
  | { kind: 'epic'; key: string; project: ProjectView; card: PlanCard; open: boolean; count: number }
  | { kind: 'loose'; key: string; project: ProjectView; open: boolean; count: number }
  | { kind: 'feature'; key: string; project: ProjectView; card: PlanCard };

const byStart = (a: PlanCard, b: PlanCard) => (a.startDate ?? '9999').localeCompare(b.startDate ?? '9999') || a.key.localeCompare(b.key);

/** Projects › epics › features. Focus keeps one epic, always open; folded epics show their bar only. */
function buildRows(projects: ProjectView[], focus: string | null, folded: Set<string>): Row[] {
  const rows: Row[] = [];
  for (const p of projects) {
    const allEpics = p.cards.filter((c) => c.type === 'epic');
    const epics = allEpics.filter((c) => !focus || c.key === focus).sort(byStart);
    if (focus && epics.length === 0) continue;
    rows.push({ kind: 'project', key: p.key, project: p });
    const features = p.cards.filter((c) => c.type === 'feature').sort(byStart);
    for (const epic of epics) {
      const children = features.filter((f) => f.parentKey === epic.key);
      const open = !!focus || !folded.has(epic.key);
      rows.push({ kind: 'epic', key: epic.key, project: p, card: epic, open, count: children.length });
      if (open) rows.push(...children.map((card) => ({ kind: 'feature' as const, key: card.key, project: p, card })));
    }
    if (focus) continue;
    const epicKeys = new Set(allEpics.map((e) => e.key));
    const loose = features.filter((f) => !f.parentKey || !epicKeys.has(f.parentKey));
    if (loose.length) {
      const key = `${p.key}:loose`;
      const open = !folded.has(key);
      rows.push({ kind: 'loose', key, project: p, open, count: loose.length });
      if (open) rows.push(...loose.map((card) => ({ kind: 'feature' as const, key: card.key, project: p, card })));
    }
  }
  return rows;
}

/**
 * Delivery plan (docs/delivery-plan.md): projects › epics › features over months, capacity band per project,
 * milestones. `?epic=KEY` focuses one epic for epic planning; without it, the plan is the cross-team overview.
 */
export function PlanPage() {
  const { t } = useTranslation();
  const ui = useUi();
  const qc = useQueryClient();
  const toast = useToast();
  const navigate = useNavigate();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const search = useSearch({ strict: false }) as { from?: string; to?: string; zoom?: Zoom; project?: string; epic?: string };
  const zoom = search.zoom ?? 'quarter';
  const monthWidth = monthWidths[zoom];
  const query = {
    from: search.from ? `${search.from}-01` : undefined,
    to: search.to ? `${search.to}-01` : undefined,
    project: search.project ? search.project.split(',') : undefined,
  };
  const planKey = getPlanQueryKey({ path: { workspace }, query });
  const { data: plan, isPending } = useQuery(getPlanOptions({ path: { workspace }, query }));

  const [override, setOverride] = useState<Override | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [cell, setCell] = useState<{ row: string; month: string; draft: Omit<Draft, 'startDate' | 'dueDate'> } | null>(null);
  const [folded, setFolded] = useState<Set<string>>(new Set());

  const update = useMutation({
    ...updateIssueMutation(),
    onMutate: (vars) => {
      // Optimistic: the card stays where it was dropped.
      const previous = qc.getQueryData<PlanResponse>(planKey);
      if (previous) {
        qc.setQueryData<PlanResponse>(planKey, {
          ...previous,
          projects: previous.projects.map((p) => ({
            ...p,
            cards: p.cards.map((c) => (c.key === vars.path.key ? { ...c, startDate: vars.body.startDate ?? c.startDate, dueDate: vars.body.dueDate ?? c.dueDate } : c)),
          })),
        });
      }
      setOverride(null);
      return previous;
    },
    onError: (_e, vars, previous) => {
      if (previous) qc.setQueryData(planKey, previous);
      toast.error(t('errors.updateFailed', { key: vars.path.key }));
    },
    onSettled: () => void qc.invalidateQueries({ queryKey: planKey }),
  });
  const create = useMutation({
    ...createIssueMutation(),
    onSuccess: (issue) => toast.info(t('issue.created', { key: issue.key })),
    onError: () => toast.error(t('errors.createFailed')),
    onSettled: () => void qc.invalidateQueries({ queryKey: planKey }),
  });

  const months = useMemo(() => (plan ? monthsBetween(plan.from, plan.to) : []), [plan]);
  const t2 = useMemo(() => timeline(months.length ? months : ['2027-01-01'], monthWidth), [months, monthWidth]);

  const base = useMemo(
    () => (plan ? plan.projects.map((p) => ({ ...p, months: monthsFor(months, p.cards, p.capacity, plan.hoursPerWeek) })) : []),
    [plan, months],
  );

  // Live while dragging: only the dragged card's project is recomputed, the rest keep their objects so memoised rows
  // skip rendering; the total is re-summed.
  const view = useMemo(() => {
    if (!plan) return null;
    const projects: ProjectView[] = override
      ? base.map((p) => {
          if (!p.cards.some((c) => c.key === override.key)) return p;
          const cards = p.cards.map((c) => (c.key === override.key ? { ...c, ...override } : c));
          return { ...p, cards, months: monthsFor(months, cards, p.capacity, plan.hoursPerWeek) };
        })
      : base;
    return { projects, totals: sumMonths(projects.map((p) => p.months), months, plan.hoursPerWeek) };
  }, [plan, base, override, months]);

  const epics = useMemo(() => base.flatMap((p) => p.cards.filter((c) => c.type === 'epic').map((c) => ({ card: c, project: p }))), [base]);
  const focus = search.epic && epics.some((e) => e.card.key === search.epic) ? search.epic : null;
  const focused = epics.find((e) => e.card.key === focus);

  const rows = useMemo(() => (view ? buildRows(view.projects, focus, folded) : []), [view, focus, folded]);
  const milestones = (plan?.milestones ?? []).filter((m) => !focused || !m.projectKey || m.projectKey === focused.project.key);
  const headerH = MONTH_ROW_H + (milestones.length ? MILESTONE_ROW_H : 0);
  const tops: number[] = [];
  let rowsEnd = headerH;
  for (const row of rows) {
    tops.push(rowsEnd);
    rowsEnd += heightOf(row);
  }
  const totalHeight = rowsEnd + (focus ? 0 : BAND_H + PAD * 2);

  const mutateUpdate = update.mutate;
  const onCommit = useCallback(
    (key: string, startDate: string, dueDate: string) => mutateUpdate({ path: { workspace, key }, body: { startDate, dueDate } }),
    [mutateUpdate, workspace],
  );
  const onOpen = useCallback(
    (key: string) => void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: key }) }),
    [navigate],
  );
  const onToggle = useCallback(
    (key: string) =>
      setFolded((prev) => {
        const next = new Set(prev);
        if (!next.delete(key)) next.add(key);
        return next;
      }),
    [],
  );
  const setSearch = useCallback(
    (patch: Record<string, string | undefined>) => void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, ...patch }) }),
    [navigate],
  );
  const onFocus = useCallback((epic: string | undefined) => setSearch({ epic }), [setSearch]);
  const onSelectMonth = useCallback(
    (row: string, month: string, draft: Omit<Draft, 'startDate' | 'dueDate'>) => setCell({ row, month, draft }),
    [],
  );

  // C with a selected month cell creates there; otherwise C keeps opening the issue dialog. A cell whose row is no
  // longer shown (focus changed, epic folded) is ignored.
  const selected = cell && rows.some((r) => r.key === cell.row) ? cell : null;
  // The cell's draft top is relative to its row: folding an earlier epic moves the row after the click.
  const selectedTop = selected ? tops[rows.findIndex((r) => r.key === selected.row)]! : 0;
  const { setCreateOverride } = ui;
  useEffect(() => {
    setCreateOverride(
      selected
        ? () => setDraft({ ...selected.draft, top: selectedTop + selected.draft.top, startDate: selected.month, dueDate: lastDayOf(selected.month) })
        : null,
    );
    return () => setCreateOverride(null);
  }, [selected, selectedTop, setCreateOverride]);

  const violated = useMemo(() => new Set(plan?.dependencies.filter((d) => d.violated).map((d) => d.blocked)), [plan]);
  const foldable = rows.filter((r) => r.kind === 'epic' || r.kind === 'loose').map((r) => r.key);

  const zoomTabs = (
    <div role="tablist" aria-label={t('plan.zoom')} className="flex rounded-r1 bg-sunk p-0.5">
      {(['month', 'quarter', 'year'] as const).map((z) => (
        <button
          key={z}
          type="button"
          role="tab"
          aria-selected={zoom === z}
          onClick={() => setSearch({ zoom: z })}
          className={`h-6 rounded-[4px] px-3 ${zoom === z ? 'bg-panel font-semibold shadow-sm' : 'text-mute hover:text-ink'}`}
        >
          {t(`plan.zooms.${z}`)}
        </button>
      ))}
    </div>
  );

  return (
    <>
      <Topbar crumbs={focused ? [t('plan.title'), focused.card.title] : [t('plan.title')]} tabs={zoomTabs} />
      <Toolbar
        workspace={workspace}
        plan={plan}
        epics={epics}
        focus={focus}
        onFocus={onFocus}
        allFolded={!focus && foldable.length > 0 && foldable.every((k) => folded.has(k))}
        onFoldAll={(fold) => setFolded(fold ? new Set(epics.map((e) => e.card.key).concat(base.map((p) => `${p.key}:loose`))) : new Set())}
        onHorizon={(from, to) => setSearch({ from, to })}
      />
      {focused && <EpicSummary epic={focused.card} features={focused.project.cards.filter((c) => c.parentKey === focused.card.key)} />}
      <main className="relative min-h-0 flex-1 overflow-auto" aria-label={t('plan.title')}>
        {isPending && <p className="p-8 text-center text-mute">{t('common.loading')}</p>}
        {plan && view && (
          <div className="relative" style={{ width: LABEL_W + t2.width, height: totalHeight }}>
            <MonthHeader months={months} monthWidth={monthWidth} zoom={zoom} height={headerH} />
            {rows.map((row, i) => (
              <PlanRow
                key={row.key}
                workspace={workspace}
                row={row}
                card={row.kind === 'epic' || row.kind === 'feature' ? row.card : undefined}
                top={tops[i]!}
                months={months}
                monthWidth={monthWidth}
                timeline={t2}
                late={row.kind === 'epic' || row.kind === 'feature' ? violated.has(row.card.key) : false}
                focused={!!focus}
                selectedMonth={selected?.row === row.key ? selected.month : null}
                onSelectMonth={onSelectMonth}
                onDraft={setDraft}
                onDrag={setOverride}
                onCommit={onCommit}
                onOpen={onOpen}
                onToggle={onToggle}
                onFocus={onFocus}
              />
            ))}
            {!focus && <TotalsRow top={rowsEnd} months={view.totals} monthWidth={monthWidth} />}
            {milestones.map((m) => (
              <div
                key={m.id}
                className="pointer-events-none absolute top-0 z-30 border-l-[1.5px] border-dashed border-acc-ink"
                style={{ left: LABEL_W + t2.x(m.date), height: totalHeight }}
              >
                <span className="absolute left-1 text-xs leading-5 font-bold whitespace-nowrap text-acc-ink" style={{ top: MONTH_ROW_H }}>
                  {m.name}
                  {m.projectKey ? ` · ${m.projectKey}` : ''}
                </span>
              </div>
            ))}
            {draft && (
              <DraftInput
                draft={draft}
                left={LABEL_W + t2.x(draft.startDate)}
                width={Math.max(160, t2.xEnd(draft.dueDate) - t2.x(draft.startDate))}
                top={draft.top}
                onCancel={() => setDraft(null)}
                onSubmit={(title) => {
                  create.mutate({
                    path: { workspace },
                    body: { project: draft.project, parent: draft.parent, title, type: draft.type, state: 'backlog', startDate: draft.startDate, dueDate: draft.dueDate },
                  });
                  setDraft(null);
                }}
              />
            )}
          </div>
        )}
        {plan && plan.projects.length === 0 && (
          <p className="p-8 text-center text-mute">
            <Link to="/w/$workspace/new-project" params={{ workspace }} className="text-acc-ink underline">
              {t('plan.noProjects')}
            </Link>
          </p>
        )}
      </main>
    </>
  );
}

const heightOf = (row: Row) => (row.kind === 'project' ? PROJECT_H : ROW_H);

/** Epic planning header: dates, features done, effort counted for the epic and its features. */
function EpicSummary({ epic, features }: { epic: PlanCard; features: PlanCard[] }) {
  const { t, i18n } = useTranslation();
  const hours = useHours();
  const date = new Intl.DateTimeFormat(i18n.language, { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
  const done = features.filter((f) => f.state === 'done').length;
  const effort = epic.countedEffort + features.reduce((s, f) => s + f.countedEffort, 0);
  return (
    <div className="flex flex-wrap items-center gap-x-6 gap-y-1 border-b border-line bg-panel px-4 py-2.5">
      <span className="flex items-center gap-2 font-semibold">
        <StatusRing state={epic.state} />
        <span className="font-mono text-sm font-normal text-mute">{epic.key}</span>
        {epic.title}
      </span>
      <span className="text-mute">
        {epic.startDate && epic.dueDate ? `${date.format(new Date(epic.startDate))} → ${date.format(new Date(epic.dueDate))}` : t('plan.noDates')}
      </span>
      <span>{t('plan.featuresDone', { done, total: features.length })}</span>
      <span className="font-mono tabular-nums">{hours(effort)}</span>
    </div>
  );
}

function MonthHeader({ months, monthWidth, zoom, height }: { months: string[]; monthWidth: number; zoom: Zoom; height: number }) {
  const { t, i18n } = useTranslation();
  const format = new Intl.DateTimeFormat(i18n.language, { month: zoom === 'year' ? 'narrow' : 'short', year: zoom === 'month' ? 'numeric' : undefined, timeZone: 'UTC' });
  return (
    <div className="sticky top-0 z-20 flex border-b border-line bg-bg" style={{ height }}>
      <div className="sticky left-0 z-10 flex shrink-0 items-start border-r border-line bg-bg px-3 text-xs leading-7 text-mute uppercase" style={{ width: LABEL_W }}>
        {t('plan.tree')}
      </div>
      {months.map((m) => (
        <div key={m} className="shrink-0 truncate border-l border-line/70 px-1.5 text-xs leading-7 text-mute uppercase" style={{ width: monthWidth }}>
          {format.format(new Date(m))}
          {m.endsWith('-01-01') && zoom !== 'month' ? ` ${m.slice(0, 4)}` : ''}
        </div>
      ))}
    </div>
  );
}

interface RowProps {
  workspace: string;
  row: Row;
  /** The row's card, passed on its own so a row re-renders only when its own card changes. */
  card?: PlanCard;
  top: number;
  months: string[];
  monthWidth: number;
  timeline: ReturnType<typeof timeline>;
  late: boolean;
  focused: boolean;
  selectedMonth: string | null;
  /** The draft's top is relative to the row. */
  onSelectMonth(row: string, month: string, draft: Omit<Draft, 'startDate' | 'dueDate'>): void;
  onDraft(draft: Draft | null): void;
  onDrag(o: Override | null): void;
  onCommit(key: string, startDate: string, dueDate: string): void;
  onOpen(key: string): void;
  onToggle(key: string): void;
  onFocus(epic: string | undefined): void;
}

/**
 * Label column plus a month track. Dragging on a project row drafts an epic, on an epic row a feature under it; on an
 * undated card's row it sets the card's dates. A click without a drag selects the month for C.
 */
const PlanRow = memo(function PlanRow(props: RowProps) {
  const { t } = useTranslation();
  const hours = useHours();
  const { row, card, top, timeline: tl, monthWidth } = props;
  const [creating, setCreating] = useState<{ x0: number; x1: number } | null>(null);
  const p = row.project;
  const height = heightOf(row);
  const dated = !!card?.startDate && !!card.dueDate;
  const draft: Omit<Draft, 'startDate' | 'dueDate'> | null =
    row.kind === 'project'
      ? { project: p.key, parent: null, type: 'epic', top: top + PAD }
      : row.kind === 'epic'
        ? { project: p.key, parent: row.card.key, type: 'feature', top: top + (ROW_H - 20) / 2 }
        : null;
  const canDate = !!card && !dated;

  function onTrackPointerDown(e: ReactPointerEvent<HTMLDivElement>) {
    if (e.target !== e.currentTarget || e.button !== 0 || (!draft && !canDate)) return;
    const x0 = e.nativeEvent.offsetX;
    e.currentTarget.setPointerCapture(e.pointerId);
    setCreating({ x0, x1: x0 });
  }

  const label =
    row.kind === 'project' ? (
      <div className="flex flex-col gap-1">
        <span className="flex min-w-0 items-center gap-1.5 font-semibold">
          <span className="size-2.5 shrink-0 rounded-[2px]" style={{ background: `var(--proj-${p.color})` }} aria-hidden="true" />
          <span className="truncate" title={p.name}>
            {p.name}
          </span>
        </span>
        <CapacityEditor workspace={props.workspace} projectKey={p.key} capacity={p.capacity} />
        {(p.health.cardsWithoutDates > 0 || p.health.cardsWithoutEffort > 0) && (
          <span className="text-xs text-warn italic">
            {p.health.cardsWithoutDates > 0 && t('plan.undated', { count: p.health.cardsWithoutDates })}
            {p.health.cardsWithoutDates > 0 && p.health.cardsWithoutEffort > 0 && ' · '}
            {p.health.cardsWithoutEffort > 0 && t('plan.noEffort', { count: p.health.cardsWithoutEffort })}
          </span>
        )}
      </div>
    ) : row.kind === 'epic' || row.kind === 'loose' ? (
      <div className="group flex min-w-0 flex-1 items-center gap-1.5">
        {props.focused ? (
          <Layers size={14} strokeWidth={1.5} aria-hidden="true" className="shrink-0 text-rev" />
        ) : (
          <button
            type="button"
            onClick={() => props.onToggle(row.key)}
            aria-expanded={row.open}
            aria-label={t(row.open ? 'plan.fold' : 'plan.unfold')}
            className="shrink-0 rounded-r1 p-0.5 text-mute hover:bg-sel hover:text-ink"
          >
            {row.open ? <ChevronDown size={14} strokeWidth={1.5} /> : <ChevronRight size={14} strokeWidth={1.5} />}
          </button>
        )}
        {row.kind === 'epic' ? (
          <button type="button" onClick={() => props.onOpen(row.card.key)} className="flex min-w-0 items-center gap-1.5 text-left font-semibold hover:underline">
            <span className="font-mono text-sm font-normal text-mute">{row.card.key}</span>
            <span className="truncate">{row.card.title}</span>
          </button>
        ) : (
          <span className="truncate text-mute italic">{t('plan.noEpic')}</span>
        )}
        <span className="ml-auto shrink-0 font-mono text-xs text-mute tabular-nums">{row.count}</span>
        {row.kind === 'epic' && !props.focused && (
          <button
            type="button"
            onClick={() => props.onFocus(row.card.key)}
            aria-label={t('plan.focusEpic', { key: row.card.key })}
            title={t('plan.focusEpic', { key: row.card.key })}
            className="shrink-0 rounded-r1 p-0.5 text-mute opacity-0 group-hover:opacity-100 hover:bg-sel hover:text-ink focus:opacity-100"
          >
            <Crosshair size={14} strokeWidth={1.5} />
          </button>
        )}
      </div>
    ) : (
      <button type="button" onClick={() => props.onOpen(row.card.key)} className="flex min-w-0 flex-1 items-center gap-1.5 pl-6 text-left hover:underline">
        <StatusRing state={row.card.state} />
        <span className="font-mono text-sm text-mute">{row.card.key}</span>
        <span className="truncate">{row.card.title}</span>
        <span className="ml-auto shrink-0 font-mono text-xs text-mute tabular-nums">{row.card.effort ? hours(row.card.effort) : ''}</span>
      </button>
    );

  return (
    <div
      className={`absolute left-0 flex border-b ${row.kind === 'project' ? 'border-line bg-sunk/60' : 'border-line/40'}`}
      style={{ top, height, width: LABEL_W + tl.width }}
    >
      <div
        className={`sticky left-0 z-10 flex shrink-0 border-r border-line px-3 ${row.kind === 'project' ? 'items-start bg-sunk py-2' : 'items-center bg-panel'}`}
        style={{ width: LABEL_W }}
      >
        {label}
      </div>
      <div
        className={`relative shrink-0 ${draft || canDate ? 'cursor-crosshair' : ''}`}
        style={{
          width: tl.width,
          backgroundImage: `repeating-linear-gradient(to right, color-mix(in srgb, var(--line) 70%, transparent) 0 1px, transparent 1px ${monthWidth}px)`,
        }}
        onPointerDown={onTrackPointerDown}
        onPointerMove={(e) => creating && setCreating({ ...creating, x1: e.nativeEvent.offsetX })}
        onPointerUp={(e) => {
          if (!creating) return;
          const a = Math.min(creating.x0, creating.x1);
          const b = Math.max(creating.x0, creating.x1);
          setCreating(null);
          e.currentTarget.releasePointerCapture(e.pointerId);
          if (canDate) {
            if (b - a >= 6) props.onCommit(card.key, tl.dateAt(a), tl.dateAt(b));
          } else if (draft && b - a < 6) {
            props.onSelectMonth(row.key, props.months[Math.floor(a / monthWidth)]!, { ...draft, top: draft.top - top });
          } else if (draft) {
            props.onDraft({ ...draft, startDate: tl.dateAt(a), dueDate: tl.dateAt(b) });
          }
        }}
        data-row={row.key}
      >
        {props.selectedMonth && (
          <div
            className="pointer-events-none absolute inset-y-0 bg-sel"
            style={{ left: tl.x(props.selectedMonth), width: monthWidth }}
            aria-label={t('plan.selectedMonth')}
          />
        )}
        {row.kind === 'project' && p.health.noDatedWork && (
          <span className="pointer-events-none absolute top-2 left-3 flex items-center gap-1.5 text-sm text-warn italic">
            <span className="inline-flex size-3.5 items-center justify-center rounded-full bg-warn text-[9px] font-bold text-on-acc not-italic">!</span>
            {t('plan.noDatedWork')}
          </span>
        )}
        {canDate && !creating && <span className="pointer-events-none absolute inset-y-0 left-3 flex items-center text-sm text-mute italic">{t('plan.dragToDate')}</span>}
        {creating && (
          <div
            className="pointer-events-none absolute h-5 rounded-r1 border-[1.5px] border-dashed border-acc-ink bg-sel"
            style={{ left: Math.min(creating.x0, creating.x1), width: Math.abs(creating.x1 - creating.x0), top: row.kind === 'project' ? PAD : (ROW_H - 20) / 2 }}
          />
        )}
        {card && dated && (
          <Card
            card={card}
            color={p.color}
            left={tl.x(card.startDate!)}
            width={tl.xEnd(card.dueDate!) - tl.x(card.startDate!)}
            top={(ROW_H - 20) / 2}
            violated={props.late}
            timeline={tl}
            onDrag={props.onDrag}
            onCommit={props.onCommit}
            onOpen={props.onOpen}
          />
        )}
        {row.kind === 'project' && <CapacityBand months={p.months} monthWidth={monthWidth} top={height - BAND_H - 2} />}
      </div>
    </div>
  );
}, sameRowProps);

/** Rows are rebuilt on every drag frame; a row only re-renders when what it shows changed (one project's rows). */
function sameRowProps(a: RowProps, b: RowProps) {
  const { row: ra, ...restA } = a;
  const { row: rb, ...restB } = b;
  const sameRow =
    ra.kind === rb.kind &&
    ra.key === rb.key &&
    (ra.kind === 'project' ? ra.project === rb.project : ra.project.color === rb.project.color) &&
    ('open' in ra && 'open' in rb ? ra.open === rb.open && ra.count === rb.count : true);
  return sameRow && (Object.keys(restA) as (keyof typeof restA)[]).every((k) => restA[k] === restB[k]);
}

interface ToolbarProps {
  workspace: string;
  plan?: PlanResponse;
  epics: { card: PlanCard; project: ProjectView }[];
  focus: string | null;
  onFocus(epic: string | undefined): void;
  allFolded: boolean;
  onFoldAll(fold: boolean): void;
  onHorizon(from: string, to: string): void;
}

function Toolbar({ workspace, plan, epics, focus, onFocus, allFolded, onFoldAll, onHorizon }: ToolbarProps) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [name, setName] = useState('');
  const [date, setDate] = useState('');
  const addMilestone = useMutation({
    ...createMilestoneMutation(),
    onSuccess: () => {
      setName('');
      void qc.invalidateQueries({ predicate: (q) => (q.queryKey[0] as { _id?: string })._id === 'getPlan' });
    },
  });
  if (!plan) return null;
  const field = 'h-7 rounded-r1 border border-line bg-panel px-2 font-mono text-sm';
  const current = epics.find((e) => e.card.key === focus)?.card;
  return (
    <div className="flex flex-wrap items-center gap-3 border-b border-line px-4 py-2 text-sm">
      <Picker
        label={t('plan.focus')}
        placeholder={t('plan.focusPlaceholder')}
        options={[
          { value: ALL, label: t('plan.allEpics'), icon: <Layers size={14} strokeWidth={1.5} aria-hidden="true" className="text-mute" /> },
          ...epics.map(({ card, project }) => ({
            value: card.key,
            label: card.title,
            hint: card.key,
            group: project.name,
            icon: <span className="size-2.5 rounded-[2px]" style={{ background: `var(--proj-${project.color})` }} />,
          })),
        ]}
        selected={[focus ?? ALL]}
        onPick={(v) => onFocus(v === ALL ? undefined : v)}
      >
        <Crosshair size={14} strokeWidth={1.5} aria-hidden="true" className={focus ? 'text-acc-ink' : 'text-mute'} />
        <span className="max-w-60 truncate">{current ? current.title : t('plan.allEpics')}</span>
        <ChevronDown size={14} strokeWidth={1.5} aria-hidden="true" className="text-mute" />
      </Picker>
      {!focus && (
        <Button variant="ghost" className="h-7 px-2" onClick={() => onFoldAll(!allFolded)}>
          {t(allFolded ? 'plan.unfoldAll' : 'plan.foldAll')}
        </Button>
      )}
      <label className="flex items-center gap-1.5">
        {t('plan.from')}
        <input type="month" className={field} value={plan.from.slice(0, 7)} onChange={(e) => e.target.value && onHorizon(e.target.value, plan.to.slice(0, 7))} />
      </label>
      <label className="flex items-center gap-1.5">
        {t('plan.to')}
        <input type="month" className={field} value={plan.to.slice(0, 7)} onChange={(e) => e.target.value && onHorizon(plan.from.slice(0, 7), e.target.value)} />
      </label>
      <form
        className="ml-auto flex items-center gap-1.5"
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim() && date) addMilestone.mutate({ path: { workspace }, body: { name: name.trim(), date } });
        }}
      >
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder={t('plan.milestoneName')} aria-label={t('plan.milestoneName')} className={`${field} font-ui`} />
        <input type="date" value={date} onChange={(e) => setDate(e.target.value)} aria-label={t('plan.milestoneDate')} className={field} />
        <Button type="submit" className="h-7 px-2" disabled={!name.trim() || !date}>
          {t('plan.addMilestone')}
        </Button>
      </form>
    </div>
  );
}

interface CardProps {
  card: PlanCard;
  color: string;
  left: number;
  width: number;
  top: number;
  violated: boolean;
  timeline: ReturnType<typeof timeline>;
  onDrag(o: Override | null): void;
  onCommit(key: string, startDate: string, dueDate: string): void;
  onOpen(key: string): void;
}

/** Outline card: project-coloured border, progress along the bottom; drag to move, drag an edge to re-date. */
const Card = memo(function Card({ card, color, left, width, top, violated, timeline: tl, onDrag, onCommit, onOpen }: CardProps) {
  const { t } = useTranslation();
  const drag = useRef<{ mode: 'move' | 'start' | 'end'; x: number; moved: boolean; start: string; due: string } | null>(null);
  const progress = card.total > 0 ? card.done / card.total : 0;

  function next(d: { mode: 'move' | 'start' | 'end'; start: string; due: string }, dx: number) {
    const span = dayOf(d.due) - dayOf(d.start);
    if (d.mode === 'move') {
      const start = tl.dateAt(tl.x(d.start) + dx + 0.5);
      return { start, due: isoOf(dayOf(start) + span) };
    }
    if (d.mode === 'start') {
      const start = tl.dateAt(tl.x(d.start) + dx + 0.5);
      return { start: start > d.due ? d.due : start, due: d.due };
    }
    const due = tl.dateAt(tl.xEnd(d.due) + dx - 0.5);
    return { start: d.start, due: due < d.start ? d.start : due };
  }

  // Edge handles mark themselves with data-edge; anywhere else on the card moves it.
  function onPointerDown(e: ReactPointerEvent) {
    if (e.button !== 0) return;
    e.stopPropagation();
    e.currentTarget.setPointerCapture(e.pointerId);
    const mode = ((e.target as HTMLElement).dataset.edge ?? 'move') as 'move' | 'start' | 'end';
    drag.current = { mode, x: e.clientX, moved: false, start: card.startDate!, due: card.dueDate! };
  }

  return (
    <div
      role="button"
      tabIndex={0}
      data-key={card.key}
      aria-label={`${card.key} ${card.title}`}
      title={`${card.key} ${card.title}${card.effort ? ` · ${card.effort}h` : ''}`}
      onKeyDown={(e) => e.key === 'Enter' && onOpen(card.key)}
      onPointerDown={onPointerDown}
      onPointerMove={(e) => {
        const d = drag.current;
        if (!d) return;
        const dx = e.clientX - d.x;
        if (!d.moved && Math.abs(dx) < 3) return;
        d.moved = true;
        const { start, due } = next(d, dx);
        onDrag({ key: card.key, startDate: start, dueDate: due });
      }}
      onPointerUp={(e) => {
        const d = drag.current;
        drag.current = null;
        if (!d) return;
        if (!d.moved) {
          onOpen(card.key);
          return;
        }
        const { start, due } = next(d, e.clientX - d.x);
        if (start !== d.start || due !== d.due) onCommit(card.key, start, due);
        else onDrag(null);
      }}
      className={`group absolute flex h-5 cursor-grab items-center overflow-hidden rounded-r1 border-[1.5px] bg-panel px-1.5 text-sm select-none active:cursor-grabbing ${card.type === 'epic' ? 'font-bold' : ''} ${violated ? 'border-bad outline-[1.5px] outline-offset-2 outline-bad outline-dashed' : ''}`}
      style={{ left, width: Math.max(width, 6), top, borderColor: violated ? undefined : `var(--proj-${color})` }}
    >
      <span className="absolute inset-y-0 left-0 w-1.5 cursor-ew-resize" data-edge="start" aria-hidden="true" />
      {violated && (
        <span className="mr-1 inline-flex size-3 shrink-0 items-center justify-center rounded-full bg-bad text-[8px] font-bold text-on-acc" aria-label={t('plan.violated')}>
          !
        </span>
      )}
      <span className="truncate">{card.title}</span>
      <span className="absolute inset-y-0 right-0 w-1.5 cursor-ew-resize" data-edge="end" aria-hidden="true" />
      <span className="absolute bottom-0 left-0 h-[2.5px]" style={{ width: `${progress * 100}%`, background: `var(--proj-${color})` }} aria-hidden="true" />
    </div>
  );
});

/** Per month: demand bar (ok if covered, bad if over) beside a supply outline, from a shared baseline (design guide). */
function CapacityBand({ months, monthWidth, top, label }: { months: PlanMonth[]; monthWidth: number; top: number; label?: string }) {
  const { t, i18n } = useTranslation();
  const number = new Intl.NumberFormat(i18n.language, { maximumFractionDigits: 1 });
  const max = Math.max(1, ...months.map((m) => Math.max(m.demandPeople, m.supplyPeople)));
  const h = BAND_H - 12;
  return (
    <div className="pointer-events-auto absolute left-0 flex" style={{ top, height: BAND_H - 4 }} aria-label={label ?? t('plan.capacity')}>
      {months.map((m) => {
        const over = m.demandPeople > m.supplyPeople + 0.005;
        const text = t('plan.needsHas', { needs: number.format(m.demandPeople), has: number.format(m.supplyPeople) });
        return (
          <div key={m.month} className="relative flex shrink-0 items-end justify-center gap-0.5" style={{ width: monthWidth, height: BAND_H - 4 }} title={text}>
            <span className={`w-[9px] rounded-t-[2px] ${over ? 'bg-bad' : 'bg-ok'}`} style={{ height: Math.max(m.demandPeople > 0 ? 2 : 0, (m.demandPeople / max) * h) }} />
            <span className="w-[9px] rounded-t-[2px] border-[1.5px] border-b-0 border-mute" style={{ height: Math.max(m.supplyPeople > 0 ? 2 : 0, (m.supplyPeople / max) * h) }} />
            <span className="sr-only">{text}</span>
          </div>
        );
      })}
    </div>
  );
}

function TotalsRow({ top, months, monthWidth }: { top: number; months: PlanMonth[]; monthWidth: number }) {
  const { t } = useTranslation();
  const short = months.filter((m) => (m.gapPeople ?? 0) > 0.005).length;
  return (
    <div className="absolute left-0 flex border-t-2 border-line bg-sunk" style={{ top, height: BAND_H + PAD * 2 }}>
      <div className="sticky left-0 z-10 flex shrink-0 flex-col justify-center gap-0.5 bg-sunk px-2" style={{ width: LABEL_W }}>
        <span className="font-semibold">{t('plan.total')}</span>
        <span className={`text-xs ${short ? 'text-bad' : 'text-mute'}`}>{short ? t('plan.shortMonths', { count: short }) : t('plan.covered')}</span>
      </div>
      <div className="relative" style={{ width: months.length * monthWidth }}>
        <CapacityBand months={months} monthWidth={monthWidth} top={PAD + 2} label={t('plan.total')} />
      </div>
    </div>
  );
}

function DraftInput({ draft, left, width, top, onCancel, onSubmit }: { draft: Draft; left: number; width: number; top: number; onCancel(): void; onSubmit(title: string): void }) {
  const { t } = useTranslation();
  const [title, setTitle] = useState('');
  return (
    <form
      className="absolute z-20"
      style={{ left, top, width }}
      onSubmit={(e) => {
        e.preventDefault();
        if (title.trim()) onSubmit(title.trim());
      }}
    >
      <input
        autoFocus
        value={title}
        onChange={(e) => setTitle(e.target.value)}
        onKeyDown={(e) => e.key === 'Escape' && onCancel()}
        onBlur={() => !title.trim() && onCancel()}
        placeholder={t(draft.type === 'epic' ? 'plan.newEpic' : 'plan.newFeature', { from: draft.startDate, to: draft.dueDate })}
        aria-label={t(draft.type === 'epic' ? 'plan.newEpicLabel' : 'plan.newFeatureLabel')}
        className="h-6 w-full rounded-r1 border-[1.5px] border-acc-ink bg-panel px-1.5 text-sm shadow-float"
      />
    </form>
  );
}
