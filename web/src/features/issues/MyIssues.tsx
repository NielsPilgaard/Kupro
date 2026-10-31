import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { listProjectsOptions } from '../../api/gen/@tanstack/react-query.gen';
import type { IssuePriority, IssueState, IssueSummary, ProjectColor } from '../../api/gen/types.gen';
import { useUi } from '../../app/UiContext';
import { Labels, PriorityIcon, StatusRing, TypeTag } from '../../components/ui';
import { useDate, useHours } from '../../lib/format';
import { isClosed, priorities, projectColorClass, projectKeyOf } from '../../lib/issues';
import { useShortcuts } from '../../lib/shortcuts';

const DAY = 86_400_000;
const sectionStates: readonly IssueState[] = ['inProgress', 'inReview', 'todo', 'backlog', 'done'];
const priorityOrder = (p: IssuePriority) => priorities.indexOf(p);

/** Whole days from today to an ISO date; negative when overdue. */
function daysUntil(iso: string) {
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  return Math.round((new Date(`${iso}T00:00:00`).getTime() - today.getTime()) / DAY);
}

/**
 * The signed-in member's work: a few counts on top, then one card per state that wraps into columns on wide screens.
 * Done shows the last 14 days only; J/K and ↵ work as in the list.
 */
export function MyIssues({ workspace, issues, emptyText }: { workspace: string; issues: IssueSummary[]; emptyText: string }) {
  const { t } = useTranslation();
  const ui = useUi();
  const navigate = useNavigate();
  const hours = useHours();
  const { data: projects } = useQuery(listProjectsOptions({ path: { workspace } }));
  const colors = useMemo(() => new Map(projects?.map((p) => [p.key, p.color])), [projects]);

  const sections = useMemo(
    () =>
      sectionStates
        .map((state) => ({
          state,
          items: issues
            .filter((i) => i.state === state && (state !== 'done' || daysUntil(i.updatedAt.slice(0, 10)) >= -14))
            .sort(
              (a, b) =>
                priorityOrder(a.priority) - priorityOrder(b.priority) ||
                (a.dueDate ?? '9999').localeCompare(b.dueDate ?? '9999') ||
                b.updatedAt.localeCompare(a.updatedAt),
            ),
        }))
        .filter((s) => s.items.length > 0),
    [issues],
  );
  const flat = useMemo(() => sections.flatMap((s) => s.items), [sections]);

  const open = issues.filter((i) => !isClosed(i.state));
  const dueSoon = open.filter((i) => i.dueDate && daysUntil(i.dueDate) <= 7);
  const overdue = dueSoon.filter((i) => daysUntil(i.dueDate!) < 0).length;

  const show = (key: string) => {
    ui.setActiveKey(key);
    void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: key }) });
  };
  const step = (delta: number) => {
    if (flat.length === 0) return;
    const index = flat.findIndex((i) => i.key === ui.activeKey);
    ui.setActiveKey((flat[Math.min(flat.length - 1, Math.max(0, index + delta))] ?? flat[0]!).key);
  };
  useShortcuts({
    j: () => step(1),
    k: () => step(-1),
    arrowdown: () => step(1),
    arrowup: () => step(-1),
    enter: () => ui.activeKey && show(ui.activeKey),
  });

  const activeRow = useRef<HTMLButtonElement | null>(null);
  useEffect(() => {
    activeRow.current?.scrollIntoView({ block: 'nearest' });
  }, [ui.activeKey]);

  if (issues.length === 0) return <p className="p-8 text-center text-md text-mute">{emptyText}</p>;

  return (
    <div className="mx-auto flex w-full max-w-[1600px] flex-col gap-6 p-4 md:p-6 3xl:max-w-[2200px] 3xl:gap-8 3xl:p-8">
      <dl className="grid grid-cols-2 gap-3 md:grid-cols-4 3xl:gap-4">
        <Stat label={t('myIssues.open')} value={open.length} />
        <Stat label={t('state.inProgress')} value={open.filter((i) => i.state === 'inProgress').length} />
        <Stat
          label={t('myIssues.dueSoon')}
          value={dueSoon.length}
          note={overdue > 0 ? t('myIssues.overdue', { count: overdue }) : undefined}
        />
        <Stat label={t('myIssues.remaining')} value={hours(open.reduce((sum, i) => sum + (i.effort ?? 0), 0))} />
      </dl>

      <div className="grid items-start gap-4 xl:grid-cols-2 3xl:grid-cols-3 3xl:gap-6">
        {sections.map((section) => (
          <section key={section.state} aria-label={t(`state.${section.state}`)} className="overflow-hidden rounded-r2 border border-line bg-panel">
            <h2 className="flex h-11 items-center gap-2 border-b border-line bg-sunk px-4 text-md font-semibold">
              <StatusRing state={section.state} />
              {t(section.state === 'done' ? 'myIssues.recentlyDone' : `state.${section.state}`)}
              <span className="font-normal text-mute">{section.items.length}</span>
              <span className="ml-auto font-mono text-sm font-normal text-mute tabular-nums">
                {hours(section.items.reduce((sum, i) => sum + (i.effort ?? 0), 0))}
              </span>
            </h2>
            <ul>
              {section.items.map((issue) => (
                <li key={issue.key}>
                  <Row
                    issue={issue}
                    color={colors.get(projectKeyOf(issue.key))}
                    active={issue.key === ui.activeKey}
                    rowRef={issue.key === ui.activeKey ? activeRow : undefined}
                    onOpen={() => show(issue.key)}
                  />
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
    </div>
  );
}

function Stat({ label, value, note }: { label: string; value: number | string; note?: string }) {
  return (
    <div className="flex flex-col gap-1 rounded-r2 border border-line bg-panel px-4 py-3 3xl:px-5 3xl:py-4">
      <dt className="text-sm text-mute 3xl:text-base">{label}</dt>
      <dd className="flex items-baseline gap-2">
        <span className="font-display text-2xl font-bold tabular-nums">{value}</span>
        {note && <span className="text-sm font-semibold text-bad">{note}</span>}
      </dd>
    </div>
  );
}

function Row({
  issue,
  color,
  active,
  onOpen,
  rowRef,
}: {
  issue: IssueSummary;
  color?: ProjectColor;
  active: boolean;
  onOpen(): void;
  rowRef?: React.Ref<HTMLButtonElement>;
}) {
  const { t } = useTranslation();
  const hours = useHours();
  const date = useDate();
  const days = issue.dueDate && !isClosed(issue.state) ? daysUntil(issue.dueDate) : null;
  const dueTone = days === null ? 'text-mute' : days < 0 ? 'font-semibold text-bad' : days <= 3 ? 'text-warn' : 'text-mute';
  return (
    <button
      ref={rowRef}
      type="button"
      onClick={onOpen}
      data-key={issue.key}
      className={`flex h-11 w-full items-center gap-3 border-b border-line/60 px-4 text-left text-md hover:bg-sel/50 3xl:h-12 ${active ? 'bg-sel shadow-[inset_2px_0_0_var(--acc-ink)]' : ''}`}
    >
      <PriorityIcon priority={issue.priority} />
      <span className="flex w-20 shrink-0 items-center gap-1.5 font-mono text-sm text-mute">
        <span className={`size-2 shrink-0 rounded-[2px] ${color ? projectColorClass[color] : 'bg-mute'}`} aria-hidden="true" />
        {issue.key}
      </span>
      <span className="flex min-w-0 flex-1 items-center gap-1.5">
        <TypeTag type={issue.type} />
        <span className={`truncate ${isClosed(issue.state) ? 'text-mute' : ''}`}>{issue.title}</span>
      </span>
      <span className="hidden 2xl:flex">
        <Labels names={issue.labels} />
      </span>
      {issue.dueDate && (
        <span className={`shrink-0 text-sm ${dueTone}`} title={t('issue.dueDate')}>
          {date(issue.dueDate)}
        </span>
      )}
      <span className="w-12 shrink-0 text-right font-mono text-sm text-mute tabular-nums">{issue.effort ? hours(issue.effort) : ''}</span>
    </button>
  );
}
