import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { memo, useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import {
  createIssueMutation,
  createMilestoneMutation,
  getPlanOptions,
  getPlanQueryKey,
  updateIssueMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import type { PlanCard, PlanMonth, PlanProject, PlanResponse } from '../../api/gen/types.gen';
import { Topbar } from '../../app/AppShell';
import { useUi } from '../../app/UiContext';
import { Button } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { CapacityEditor } from './CapacityEditor';
import { dayOf, isoOf, lanes, lastDayOf, monthsBetween, monthsFor, sumMonths, timeline } from './demand';

export type Zoom = 'month' | 'quarter' | 'year';
const monthWidths: Record<Zoom, number> = { month: 160, quarter: 72, year: 34 };
const HEADER_W = 132;
const LANE_H = 26;
const BAND_H = 40;
const PAD = 8;
const MONTH_ROW_H = 28;
// Milestone labels get their own strip under the month names, so they never cover them.
const MILESTONE_ROW_H = 20;

type Override = { key: string; startDate: string; dueDate: string };
type Draft = { project: string; startDate: string; dueDate: string };

/** Delivery plan v1 (docs/delivery-plan.md): projects × months, cards, capacity band, milestones, dependencies. */
export function PlanPage() {
  const { t } = useTranslation();
  const ui = useUi();
  const qc = useQueryClient();
  const toast = useToast();
  const navigate = useNavigate();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const search = useSearch({ strict: false }) as { from?: string; to?: string; zoom?: Zoom; project?: string };
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
  const [cell, setCell] = useState<{ project: string; month: string } | null>(null);

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
  const headerH = MONTH_ROW_H + (plan?.milestones.length ? MILESTONE_ROW_H : 0);
  const t2 = useMemo(() => timeline(months.length ? months : ['2027-01-01'], monthWidth), [months, monthWidth]);

  const layout = useCallback(
    (p: PlanProject, cards: PlanCard[], hoursPerWeek: number) => {
      const packed = lanes(cards);
      return { ...p, cards, lane: packed.lane, laneCount: packed.count, months: monthsFor(months, cards, p.capacity, hoursPerWeek) };
    },
    [months],
  );
  const base = useMemo(() => (plan ? plan.projects.map((p) => layout(p, p.cards, plan.hoursPerWeek)) : []), [plan, layout]);

  // Live while dragging: only the dragged card's project is recomputed (lanes kept, so rows don't jump), the rest
  // keep their objects so memoised rows skip rendering; the total is re-summed.
  const view = useMemo(() => {
    if (!plan) return null;
    const projects = override
      ? base.map((p) => {
          if (!p.cards.some((c) => c.key === override.key)) return p;
          const cards = p.cards.map((c) => (c.key === override.key ? { ...c, ...override } : c));
          return { ...p, cards, months: monthsFor(months, cards, p.capacity, plan.hoursPerWeek) };
        })
      : base;
    return { projects, totals: sumMonths(projects.map((p) => p.months), months, plan.hoursPerWeek) };
  }, [plan, base, override, months]);

  const mutateUpdate = update.mutate;
  const onCommit = useCallback(
    (key: string, startDate: string, dueDate: string) => mutateUpdate({ path: { workspace, key }, body: { startDate, dueDate } }),
    [mutateUpdate, workspace],
  );
  const onOpen = useCallback(
    (key: string) => void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: key }) }),
    [navigate],
  );
  const onSelectMonth = useCallback((project: string, month: string) => setCell({ project, month }), []);

  // C with a selected month cell creates there; otherwise C keeps opening the issue dialog.
  const { setCreateOverride } = ui;
  useEffect(() => {
    setCreateOverride(cell ? () => setDraft({ project: cell.project, startDate: cell.month, dueDate: lastDayOf(cell.month) }) : null);
    return () => setCreateOverride(null);
  }, [cell, setCreateOverride]);

  const rows = useMemo(
    () =>
      (view?.projects ?? []).reduce<{ key: string; top: number; height: number }[]>((acc, p) => {
        const top = acc.length ? acc[acc.length - 1]!.top + acc[acc.length - 1]!.height : headerH;
        return [...acc, { key: p.key, top, height: PAD * 2 + p.laneCount * LANE_H + BAND_H }];
      }, []),
    [view, headerH],
  );
  const totalHeight = (rows.at(-1)?.top ?? headerH) + (rows.at(-1)?.height ?? 0) + BAND_H + PAD * 2;

  const cardPos = useMemo(() => {
    const pos = new Map<string, { x1: number; x2: number; y: number }>();
    view?.projects.forEach((p, i) => {
      for (const c of p.cards) {
        if (!c.startDate || !c.dueDate) continue;
        const y = rows[i]!.top + PAD + (p.lane.get(c.key) ?? 0) * LANE_H + 10;
        pos.set(c.key, { x1: HEADER_W + t2.x(c.startDate), x2: HEADER_W + t2.xEnd(c.dueDate), y });
      }
    });
    return pos;
  }, [view, rows, t2]);

  const violated = useMemo(() => new Set(plan?.dependencies.filter((d) => d.violated).map((d) => d.blocked)), [plan]);

  const setSearch = (patch: Record<string, string | undefined>) =>
    void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, ...patch }) });

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
      <Topbar crumbs={[t('plan.title')]} tabs={zoomTabs} />
      <Toolbar workspace={workspace} plan={plan} onHorizon={(from, to) => setSearch({ from, to })} />
      <main className="relative min-h-0 flex-1 overflow-auto" aria-label={t('plan.title')}>
        {isPending && <p className="p-8 text-center text-mute">{t('common.loading')}</p>}
        {plan && view && (
          <div className="relative" style={{ width: HEADER_W + t2.width, height: totalHeight }}>
            <MonthHeader months={months} monthWidth={monthWidth} zoom={zoom} height={headerH} />
            {view.projects.map((p, i) => (
              <ProjectRow
                key={p.key}
                workspace={workspace}
                project={p}
                top={rows[i]!.top}
                height={rows[i]!.height}
                months={months}
                monthWidth={monthWidth}
                timeline={t2}
                violated={violated}
                selectedMonth={cell?.project === p.key ? cell.month : null}
                onSelectMonth={onSelectMonth}
                onDraft={setDraft}
                onDrag={setOverride}
                onCommit={onCommit}
                onOpen={onOpen}
              />
            ))}
            <TotalsRow top={rows.at(-1) ? rows.at(-1)!.top + rows.at(-1)!.height : headerH} months={view.totals} monthWidth={monthWidth} />
            {plan.milestones.map((m) => (
              <div
                key={m.id}
                className="pointer-events-none absolute top-0 z-30 border-l-[1.5px] border-dashed border-acc-ink"
                style={{ left: HEADER_W + t2.x(m.date), height: totalHeight }}
              >
                <span className="absolute left-1 text-xs leading-5 font-bold whitespace-nowrap text-acc-ink" style={{ top: MONTH_ROW_H }}>
                  {m.name}
                  {m.projectKey ? ` · ${m.projectKey}` : ''}
                </span>
              </div>
            ))}
            <Dependencies dependencies={plan.dependencies} positions={cardPos} width={HEADER_W + t2.width} height={totalHeight} />
            {draft && (
              <DraftInput
                draft={draft}
                left={HEADER_W + t2.x(draft.startDate)}
                width={Math.max(120, t2.xEnd(draft.dueDate) - t2.x(draft.startDate))}
                top={rows.find((r) => r.key === draft.project)!.top + PAD}
                onCancel={() => setDraft(null)}
                onSubmit={(title) => {
                  create.mutate({ path: { workspace }, body: { project: draft.project, title, type: 'feature', state: 'backlog', startDate: draft.startDate, dueDate: draft.dueDate } });
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

function Toolbar({ workspace, plan, onHorizon }: { workspace: string; plan?: PlanResponse; onHorizon(from: string, to: string): void }) {
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
  return (
    <div className="flex flex-wrap items-center gap-3 border-b border-line px-4 py-2 text-sm">
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

function MonthHeader({ months, monthWidth, zoom, height }: { months: string[]; monthWidth: number; zoom: Zoom; height: number }) {
  const { i18n } = useTranslation();
  const format = new Intl.DateTimeFormat(i18n.language, { month: zoom === 'year' ? 'narrow' : 'short', year: zoom === 'month' ? 'numeric' : undefined, timeZone: 'UTC' });
  return (
    <div className="sticky top-0 z-20 flex border-b border-line bg-bg" style={{ height }}>
      <div className="sticky left-0 z-10 shrink-0 bg-bg" style={{ width: HEADER_W }} />
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
  project: PlanProject & { lane: Map<string, number>; laneCount: number };
  top: number;
  height: number;
  months: string[];
  monthWidth: number;
  timeline: ReturnType<typeof timeline>;
  violated: Set<string>;
  selectedMonth: string | null;
  onSelectMonth(project: string, month: string): void;
  onDraft(draft: Draft | null): void;
  onDrag(o: Override | null): void;
  onCommit(key: string, startDate: string, dueDate: string): void;
  onOpen(key: string): void;
}

const ProjectRow = memo(function ProjectRow(props: RowProps) {
  const { t } = useTranslation();
  const { project: p, top, height, timeline: tl, monthWidth } = props;
  const [creating, setCreating] = useState<{ x0: number; x1: number } | null>(null);
  const undated = p.cards.filter((c) => !c.startDate || !c.dueDate).length;

  function onTrackPointerDown(e: ReactPointerEvent<HTMLDivElement>) {
    if (e.target !== e.currentTarget || e.button !== 0) return;
    const x0 = e.nativeEvent.offsetX;
    e.currentTarget.setPointerCapture(e.pointerId);
    setCreating({ x0, x1: x0 });
  }

  return (
    <div className="absolute left-0 flex border-b border-line" style={{ top, height, width: HEADER_W + tl.width }}>
      <div className="sticky left-0 z-10 flex shrink-0 flex-col gap-1 border-r border-line bg-panel p-2" style={{ width: HEADER_W }}>
        <span className="flex min-w-0 items-center gap-1.5 font-semibold">
          <span className="size-2.5 shrink-0 rounded-[2px]" style={{ background: `var(--proj-${p.color})` }} aria-hidden="true" />
          <span className="truncate" title={p.name}>
            {p.name}
          </span>
        </span>
        <CapacityEditor workspace={props.workspace} projectKey={p.key} capacity={p.capacity} />
        {(p.health.cardsWithoutDates > 0 || p.health.cardsWithoutEffort > 0) && (
          <span className="text-xs text-warn italic">
            {p.health.cardsWithoutDates > 0 && t('plan.undated', { count: undated })}
            {p.health.cardsWithoutDates > 0 && p.health.cardsWithoutEffort > 0 && ' · '}
            {p.health.cardsWithoutEffort > 0 && t('plan.noEffort', { count: p.health.cardsWithoutEffort })}
          </span>
        )}
      </div>
      <div
        className="relative shrink-0 cursor-crosshair"
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
          if (b - a < 6) {
            props.onSelectMonth(p.key, props.months[Math.floor(a / monthWidth)]!);
          } else {
            props.onDraft({ project: p.key, startDate: tl.dateAt(a), dueDate: tl.dateAt(b) });
          }
          e.currentTarget.releasePointerCapture(e.pointerId);
        }}
        data-project={p.key}
      >
        {props.selectedMonth && (
          <div
            className="pointer-events-none absolute inset-y-0 bg-sel"
            style={{ left: tl.x(props.selectedMonth), width: monthWidth }}
            aria-label={t('plan.selectedMonth')}
          />
        )}
        {p.health.noDatedWork && (
          <span className="pointer-events-none absolute top-3 left-3 flex items-center gap-1.5 text-sm text-warn italic">
            <span className="inline-flex size-3.5 items-center justify-center rounded-full bg-warn text-[9px] font-bold text-on-acc not-italic">!</span>
            {t('plan.noDatedWork')}
          </span>
        )}
        {creating && (
          <div
            className="pointer-events-none absolute h-5 rounded-r1 border-[1.5px] border-dashed border-acc-ink bg-sel"
            style={{ left: Math.min(creating.x0, creating.x1), width: Math.abs(creating.x1 - creating.x0), top: PAD }}
          />
        )}
        {p.cards.map((card) =>
          card.startDate && card.dueDate ? (
            <Card
              key={card.key}
              card={card}
              color={p.color}
              left={tl.x(card.startDate)}
              width={tl.xEnd(card.dueDate) - tl.x(card.startDate)}
              top={PAD + (p.lane.get(card.key) ?? 0) * LANE_H}
              violated={props.violated.has(card.key)}
              timeline={tl}
              onDrag={props.onDrag}
              onCommit={props.onCommit}
              onOpen={props.onOpen}
            />
          ) : null,
        )}
        <CapacityBand months={p.months} monthWidth={monthWidth} top={height - BAND_H - 2} />
      </div>
    </div>
  );
});

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
      <div className="sticky left-0 z-10 flex shrink-0 flex-col justify-center gap-0.5 bg-sunk px-2" style={{ width: HEADER_W }}>
        <span className="font-semibold">{t('plan.total')}</span>
        <span className={`text-xs ${short ? 'text-bad' : 'text-mute'}`}>{short ? t('plan.shortMonths', { count: short }) : t('plan.covered')}</span>
      </div>
      <div className="relative" style={{ width: months.length * monthWidth }}>
        <CapacityBand months={months} monthWidth={monthWidth} top={PAD + 2} label={t('plan.total')} />
      </div>
    </div>
  );
}

function Dependencies({
  dependencies,
  positions,
  width,
  height,
}: {
  dependencies: PlanResponse['dependencies'];
  positions: Map<string, { x1: number; x2: number; y: number }>;
  width: number;
  height: number;
}) {
  return (
    <svg className="pointer-events-none absolute top-0 left-0" width={width} height={height} aria-hidden="true">
      {dependencies.map((d) => {
        const a = positions.get(d.blocker);
        const b = positions.get(d.blocked);
        if (!a || !b) return null;
        const mid = Math.max(24, Math.abs(b.x1 - a.x2) / 2);
        return (
          <path
            key={`${d.blocker}-${d.blocked}`}
            d={`M ${a.x2} ${a.y} C ${a.x2 + mid} ${a.y}, ${b.x1 - mid} ${b.y}, ${b.x1} ${b.y}`}
            fill="none"
            strokeWidth="1.5"
            className={d.violated ? 'stroke-bad' : 'stroke-mute'}
            strokeDasharray={d.violated ? '4 3' : undefined}
          />
        );
      })}
    </svg>
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
        placeholder={t('plan.newFeature', { from: draft.startDate, to: draft.dueDate })}
        aria-label={t('plan.newFeatureLabel')}
        className="h-6 w-full rounded-r1 border-[1.5px] border-acc-ink bg-panel px-1.5 text-sm shadow-float"
      />
    </form>
  );
}
