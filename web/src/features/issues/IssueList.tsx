import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { listItems, loadedStateIssues, useIssues, useStateIssues, type IssueFilters } from '../../api/issues';
import type { IssuePriority, IssueState, IssueSummary } from '../../api/gen/types.gen';
import { useUi } from '../../app/UiContext';
import { LoadMore } from '../../components/LoadMore';
import { Avatar, Labels, PriorityIcon, StatusRing, TypeTag } from '../../components/ui';
import { useHours } from '../../lib/format';
import { isClosed, priorities, states } from '../../lib/issues';
import { useShortcuts } from '../../lib/shortcuts';

const priorityOrder = (p: IssuePriority) => priorities.indexOf(p);
const byPriority = (a: IssueSummary, b: IssueSummary) =>
  priorityOrder(a.priority) - priorityOrder(b.priority) || b.updatedAt.localeCompare(a.updatedAt);

/**
 * Grouped by state in workflow order; urgent first inside a group (design guide: list row, group header). Each group
 * pages on its own (server order: priority), so it is complete up to where it has scrolled.
 */
export function IssueList({ workspace, filters, emptyText }: { workspace: string; filters: IssueFilters; emptyText: string }) {
  const { t } = useTranslation();
  const ui = useUi();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const query: IssueFilters = { ...filters, sort: 'priority' };
  // One row is enough to tell an empty project from groups that are still loading.
  const { data: probe } = useIssues(workspace, { ...filters, limit: 1 });

  const open = (key: string) => void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: key }) });
  const step = (delta: number) => {
    // Read from the cache at key time: the groups own their queries.
    const flat = states.flatMap((state) => loadedStateIssues(qc, workspace, query, state).sort(byPriority));
    if (flat.length === 0) return;
    const index = flat.findIndex((i) => i.key === ui.activeKey);
    const next = flat[Math.min(flat.length - 1, Math.max(0, index + delta))] ?? flat[0]!;
    ui.setActiveKey(next.key);
  };

  useShortcuts({
    j: () => step(1),
    k: () => step(-1),
    arrowdown: () => step(1),
    arrowup: () => step(-1),
    enter: () => ui.activeKey && open(ui.activeKey),
  });

  const activeRow = useRef<HTMLButtonElement | null>(null);
  // Braces: newer Chromium returns a promise from scrollIntoView, and an effect may only return a cleanup function.
  useEffect(() => {
    activeRow.current?.scrollIntoView({ block: 'nearest' });
  }, [ui.activeKey]);

  if (probe?.items.length === 0) {
    return <p className="p-8 text-center text-mute">{emptyText}</p>;
  }

  return (
    <div role="list" aria-label={t('issue.list')}>
      {states.map((state) => (
        <Group
          key={state}
          workspace={workspace}
          query={query}
          state={state}
          activeKey={ui.activeKey}
          activeRow={activeRow}
          onOpen={(key) => {
            ui.setActiveKey(key);
            open(key);
          }}
        />
      ))}
    </div>
  );
}

function Group({
  workspace,
  query,
  state,
  activeKey,
  activeRow,
  onOpen,
}: {
  workspace: string;
  query: IssueFilters;
  state: IssueState;
  activeKey: string | null;
  activeRow: React.Ref<HTMLButtonElement>;
  onOpen(key: string): void;
}) {
  const { t } = useTranslation();
  const hours = useHours();
  const pages = useStateIssues(workspace, query, state);
  // A realtime update changes a row's state in place; it leaves this group at once and joins its new one on refetch.
  const items = listItems(pages.data).filter((i) => i.state === state).sort(byPriority);
  if (items.length === 0) return null;
  const more = pages.hasNextPage ? '+' : '';
  return (
    <section aria-label={t(`state.${state}`)}>
      <h2 className="sticky top-0 z-10 flex h-[var(--row)] items-center gap-2 bg-sunk px-4 font-semibold">
        <StatusRing state={state} />
        {t(`state.${state}`)}
        <span className="font-normal text-mute">
          {items.length}
          {more}
        </span>
        <span className="ml-auto font-mono text-sm font-normal text-mute tabular-nums">
          {hours(items.reduce((sum, i) => sum + (i.effort ?? 0), 0))}
          {more}
        </span>
      </h2>
      {items.map((issue) => (
        <IssueRow
          key={issue.key}
          issue={issue}
          active={issue.key === activeKey}
          rowRef={issue.key === activeKey ? activeRow : undefined}
          onOpen={() => onOpen(issue.key)}
        />
      ))}
      <LoadMore {...pages} />
    </section>
  );
}

function IssueRow({
  issue,
  active,
  onOpen,
  rowRef,
}: {
  issue: IssueSummary;
  active: boolean;
  onOpen(): void;
  rowRef?: React.Ref<HTMLButtonElement>;
}) {
  const hours = useHours();
  return (
    <button
      ref={rowRef}
      type="button"
      role="listitem"
      onClick={onOpen}
      data-key={issue.key}
      className={`grid h-[var(--row)] w-full grid-cols-[14px_66px_minmax(0,1fr)_auto_52px_22px] items-center gap-3 border-b border-line/60 px-4 text-left hover:bg-sel/50 ${active ? 'bg-sel shadow-[inset_2px_0_0_var(--acc-ink)]' : ''}`}
    >
      <StatusRing state={issue.state} />
      <span className="font-mono text-sm text-mute">{issue.key}</span>
      <span className="flex min-w-0 items-center gap-1.5">
        <PriorityIcon priority={issue.priority} />
        <TypeTag type={issue.type} />
        <span className={`truncate ${isClosed(issue.state) ? 'text-mute' : ''} ${issue.state === 'canceled' ? 'line-through' : ''}`}>
          {issue.title}
        </span>
      </span>
      <Labels names={issue.labels} />
      <span className="text-right font-mono text-sm text-mute tabular-nums">{issue.effort ? hours(issue.effort) : ''}</span>
      <Avatar member={issue.assignee} />
    </button>
  );
}
