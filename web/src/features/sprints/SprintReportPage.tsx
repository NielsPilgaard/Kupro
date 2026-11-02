import { useMutation, useQuery, useQueryClient, type Query } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  closeSprintMutation,
  createSprintMutation,
  getSprintBurndownOptions,
  getSprintReviewOptions,
} from '../../api/gen/@tanstack/react-query.gen';
import type { CloseOutcome, SprintBurndown, SprintResponse, SprintReview, SprintReviewIssue } from '../../api/gen/types.gen';
import { Topbar } from '../../app/AppShell';
import { Avatar, Button, Kbd, StatusRing } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { useDate, useHours } from '../../lib/format';
import { useShortcuts } from '../../lib/shortcuts';
import { BurndownChart } from './BurndownChart';
import { canSplit, closeItems, closeTotals, defaultPlan, highest, hoursLeftOf, nextSprint, planIsValid, type RowPlan } from './sprintClose';

const OUTCOMES: CloseOutcome[] = ['carry', 'split', 'backlog', 'cancel', 'done'];
const SHORTCUTS = ['j', 'k', 'x', 'c', 's', 'b', 'd'] as const;

const isSprintOrIssueQuery = (q: Query) => {
  const id = (q.queryKey[0] as { _id?: string } | undefined)?._id;
  return ['listSprints', 'listIssues', 'getSprint', 'getSprintReview', 'getSprintBurndown', 'getIssue', 'listIssueActivity'].includes(id ?? '');
};

/** The close review while a sprint is active; the same page is the read-only sprint report afterwards (task 028). */
export function SprintReportPage() {
  const { t } = useTranslation();
  const { workspace, sprintId } = useParams({ strict: false }) as { workspace: string; sprintId: string };
  const review = useQuery(getSprintReviewOptions({ path: { workspace, id: sprintId } }));
  const burndown = useQuery(getSprintBurndownOptions({ path: { workspace, id: sprintId }, query: { by: 'assignee' } }));
  const sprint = review.data?.sprint;

  return (
    <>
      <Topbar crumbs={[t('sprint.title'), sprint?.name ?? '']} />
      <main className="flex min-h-0 flex-1 flex-col overflow-auto">
        {review.isError && <p className="p-4 text-bad">{t('sprintClose.loadFailed')}</p>}
        {review.data && (
          <div className="flex flex-col gap-6 p-4 pb-0">
            <Header review={review.data} />
            <Stats review={review.data} />
            {burndown.data && <Charts burndown={burndown.data} atStart={review.data.summary.atStart} />}
          </div>
        )}
        {review.data &&
          (review.data.sprint.state === 'active' ? (
            <CloseReview workspace={workspace} review={review.data} />
          ) : (
            <div className="flex flex-col gap-6 p-4">
              <IssueTable title={t('sprintClose.leftSprint')} issues={review.data.unfinished} workspace={workspace} />
              <IssueTable title={t('sprintClose.completed', { count: review.data.completed.length })} issues={review.data.completed} workspace={workspace} />
            </div>
          ))}
      </main>
    </>
  );
}

function Header({ review }: { review: SprintReview }) {
  const { t } = useTranslation();
  const date = useDate();
  const { sprint } = review;
  return (
    <header className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
      <h1 className="font-display text-lg font-bold">{sprint.state === 'active' ? t('sprintClose.closeTitle', { name: sprint.name }) : t('sprintClose.reportTitle', { name: sprint.name })}</h1>
      <span className="font-mono text-sm text-mute">
        {date(sprint.startDate)} – {date(sprint.endDate)}
      </span>
      {sprint.goal && <span className="text-mute">{sprint.goal}</span>}
    </header>
  );
}

