import {
  closestCorners,
  DndContext,
  DragOverlay,
  KeyboardSensor,
  PointerSensor,
  pointerWithin,
  useDroppable,
  useSensor,
  useSensors,
  type CollisionDetection,
  type DragEndEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import { SortableContext, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { listItems, loadedStateIssues, useMoveIssue, useStateIssues, type IssueFilters } from '../../api/issues';
import type { IssueState, IssueSummary } from '../../api/gen/types.gen';
import { useUi } from '../../app/UiContext';
import { LoadMore } from '../../components/LoadMore';
import { Avatar, Labels, StatusRing, TypeTag } from '../../components/ui';
import { useHours } from '../../lib/format';
import { boardStates, compareRank } from '../../lib/issues';

const columnId = (state: IssueState) => `column:${state}`;

/** The droppable under the pointer wins. Corners alone let the card's own slot beat a tall empty column it was dropped
 *  into; corners stay the fallback for the gaps between columns and for the keyboard sensor (no pointer). A card beats
 *  its column, which would otherwise mean "drop at the end". */
const collisionDetection: CollisionDetection = (args) => {
  const hits = pointerWithin(args);
  if (hits.length === 0) return closestCorners(args);
  const isColumn = (id: unknown) => String(id).startsWith('column:');
  return [...hits.filter((h) => !isColumn(h.id)), ...hits.filter((h) => isColumn(h.id))];
};

/**
 * Columns per state; cards in manual rank order. Each column pages on its own, so it is complete up to where it has
 * scrolled. Dropping sends one move (state + neighbours).
 */
export function Board({ workspace, filters }: { workspace: string; filters: IssueFilters }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const move = useMoveIssue(workspace);
  const [dragging, setDragging] = useState<IssueSummary | null>(null);
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  // Read from the cache at event time: the columns own their queries.
  const loaded = () =>
    boardStates.map((state) => ({ state, items: loadedStateIssues(qc, workspace, filters, state).sort(compareRank) }));

  function onDragStart(event: DragStartEvent) {
    setDragging(loaded().flatMap((c) => c.items).find((i) => i.key === event.active.id) ?? null);
  }

  function onDragEnd({ active, over }: DragEndEvent) {
    setDragging(null);
    if (!over || active.id === over.id) return;
    const columns = loaded();
    const issues = columns.flatMap((c) => c.items);
    const issue = issues.find((i) => i.key === active.id);
    if (!issue) return;

    const overIssue = issues.find((i) => i.key === over.id);
    const state = overIssue?.state ?? (String(over.id).replace('column:', '') as IssueState);
    const column = columns.find((c) => c.state === state)!.items.filter((i) => i.key !== issue.key);

    let index = overIssue ? column.findIndex((i) => i.key === overIssue.key) : column.length;
    const translated = active.rect.current.translated;
    if (overIssue && translated && translated.top + translated.height / 2 > over.rect.top + over.rect.height / 2) index += 1;

    const after = column[index - 1]?.key ?? null;
    const before = column[index]?.key ?? null;
    if (state === issue.state && after === prevKey(columns, issue) && before === nextKey(columns, issue)) return;
    move.mutate({ path: { workspace, key: issue.key }, body: { state, after, before } });
  }

  return (
    <DndContext sensors={sensors} collisionDetection={collisionDetection} onDragStart={onDragStart} onDragEnd={onDragEnd}>
      <div className="flex h-full gap-3 overflow-x-auto p-4" role="region" aria-label={t('views.board')}>
        {boardStates.map((state) => (
          <Column key={state} workspace={workspace} filters={filters} state={state} />
        ))}
      </div>
      <DragOverlay dropAnimation={null}>{dragging && <Card issue={dragging} overlay />}</DragOverlay>
    </DndContext>
  );
}

function prevKey(columns: { state: IssueState; items: IssueSummary[] }[], issue: IssueSummary) {
  const items = columns.find((c) => c.state === issue.state)?.items ?? [];
  return items[items.findIndex((i) => i.key === issue.key) - 1]?.key ?? null;
}

function nextKey(columns: { state: IssueState; items: IssueSummary[] }[], issue: IssueSummary) {
  const items = columns.find((c) => c.state === issue.state)?.items ?? [];
  return items[items.findIndex((i) => i.key === issue.key) + 1]?.key ?? null;
}

function Column({ workspace, filters, state }: { workspace: string; filters: IssueFilters; state: IssueState }) {
  const { t } = useTranslation();
  const { setNodeRef, isOver } = useDroppable({ id: columnId(state) });
  const pages = useStateIssues(workspace, filters, state);
  // A realtime update changes a card's state in place; it leaves this column at once and joins its new one on refetch.
  const items = listItems(pages.data).filter((i) => i.state === state).sort(compareRank);
  return (
    <section
      ref={setNodeRef}
      aria-label={t(`state.${state}`)}
      data-state={state}
      className={`flex min-h-0 min-w-64 flex-1 basis-0 flex-col gap-2 rounded-r2 p-2 3xl:text-md ${isOver ? 'bg-sel' : 'bg-sunk'}`}
    >
      <h2 className="flex items-center gap-2 px-1 py-1 font-semibold">
        <StatusRing state={state} />
        {t(`state.${state}`)}
        <span className="font-normal text-mute">
          {items.length}
          {pages.hasNextPage && '+'}
        </span>
      </h2>
      <SortableContext items={items.map((i) => i.key)} strategy={verticalListSortingStrategy}>
        {/* Each column scrolls on its own; the padding keeps the active card's outline inside the scroll box. */}
        <div className="-m-1 flex min-h-12 flex-1 flex-col gap-2 overflow-y-auto p-1">
          {items.map((issue) => (
            <SortableCard key={issue.key} issue={issue} />
          ))}
          <LoadMore {...pages} />
        </div>
      </SortableContext>
    </section>
  );
}

function SortableCard({ issue }: { issue: IssueSummary }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: issue.key });
  return (
    <div
      ref={setNodeRef}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      className={isDragging ? 'rounded-r2 border border-dashed border-line opacity-40' : ''}
      {...attributes}
      {...listeners}
    >
      <Card issue={issue} />
    </div>
  );
}

function Card({ issue, overlay = false }: { issue: IssueSummary; overlay?: boolean }) {
  const ui = useUi();
  const navigate = useNavigate();
  const hours = useHours();
  return (
    <article
      data-key={issue.key}
      onClick={() => {
        ui.setActiveKey(issue.key);
        void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: issue.key }) });
      }}
      className={`flex cursor-pointer flex-col gap-2 rounded-r2 border border-line bg-panel p-2.5 ${overlay ? '-rotate-[1.5deg] shadow-float outline-[1.5px] outline-acc-ink' : 'hover:border-mute'} ${ui.activeKey === issue.key ? 'outline-[1.5px] outline-acc-ink' : ''}`}
    >
      <div className="flex items-center justify-between">
        <span className="font-mono text-sm text-mute">{issue.key}</span>
        <StatusRing state={issue.state} />
      </div>
      <div className="flex items-start gap-1.5">
        <TypeTag type={issue.type} />
        <span className="line-clamp-3">{issue.title}</span>
      </div>
      <div className="flex items-center gap-2">
        <Labels names={issue.labels} />
        <span className="ml-auto font-mono text-sm text-mute tabular-nums">{issue.effort ? hours(issue.effort) : ''}</span>
        <Avatar member={issue.assignee} />
      </div>
    </article>
  );
}
