import { useNavigate } from '@tanstack/react-router';
import { useEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import type { IssuePriority, IssueState, IssueSummary } from '../../api/gen/types.gen';
import { useUi } from '../../app/UiContext';
import { Avatar, Labels, PriorityIcon, StatusRing, TypeTag } from '../../components/ui';
import { useHours } from '../../lib/format';
import { isClosed, priorities, states } from '../../lib/issues';
import { useShortcuts } from '../../lib/shortcuts';

const priorityOrder = (p: IssuePriority) => priorities.indexOf(p);

/** Grouped by state in workflow order; urgent first inside a group (design guide: list row, group header). */
export function IssueList({ issues, emptyText }: { issues: IssueSummary[]; emptyText: string }) {
  const { t } = useTranslation();
  const ui = useUi();
  const navigate = useNavigate();
  const hours = useHours();

  const groups = useMemo(
    () =>
      states
        .map((state) => ({
          state,
          items: issues
            .filter((i) => i.state === state)
            .sort((a, b) => priorityOrder(a.priority) - priorityOrder(b.priority) || b.updatedAt.localeCompare(a.updatedAt)),
        }))
        .filter((g) => g.items.length > 0),
    [issues],
  );
  const flat = useMemo(() => groups.flatMap((g) => g.items), [groups]);

  const open = (key: string) => void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: key }) });
  const step = (delta: number) => {
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

  if (issues.length === 0) {
    return <p className="p-8 text-center text-mute">{emptyText}</p>;
  }

  return (
    <div role="list" aria-label={t('issue.list')}>
      {groups.map((group) => (
        <section key={group.state} aria-label={t(`state.${group.state}`)}>
          <h2 className="sticky top-0 z-10 flex h-[var(--row)] items-center gap-2 bg-sunk px-4 font-semibold">
            <StatusRing state={group.state as IssueState} />
            {t(`state.${group.state}`)}
            <span className="font-normal text-mute">{group.items.length}</span>
            <span className="ml-auto font-mono text-sm font-normal text-mute tabular-nums">
              {hours(group.items.reduce((sum, i) => sum + (i.effort ?? 0), 0))}
            </span>
          </h2>
          {group.items.map((issue) => (
            <IssueRow
              key={issue.key}
              issue={issue}
              active={issue.key === ui.activeKey}
              rowRef={issue.key === ui.activeKey ? activeRow : undefined}
              onOpen={() => {
                ui.setActiveKey(issue.key);
                open(issue.key);
              }}
            />
          ))}
        </section>
      ))}
    </div>
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