function Stats({ review }: { review: SprintReview }) {
  const { t } = useTranslation();
  const hours = useHours();
  const { summary } = review;
  const stats = [
    [t('sprintClose.stats.atStart'), hours(summary.atStart)],
    [t('sprintClose.stats.added'), hours(summary.added)],
    [t('sprintClose.stats.completed'), hours(summary.completed)],
    [t('sprintClose.stats.carried'), hours(summary.carried + summary.split)],
    [t('sprintClose.stats.completion'), `${summary.completionPercent} %`],
  ];
  return (
    <dl className="flex flex-wrap gap-x-8 gap-y-2">
      {stats.map(([label, value]) => (
        <div key={label} className="flex flex-col">
          <dt className="text-sm text-mute">{label}</dt>
          <dd className="m-0 font-mono text-lg tabular-nums">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

function Charts({ burndown, atStart }: { burndown: SprintBurndown; atStart: number }) {
  const { t } = useTranslation();
  const hours = useHours();
  const assignees = burndown.assignees ?? [];
  if (!burndown.points.some((p) => p.remaining != null)) {
    return <p className="text-mute">{t('sprintClose.noBurndown')}</p>;
  }

  const shared = highest(assignees.map((a) => a.points));
  return (
    <section className="flex flex-col gap-4" aria-label={t('sprintClose.burndown')}>
      <div className="flex max-w-3xl flex-col gap-1">
        <h2 className="font-semibold">{t('sprintClose.burndown')}</h2>
        <BurndownChart
          title={t('sprintClose.burndown')}
          points={burndown.points}
          ideal={burndown.ideal}
          atStart={atStart}
          endDate={burndown.endDate}
          max={Math.max(highest([burndown.points]), atStart)}
        />
        <p className="flex gap-4 text-xs text-mute">
          <span className="font-semibold text-acc-ink">{t('sprintClose.chart.remaining')}</span>
          <span>{t('sprintClose.chart.scopeDashed')}</span>
          <span>{t('sprintClose.chart.ideal')}</span>
          <span>{t('sprintClose.chart.weekends')}</span>
        </p>
      </div>
      {assignees.length > 0 && (
        <div className="flex flex-col gap-3">
          <h2 className="font-semibold">{t('sprintClose.perAssignee')}</h2>
          <div className="grid grid-cols-[repeat(auto-fill,minmax(240px,1fr))] gap-4">
            {assignees.map((a) => {
              const name = a.assignee?.name ?? t('sprintClose.unassigned');
              return (
                <div key={a.assignee?.id ?? 'unassigned'} className="flex flex-col gap-1">
                  <span className="flex items-center gap-2 text-sm font-semibold">
                    <Avatar member={a.assignee} size={18} />
                    {name}
                  </span>
                  <BurndownChart title={t('sprintClose.burndownOf', { name })} points={a.points} endDate={burndown.endDate} max={shared} compact />
                </div>
              );
            })}
          </div>
          <table className="w-full max-w-3xl border-collapse text-sm">
            <thead>
              <tr className="border-b border-line text-left text-mute">
                <th scope="col" className="py-1 font-normal">{t('issue.assignee')}</th>
                <th scope="col" className="py-1 text-right font-normal">{t('sprintClose.stats.atStart')}</th>
                <th scope="col" className="py-1 text-right font-normal">{t('sprintClose.stats.added')}</th>
                <th scope="col" className="py-1 text-right font-normal">{t('sprintClose.stats.completed')}</th>
                <th scope="col" className="py-1 text-right font-normal">{t('sprintClose.stats.remaining')}</th>
                <th scope="col" className="py-1 text-right font-normal">{t('sprintClose.stats.carried')}</th>
              </tr>
            </thead>
            <tbody className="font-mono tabular-nums">
              {assignees.map((a) => (
                <tr key={a.assignee?.id ?? 'unassigned'} className="border-b border-line">
                  <th scope="row" className="py-1 text-left font-sans font-normal">{a.assignee?.name ?? t('sprintClose.unassigned')}</th>
                  <td className="py-1 text-right">{hours(a.atStart)}</td>
                  <td className="py-1 text-right">{hours(a.added)}</td>
                  <td className="py-1 text-right">{hours(a.completed)}</td>
                  <td className="py-1 text-right">{hours(a.remaining)}</td>
                  <td className="py-1 text-right">{hours(a.carried)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function IssueLinkCell({ workspace, issueKey }: { workspace: string; issueKey: string }) {
  return (
    <Link to="." params={{ workspace }} search={(s: Record<string, unknown>) => ({ ...s, issue: issueKey })} className="font-mono text-sm text-mute hover:text-ink">
      {issueKey}
    </Link>
  );
}

/** Read-only rows: the report's lists and the collapsed Completed list of the review. */
function IssueTable({ title, issues, workspace, collapsed = false }: { title: string; issues: SprintReviewIssue[]; workspace: string; collapsed?: boolean }) {
  const { t } = useTranslation();
  const hours = useHours();
  const rows = (
    <ul className="m-0 flex list-none flex-col p-0">
      {issues.map((i) => (
        <li key={i.key} className="flex h-[var(--row)] items-center gap-3 border-b border-line">
          <IssueLinkCell workspace={workspace} issueKey={i.key} />
          <StatusRing state={i.state} />
          <span className="min-w-0 flex-1 truncate">{i.title}</span>
          {i.continuedIn ? (
            <span className="text-sm text-mute">
              {t('sprintClose.continuedIn')} <IssueLinkCell workspace={workspace} issueKey={i.continuedIn} />
            </span>
          ) : i.outcome ? (
            <span className="text-sm text-mute">{t(`sprintClose.outcomes.${i.outcome}`)}</span>
          ) : null}
          <span className="font-mono text-sm text-mute tabular-nums">{i.effort == null ? '' : hours(i.effort)}</span>
          <Avatar member={i.assignee} size={18} />
        </li>
      ))}
    </ul>
  );
  if (collapsed) {
    return (
      <details>
        <summary className="cursor-pointer font-semibold">{title}</summary>
        {rows}
      </details>
    );
  }

  return (
    <section aria-label={title} className="flex flex-col gap-1">
      <h2 className="font-semibold">{title}</h2>
      {issues.length > 0 ? rows : <p className="text-mute">{t('sprintClose.none')}</p>}
    </section>
  );
}

function CloseReview({ workspace, review }: { workspace: string; review: SprintReview }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as { issue?: string; meeting?: string };
  const { sprint, unfinished, completed, targets } = review;
  const [plans, setPlans] = useState<Record<string, RowPlan>>({});
  const [selected, setSelected] = useState<string[]>([]);
  const [cursor, setCursor] = useState(0);
  const [focusSplit, setFocusSplit] = useState<string | null>(null);
  const [targetId, setTargetId] = useState<string | null>(null);
  const [startNext, setStartNext] = useState(false);
  const target = targets.find((s) => s.id === targetId) ?? targets[0];
  const refresh = () => void qc.invalidateQueries({ predicate: isSprintOrIssueQuery });

  const close = useMutation({
    ...closeSprintMutation(),
    onSuccess: (result) => {
      toast.info(t('sprintClose.closed', { name: result.sprint.name, count: result.moved }));
      if (result.splits.length) {
        toast.info(t('sprintClose.closedSplits', { keys: result.splits.map((s) => s.continuation).join(', ') }));
      }

      if (result.skipped.length) {
        toast.info(t('sprintClose.closedSkipped', { keys: result.skipped.join(', ') }));
      }

      refresh();
    },
    onError: () => toast.error(t('sprint.closeFailed')),
  });
  const create = useMutation({
    ...createSprintMutation(),
    onSuccess: (created: SprintResponse) => {
      setTargetId(created.id);
      refresh();
    },
    onError: () => toast.error(t('sprint.createFailed')),
  });

  const setOutcome = (keys: string[], outcome: CloseOutcome) => {
    setPlans((current) => {
      const next = { ...current };
      for (const key of keys) next[key] = { ...(current[key] ?? defaultPlan), outcome };
      return next;
    });
    if (outcome === 'split' && keys.length === 1) setFocusSplit(keys[0] ?? null);
  };

  const row = unfinished[Math.min(cursor, unfinished.length - 1)] as SprintReviewIssue | undefined;
  const choose = (outcome: CloseOutcome) => {
    if (selected.length > 0 && outcome !== 'split') setOutcome(selected, outcome);
    else if (row && (outcome !== 'split' || canSplit(row))) setOutcome([row.key], outcome);
  };
  // Off while a panel is open over the page: it has its own single-key shortcuts.
  const keys = !search.issue && !search.meeting;
  useShortcuts({
    j: keys && (() => setCursor((c) => Math.min(c + 1, unfinished.length - 1))),
    k: keys && (() => setCursor((c) => Math.max(c - 1, 0))),
    x: keys && (() => row && setSelected((s) => (s.includes(row.key) ? s.filter((k) => k !== row.key) : [...s, row.key]))),
    c: keys && (() => choose('carry')),
    s: keys && (() => choose('split')),
    b: keys && (() => choose('backlog')),
    delete: keys && (() => choose('cancel')),
    d: keys && (() => choose('done')),
  });

  const needsTarget = unfinished.some((i) => ['carry', 'split'].includes(plans[i.key]?.outcome ?? 'carry'));
  const valid = planIsValid(unfinished, plans) && (!needsTarget || !!target);
  const totals = closeTotals(unfinished, plans);
  const hours = useHours();

  const summary = [
    totals.carry.count > 0 && t('sprintClose.footer.carry', { count: totals.carry.count, hours: hours(totals.carry.hours) }),
    totals.split.count > 0 && t('sprintClose.footer.split', { count: totals.split.count, hours: hours(totals.split.hoursDone) }),
    totals.backlog.count > 0 && t('sprintClose.footer.backlog', { count: totals.backlog.count }),
    totals.cancel.count > 0 && t('sprintClose.footer.cancel', { count: totals.cancel.count }),
    totals.done.count > 0 && t('sprintClose.footer.done', { count: totals.done.count }),
  ].filter(Boolean);

  return (
    <>
      <div className="flex flex-1 flex-col gap-6 p-4">
        <section aria-label={t('sprintClose.unfinished')} className="flex flex-col gap-2">
          <div className="flex flex-wrap items-center gap-3">
            <h2 className="font-semibold">{t('sprintClose.unfinished')}</h2>
            {targets.length > 0 ? (
              <label className="flex items-center gap-2 text-sm text-mute">
                {t('sprintClose.target')}
                <select
                  value={target?.id ?? ''}
                  onChange={(e) => setTargetId(e.target.value)}
                  className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2 text-ink"
                >
                  {targets.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name}
                    </option>
                  ))}
                </select>
              </label>
            ) : (
              <Button
                disabled={create.isPending}
                onClick={() => create.mutate({ path: { workspace }, body: { project: sprint.projectKey, ...nextSprint(sprint) } })}
              >
                {t('sprintClose.createNext')}
              </Button>
            )}
            {selected.length > 0 && (
              <div role="group" aria-label={t('sprintClose.bulk', { count: selected.length })} className="flex items-center gap-2">
                <span className="text-sm text-mute">{t('sprintClose.bulk', { count: selected.length })}</span>
                {OUTCOMES.filter((o) => o !== 'split').map((o) => (
                  <Button key={o} className="h-7" onClick={() => setOutcome(selected, o)}>
                    {t(`sprintClose.outcomes.${o}`)}
                  </Button>
                ))}
              </div>
            )}
          </div>
          {unfinished.length === 0 ? (
            <p className="text-mute">{t('sprintClose.allFinished')}</p>
          ) : (
            <ul className="m-0 flex list-none flex-col p-0">
              {unfinished.map((issue, index) => (
                <UnfinishedRow
                  key={issue.key}
                  workspace={workspace}
                  issue={issue}
                  plan={plans[issue.key] ?? defaultPlan}
                  active={row?.key === issue.key}
                  selected={selected.includes(issue.key)}
                  focusHours={focusSplit === issue.key}
                  targetName={target?.name}
                  onFocusRow={() => setCursor(index)}
                  onSelect={(on) => setSelected((s) => (on ? [...s, issue.key] : s.filter((k) => k !== issue.key)))}
                  onOutcome={(o) => setOutcome([issue.key], o)}
                  onHours={(hoursLeft) => setPlans((p) => ({ ...p, [issue.key]: { ...(p[issue.key] ?? defaultPlan), hoursLeft } }))}
                />
              ))}
            </ul>
          )}
          <p className="flex flex-wrap gap-3 text-xs text-mute">
            {SHORTCUTS.map((key) => (
              <span key={key}>
                <Kbd>{key.toUpperCase()}</Kbd> {t(`sprintClose.keys.${key}`)}
              </span>
            ))}
          </p>
        </section>
        <IssueTable title={t('sprintClose.completed', { count: completed.length })} issues={completed} workspace={workspace} collapsed />
      </div>
      <footer className="sticky bottom-0 flex flex-wrap items-center gap-x-4 gap-y-2 border-t border-line bg-panel px-4 py-2">
        <span className="min-w-0 flex-1 text-sm" data-testid="close-summary">
          {summary.join(' · ') || t('sprintClose.footer.nothing')}
        </span>
        {target && (
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={startNext} onChange={(e) => setStartNext(e.target.checked)} />
            {t('sprintClose.startNext', { name: target.name })}
          </label>
        )}
        <Button
          variant="primary"
          disabled={!valid || close.isPending}
          title={needsTarget && !target ? t('sprint.noNext') : undefined}
          onClick={() =>
            close.mutate({
              path: { workspace, id: sprint.id },
              body: { target: target?.id, items: closeItems(unfinished, plans), startNext: startNext && !!target, moveUnfinishedTo: 'next' },
            })
          }
        >
          {t('sprint.close')}
        </Button>
        <Button variant="ghost" onClick={() => void navigate({ to: '/w/$workspace/sprints', params: { workspace }, search: { project: sprint.projectKey } })}>
          {t('common.cancel')}
        </Button>
      </footer>
    </>
  );
}

function UnfinishedRow({
  workspace,
  issue,
  plan,
  active,
  selected,
  focusHours,
  targetName,
  onFocusRow,
  onSelect,
  onOutcome,
  onHours,
}: {
  workspace: string;
  issue: SprintReviewIssue;
  plan: RowPlan;
  active: boolean;
  selected: boolean;
  focusHours: boolean;
  targetName?: string;
  onFocusRow(): void;
  onSelect(on: boolean): void;
  onOutcome(outcome: CloseOutcome): void;
  onHours(value: string): void;
}) {
  const { t } = useTranslation();
  const hours = useHours();
  const input = useRef<HTMLInputElement>(null);
  const isSplit = plan.outcome === 'split';
  useEffect(() => {
    if (isSplit && focusHours) input.current?.focus();
  }, [isSplit, focusHours]);
  const left = hoursLeftOf(issue, plan);
  const splittable = canSplit(issue);

  return (
    <li className={`flex flex-col gap-1 border-b border-line py-1 ${active ? 'bg-sel' : ''}`} data-key={issue.key} onFocusCapture={onFocusRow} onMouseDown={onFocusRow}>
      <div className="flex min-h-[var(--control)] flex-wrap items-center gap-3 px-1">
        <input type="checkbox" checked={selected} onChange={(e) => onSelect(e.target.checked)} aria-label={t('sprintClose.selectRow', { key: issue.key })} />
        <IssueLinkCell workspace={workspace} issueKey={issue.key} />
        <StatusRing state={issue.state} />
        <span className="min-w-40 flex-1 truncate">{issue.title}</span>
        <Avatar member={issue.assignee} size={18} />
        <span className="w-12 text-right font-mono text-sm text-mute tabular-nums">{issue.effort == null ? '–' : hours(issue.effort)}</span>
        <div role="radiogroup" aria-label={t('sprintClose.outcomeOf', { key: issue.key })} className="flex rounded-r1 bg-sunk p-0.5">
          {OUTCOMES.map((o) => (
            <button
              key={o}
              type="button"
              role="radio"
              aria-checked={plan.outcome === o}
              disabled={o === 'split' && !splittable}
              title={o === 'split' && !splittable ? t(issue.hasChildren ? 'sprintClose.splitHasChildren' : 'sprintClose.splitNoEffort') : undefined}
              onClick={() => onOutcome(o)}
              className={`h-6 rounded-[4px] px-2 text-sm disabled:opacity-50 ${plan.outcome === o ? 'bg-panel font-semibold shadow-sm' : 'text-mute hover:text-ink'}`}
            >
              {t(`sprintClose.outcomes.${o}`)}
            </button>
          ))}
        </div>
      </div>
      {isSplit && (
        <div className="flex flex-wrap items-center gap-2 pl-7 text-sm">
          <label className="flex items-center gap-2">
            {t('sprintClose.hoursLeft')}
            <input
              ref={input}
              inputMode="decimal"
              value={plan.hoursLeft}
              onChange={(e) => onHours(e.target.value)}
              aria-invalid={plan.hoursLeft !== '' && left === null}
              className="h-7 w-16 rounded-r1 border border-line bg-panel px-2 font-mono"
            />
          </label>
          {left !== null ? (
            <span className="text-mute">
              {t('sprintClose.splitPreview', { key: issue.key, done: hours((issue.effort ?? 0) - left), left: hours(left), target: targetName ?? '' })}
            </span>
          ) : (
            plan.hoursLeft !== '' && <span className="text-bad">{t('sprintClose.splitInvalid', { hours: hours(issue.effort ?? 0) })}</span>
          )}
        </div>
      )}
    </li>
  );
}
