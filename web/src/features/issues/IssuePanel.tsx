import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { Ban, ChevronDown, Copy, IterationCw, Link2, Maximize2, Minimize2, OctagonAlert, Tag, X } from 'lucide-react';
import { useEffect, useState, useSyncExternalStore, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import {
  addIssueRelationMutation,
  getIssueQueryKey,
  listIssueActivityQueryKey,
  listLabelsOptions,
  listSprintsOptions,
  removeIssueRelationMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import type { IssueDetail, IssueType, RelationKind, UpdateIssueRequest } from '../../api/gen/types.gen';
import { useMarkdownUpload } from '../../api/attachments';
import { useIssue, useMembers, useUpdateIssue } from '../../api/issues';
import { useUi, type PalettePage } from '../../app/UiContext';
import { Picker, type PickerOption } from '../../components/Picker';
import { Avatar, Button, Kbd, LabelPill, PriorityIcon, StatusRing, TypeIcon, TypeTag } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { useHours } from '../../lib/format';
import { priorities, states, types } from '../../lib/issues';
import { useShortcuts } from '../../lib/shortcuts';
import { ActivityFeed } from './ActivityFeed';
import { RichMarkdown } from './RichMarkdown';

/** Same as Tailwind's 3xl breakpoint: from here the panel has room for two columns. */
const largeScreen = '(min-width: 120rem)';

function useMediaQuery(query: string) {
  return useSyncExternalStore(
    (onChange) => {
      const list = window.matchMedia(query);
      list.addEventListener('change', onChange);
      return () => list.removeEventListener('change', onChange);
    },
    () => window.matchMedia(query).matches,
  );
}

/**
 * Right-hand panel over list/board, full width on narrow screens (design guide). 560 px: chips, a planning strip and
 * tabs. Wide (large screens, or widened by hand): content on the left, every property in a rail on the right.
 */
export function IssuePanel({ workspace, issueKey, meName }: { workspace: string; issueKey: string; meName?: string }) {
  const { t } = useTranslation();
  const ui = useUi();
  const navigate = useNavigate();
  const { data: issue, isError } = useIssue(workspace, issueKey);
  const [editing, setEditing] = useState(false);
  const [widened, setWidened] = useState(false);
  const wide = useMediaQuery(largeScreen) || widened;
  const { setActiveKey } = ui;

  useEffect(() => setActiveKey(issueKey), [issueKey, setActiveKey]);
  useShortcuts({ e: () => setEditing(true) });

  const close = () => void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: undefined }) });

  return (
    <aside
      aria-label={t('issue.detail', { key: issueKey })}
      className={`flex shrink-0 flex-col border-l border-line bg-panel max-md:fixed max-md:inset-0 max-md:z-30 max-md:w-full ${wide ? 'w-[min(920px,62vw)]' : 'w-[min(560px,100%)]'}`}
    >
      <header className="flex h-[var(--topbar)] shrink-0 items-center gap-2 border-b border-line px-4">
        <span className="font-mono text-sm text-mute">{issueKey}</span>
        {issue && <TypeTag type={issue.type} />}
        {issue?.parent && (
          <span className="flex min-w-0 items-center gap-1 text-sm text-mute">
            <span aria-hidden="true">·</span>
            <ParentLink issueRef={issue.parent} />
          </span>
        )}
        <button
          type="button"
          onClick={() => setWidened(!widened)}
          className="ml-auto rounded-r1 p-1 text-mute hover:bg-sel hover:text-ink max-md:hidden"
          aria-label={t(wide ? 'issue.narrow' : 'issue.widen')}
          aria-pressed={wide}
          title={t(wide ? 'issue.narrow' : 'issue.widen')}
        >
          {wide ? <Minimize2 size={16} strokeWidth={1.5} /> : <Maximize2 size={16} strokeWidth={1.5} />}
        </button>
        <button type="button" onClick={close} className="rounded-r1 p-1 text-mute hover:bg-sel hover:text-ink max-md:ml-auto" aria-label={t('common.close')}>
          <X size={16} strokeWidth={1.5} />
        </button>
      </header>
      {isError && <p className="p-4 text-bad">{t('errors.issueNotFound', { key: issueKey })}</p>}
      {issue && (
        <IssueBody key={issue.key} workspace={workspace} issue={issue} wide={wide} editing={editing} setEditing={setEditing} meName={meName} />
      )}
    </aside>
  );
}

type Tab = 'description' | 'children' | 'relations' | 'activity';

function IssueBody({
  workspace,
  issue,
  wide,
  editing,
  setEditing,
}: {
  workspace: string;
  issue: IssueDetail;
  wide: boolean;
  editing: boolean;
  setEditing(v: boolean): void;
  meName?: string;
}) {
  const { t } = useTranslation();
  const ui = useUi();
  const hours = useHours();
  const update = useUpdateIssue(workspace);
  const save = (body: UpdateIssueRequest) => update.mutate({ path: { workspace, key: issue.key }, body });
  const [title, setTitle] = useState(issue.title);
  const [chosenTab, setTab] = useState<Tab>('description');
  // E edits the description, so it has to be the visible tab.
  const tab = editing ? 'description' : chosenTab;
  const { data: members } = useMembers(workspace);
  const [picker, setPicker] = useState<PalettePage | null>(null);
  const { data: labels } = useQuery({ ...listLabelsOptions({ path: { workspace } }), enabled: picker === 'label' });

  // S/A/P/L open the pickers here instead of the palette while this issue is the active one.
  const { activeKey, setPickerOverride } = ui;
  useEffect(() => {
    setPickerOverride((page) => {
      if (activeKey !== issue.key || page === 'root') return false;
      setPicker(page);
      return true;
    });
    return () => setPickerOverride(null);
  }, [activeKey, issue.key, setPickerOverride]);
  const controlled = (page: PalettePage) => ({ open: picker === page, onOpenChange: (o: boolean) => setPicker(o ? page : null) });

  // Assignees are people: they answer for the work (ADR-008).
  const assignees: PickerOption[] = [
    { value: NONE, label: t('issue.unassigned'), icon: <Avatar member={null} size={20} /> },
    ...(members ?? [])
      .filter((m) => m.status === 'active' && m.kind === 'human')
      .map((m) => ({ value: m.id, label: m.name, icon: <Avatar member={{ id: m.id, name: m.name, kind: m.kind }} size={20} /> })),
  ];
  const labelNames = [...new Set([...issue.labels, ...(labels ?? []).map((l) => l.name)])];
  const childrenDone = issue.children.filter((c) => c.state === 'done').length;
  const blocked = issue.relations.some((r) => r.type === 'blockedBy');

  const state = (
    <Picker
      {...controlled('state')}
      label={t('issue.state')}
      placeholder={t('palette.placeholder.state')}
      shortcut="S"
      options={states.map((x, i) => ({ value: x, label: t(`state.${x}`), icon: <StatusRing state={x} />, key: String(i + 1) }))}
      selected={[issue.state]}
      onPick={(v) => save({ state: v as IssueDetail['state'] })}
    >
      <StatusRing state={issue.state} /> {t(`state.${issue.state}`)}
    </Picker>
  );
  const priority = (
    <Picker
      {...controlled('priority')}
      label={t('issue.priority')}
      placeholder={t('palette.placeholder.priority')}
      shortcut="P"
      options={priorities.map((x, i) => ({ value: x, label: t(`priority.${x}`), icon: <PriorityIcon priority={x} />, key: String((i + 1) % 5) }))}
      selected={[issue.priority]}
      onPick={(v) => save({ priority: v as IssueDetail['priority'] })}
    >
      <PriorityIcon priority={issue.priority} /> {t(`priority.${issue.priority}`)}
    </Picker>
  );
  const assignee = (
    <Picker
      {...controlled('assign')}
      label={t('issue.assignee')}
      placeholder={t('palette.placeholder.assign')}
      shortcut="A"
      options={assignees}
      selected={[issue.assignee?.id ?? NONE]}
      onPick={(v) => save(v === NONE ? { clear: ['assignee'] } : { assignee: v })}
    >
      <Avatar member={issue.assignee} size={18} /> {issue.assignee?.name ?? t('issue.unassigned')}
    </Picker>
  );
  const labelPicker = (
    <Picker
      {...controlled('label')}
      multi
      label={t('issue.labels')}
      placeholder={t('palette.placeholder.label')}
      shortcut="L"
      options={labelNames.map((name) => ({ value: name, label: name, icon: <LabelDot name={name} /> }))}
      selected={issue.labels}
      onPick={(v) => save(issue.labels.includes(v) ? { removeLabels: [v] } : { addLabels: [v] })}
      onCreate={(name) => save({ addLabels: [name] })}
    >
      {issue.labels.length === 0 ? (
        <>
          <Tag size={14} strokeWidth={1.5} aria-hidden="true" className="text-mute" />
          <span className="text-mute">{t('issue.noLabels')}</span>
        </>
      ) : (
        issue.labels.map((l) => <LabelPill key={l} name={l} />)
      )}
    </Picker>
  );
  const type = (
    <Picker
      label={t('issue.type')}
      placeholder={t('picker.type')}
      options={types.map((x) => ({ value: x, label: t(`type.${x}`), icon: <TypeIcon type={x} /> }))}
      selected={[issue.type]}
      onPick={(v) => save({ type: v as IssueType })}
    >
      <TypeIcon type={issue.type} /> {t(`type.${issue.type}`)}
    </Picker>
  );
  const sprint = <SprintSelect workspace={workspace} issue={issue} onChange={(id) => save(id ? { sprint: id } : { clear: ['sprint'] })} />;
  const dates = (
    <span className="flex items-center gap-1.5">
      <DateInput value={issue.startDate} label={t('issue.startDate')} onCommit={(v) => save(v ? { startDate: v } : { clear: ['startDate'] })} />
      <span className="text-mute">→</span>
      <DateInput value={issue.dueDate} label={t('issue.dueDate')} onCommit={(v) => save(v ? { dueDate: v } : { clear: ['dueDate'] })} />
    </span>
  );
  const effort = (
    <span className="flex items-center gap-2">
      <NumberInput value={issue.effort} label={t('issue.effort')} onCommit={(v) => save(v === null ? { clear: ['effort'] } : { effort: v })} />
      {issue.childEffort != null && <span className="text-sm text-mute">{t('issue.childEffort', { hours: hours(issue.childEffort) })}</span>}
    </span>
  );

  const titleRow = (
    <div className="flex items-start gap-2">
      <button type="button" onClick={() => setPicker('state')} className="mt-1.5 rounded-full" aria-label={t('issue.changeState')}>
        <StatusRing state={issue.state} />
      </button>
      <textarea
        value={title}
        rows={1}
        aria-label={t('issue.title')}
        onChange={(e) => setTitle(e.target.value)}
        onBlur={() => title.trim() && title !== issue.title && save({ title: title.trim() })}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault();
            e.currentTarget.blur();
          }
        }}
        className="field-sizing-content min-w-0 flex-1 resize-none rounded-r1 bg-transparent px-1 font-display text-xl font-bold focus:bg-sunk"
      />
    </div>
  );
  const description = <Description workspace={workspace} issue={issue} editing={editing} setEditing={setEditing} save={save} />;
  const children =
    issue.children.length > 0 ? (
      <section className="flex flex-col gap-1" aria-label={t('issue.subtasks')}>
        <ChildProgress done={childrenDone} total={issue.children.length} />
        {issue.children.map((child) => (
          <IssueLink key={child.key} issueRef={child} />
        ))}
      </section>
    ) : (
      <p className="text-mute">{t('issue.noSubtasks')}</p>
    );
  const activity = (
    <>
      <ActivityFeed workspace={workspace} issueKey={issue.key} />
      <p className="text-sm text-mute">{t('issue.createdBy', { name: issue.createdBy.name })}</p>
    </>
  );

  if (wide) {
    return (
      <div className="flex min-h-0 flex-1">
        <div className="flex min-w-0 flex-1 flex-col gap-6 overflow-auto px-6 py-5">
          {titleRow}
          {description}
          {issue.children.length > 0 && (
            <div className="flex flex-col gap-1">
              <h3 className="text-sm font-semibold text-mute">{t('issue.subtasks')}</h3>
              {children}
            </div>
          )}
          <Relations workspace={workspace} issue={issue} />
          {activity}
        </div>
        <dl className="flex w-72 shrink-0 flex-col gap-3 overflow-auto border-l border-line bg-sunk/40 px-4 py-5">
          <RailField label={t('issue.state')}>{state}</RailField>
          <RailField label={t('issue.priority')}>{priority}</RailField>
          <RailField label={t('issue.assignee')}>{assignee}</RailField>
          <RailField label={t('issue.labels')}>{labelPicker}</RailField>
          <RailField label={t('issue.type')}>{type}</RailField>
          <hr className="border-line" />
          <RailField label={t('issue.sprint')}>{sprint}</RailField>
          <RailField label={t('issue.dates')}>{dates}</RailField>
          <RailField label={t('issue.effort')}>{effort}</RailField>
        </dl>
      </div>
    );
  }

  const tabs: { id: Tab; label: string; count?: ReactNode }[] = [
    { id: 'description', label: t('issue.description') },
    { id: 'children', label: t('issue.subtasks'), count: issue.children.length ? `${childrenDone}/${issue.children.length}` : undefined },
    {
      id: 'relations',
      label: t('issue.relations'),
      count: issue.relations.length ? <span className={blocked ? 'font-semibold text-bad' : ''}>{issue.relations.length}</span> : undefined,
    },
    { id: 'activity', label: t('feed.title') },
  ];

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 flex-col gap-3 px-4 pt-4">
        {titleRow}
        <div className="flex flex-wrap gap-1.5">
          {state}
          {priority}
          {assignee}
          {labelPicker}
          {type}
        </div>
        <div role="group" aria-label={t('issue.planning')} className="flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded-r2 bg-sunk/60 p-1.5">
          {sprint}
          {dates}
          {effort}
        </div>
        <div role="tablist" aria-label={t('issue.sections')} className="flex gap-4 border-b border-line">
          {tabs.map((x) => (
            <button
              key={x.id}
              type="button"
              role="tab"
              id={`issue-tab-${x.id}`}
              aria-selected={tab === x.id}
              aria-controls="issue-tabpanel"
              onClick={() => setTab(x.id)}
              className={`-mb-px flex h-9 items-center gap-1.5 border-b-2 ${tab === x.id ? 'border-acc-ink font-semibold' : 'border-transparent text-mute hover:text-ink'}`}
            >
              {x.label}
              {x.count != null && <span className="text-xs font-normal text-mute">{x.count}</span>}
            </button>
          ))}
        </div>
      </div>
      <div role="tabpanel" id="issue-tabpanel" aria-labelledby={`issue-tab-${tab}`} className="flex min-h-0 flex-1 flex-col gap-4 overflow-auto p-4">
        {tab === 'description' && description}
        {tab === 'children' && children}
        {tab === 'relations' && <Relations workspace={workspace} issue={issue} />}
        {tab === 'activity' && activity}
      </div>
    </div>
  );
}

function Description({
  workspace,
  issue,
  editing,
  setEditing,
  save,
}: {
  workspace: string;
  issue: IssueDetail;
  editing: boolean;
  setEditing(v: boolean): void;
  save(body: UpdateIssueRequest): void;
}) {
  const { t } = useTranslation();
  const [description, setDescription] = useState(issue.description ?? '');
  const upload = useMarkdownUpload(workspace, issue.key, setDescription);
  return (
    <section className="flex flex-col gap-2" aria-label={t('issue.description')}>
      {editing ? (
        <textarea
          autoFocus
          value={description}
          aria-label={t('issue.description')}
          onChange={(e) => setDescription(e.target.value)}
          onBlur={() => {
            setEditing(false);
            if (description !== (issue.description ?? '')) save(description ? { description } : { clear: ['description'] });
          }}
          onKeyDown={(e) => {
            if (e.key === 'Escape' || (e.key === 'Enter' && (e.ctrlKey || e.metaKey))) {
              e.preventDefault();
              e.stopPropagation();
              e.currentTarget.blur();
            }
          }}
          placeholder={t('issue.descriptionPlaceholder')}
          {...upload}
          className="min-h-60 rounded-r1 border border-line bg-panel p-2 font-mono text-md"
        />
      ) : issue.description ? (
        <div className="group relative" onDoubleClick={() => setEditing(true)}>
          <Button
            variant="ghost"
            className="absolute top-0 right-0 h-6 px-2 text-sm opacity-0 group-hover:opacity-100 focus:opacity-100"
            onClick={() => setEditing(true)}
          >
            {t('common.edit')} <Kbd>E</Kbd>
          </Button>
          <RichMarkdown workspace={workspace}>{issue.description}</RichMarkdown>
        </div>
      ) : (
        <button type="button" onClick={() => setEditing(true)} className="rounded-r1 border border-dashed border-line p-3 text-left text-mute">
          {t('issue.descriptionPlaceholder')}
        </button>
      )}
    </section>
  );
}

function ChildProgress({ done, total }: { done: number; total: number }) {
  const { t } = useTranslation();
  return (
    <div className="mb-1 flex items-center gap-2 text-sm text-mute">
      {t('issue.childrenDone', { done, total })}
      <span className="h-1 w-24 overflow-hidden rounded-full bg-line" aria-hidden="true">
        <span className="block h-1 bg-ok" style={{ width: `${(done / total) * 100}%` }} />
      </span>
    </div>
  );
}

function RailField({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <dt className="text-xs text-mute">{label}</dt>
      <dd className="min-w-0">{children}</dd>
    </div>
  );
}

function ParentLink({ issueRef }: { issueRef: { key: string; title: string } }) {
  const navigate = useNavigate();
  return (
    <button
      type="button"
      onClick={() => void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: issueRef.key }) })}
      className="min-w-0 truncate rounded-r1 px-1 hover:bg-sel hover:text-ink"
    >
      <span className="font-mono">{issueRef.key}</span> {issueRef.title}
    </button>
  );
}

function SprintSelect({ workspace, issue, onChange }: { workspace: string; issue: IssueDetail; onChange(id: string | null): void }) {
  const { t } = useTranslation();
  const { data: sprints } = useQuery(listSprintsOptions({ path: { workspace }, query: { project: issue.projectKey, state: ['planned', 'active'] } }));
  const current = sprints?.find((s) => s.id === issue.sprintId);
  return (
    <Picker
      label={t('issue.sprint')}
      placeholder={t('picker.sprint')}
      options={[
        { value: NONE, label: t('issue.noSprint'), icon: <SprintIcon /> },
        ...(sprints ?? []).map((s) => ({
          value: s.id,
          label: s.name,
          icon: <SprintIcon active={s.state === 'active'} />,
          hint: s.state === 'active' ? t('sprint.active') : undefined,
        })),
      ]}
      selected={[issue.sprintId ?? NONE]}
      onPick={(v) => onChange(v === NONE ? null : v)}
    >
      <SprintIcon active={current?.state === 'active'} />
      {current ? current.name : <span className="text-mute">{t('issue.noSprint')}</span>}
    </Picker>
  );
}

/** Option value for "none" (unassigned, no sprint): cmdk needs a non-empty value. */
const NONE = 'none';

function LabelDot({ name }: { name: string }) {
  return <span className={`size-2 rounded-full ${name === 'bug' ? 'bg-bad' : 'bg-mute'}`} />;
}

function SprintIcon({ active = false }: { active?: boolean }) {
  return <IterationCw size={14} strokeWidth={1.5} aria-hidden="true" className={active ? 'text-acc-ink' : 'text-mute'} />;
}

const relationIcon: Record<RelationKind, ReactNode> = {
  blocks: <OctagonAlert size={14} strokeWidth={1.5} aria-hidden="true" className="text-bad" />,
  blockedBy: <Ban size={14} strokeWidth={1.5} aria-hidden="true" className="text-warn" />,
  relates: <Link2 size={14} strokeWidth={1.5} aria-hidden="true" className="text-mute" />,
  duplicates: <Copy size={14} strokeWidth={1.5} aria-hidden="true" className="text-mute" />,
  duplicatedBy: <Copy size={14} strokeWidth={1.5} aria-hidden="true" className="text-mute" />,
};

function NumberInput({ value, label, onCommit }: { value: number | null; label: string; onCommit(v: number | null): void }) {
  const [text, setText] = useState(value?.toString() ?? '');
  return (
    <input
      inputMode="decimal"
      aria-label={label}
      value={text}
      onChange={(e) => setText(e.target.value)}
      onBlur={() => {
        const parsed = text.trim() === '' ? null : Number(text.replace(',', '.'));
        if (parsed !== value && (parsed === null || !Number.isNaN(parsed))) onCommit(parsed);
      }}
      className="h-7 w-20 rounded-r1 bg-sunk px-2 font-mono tabular-nums hover:bg-sel"
    />
  );
}

function DateInput({ value, label, onCommit }: { value: string | null; label: string; onCommit(v: string | null): void }) {
  return (
    <input
      type="date"
      aria-label={label}
      defaultValue={value ?? ''}
      onBlur={(e) => e.target.value !== (value ?? '') && onCommit(e.target.value || null)}
      className="h-7 rounded-r1 bg-sunk px-2 font-mono text-sm hover:bg-sel"
    />
  );
}

function IssueLink({ issueRef }: { issueRef: { key: string; title: string; state: IssueDetail['state']; type: IssueType } }) {
  const navigate = useNavigate();
  return (
    <button
      type="button"
      onClick={() => void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: issueRef.key }) })}
      className="flex h-7 min-w-0 items-center gap-2 rounded-r1 px-1.5 text-left hover:bg-sel"
    >
      <StatusRing state={issueRef.state} />
      <span className="font-mono text-sm text-mute">{issueRef.key}</span>
      <TypeTag type={issueRef.type} />
      <span className="truncate">{issueRef.title}</span>
    </button>
  );
}

const relationKinds: RelationKind[] = ['blocks', 'blockedBy', 'relates', 'duplicates', 'duplicatedBy'];

function Relations({ workspace, issue }: { workspace: string; issue: IssueDetail }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();
  const [kind, setKind] = useState<RelationKind>('blocks');
  const [target, setTarget] = useState('');
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: getIssueQueryKey({ path: { workspace, key: issue.key } }) });
    void qc.invalidateQueries({ queryKey: listIssueActivityQueryKey({ path: { workspace, key: issue.key } }) });
  };
  const add = useMutation({
    ...addIssueRelationMutation(),
    onSuccess: () => {
      setTarget('');
      refresh();
    },
    onError: () => toast.error(t('errors.relationFailed')),
  });
  const remove = useMutation({ ...removeIssueRelationMutation(), onSettled: refresh });

  return (
    <section className="flex flex-col gap-1">
      <h3 className="text-sm font-semibold text-mute">{t('issue.relations')}</h3>
      {issue.relations.map((r) => (
        <div key={r.id} className="group flex items-center gap-2">
          <span className="flex w-28 shrink-0 items-center gap-1.5 text-sm text-mute">
            {relationIcon[r.type]}
            {t(`relation.${r.type}`)}
          </span>
          <IssueLink issueRef={r.issue} />
          <button
            type="button"
            onClick={() => remove.mutate({ path: { workspace, key: issue.key, relationId: r.id } })}
            className="ml-auto rounded-r1 p-1 text-mute opacity-0 group-hover:opacity-100 hover:text-bad focus:opacity-100"
            aria-label={t('issue.removeRelation', { key: r.issue.key })}
          >
            <X size={14} strokeWidth={1.5} />
          </button>
        </div>
      ))}
      <form
        className="flex items-center gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (target.trim()) add.mutate({ path: { workspace, key: issue.key }, body: { type: kind, target: target.trim() } });
        }}
      >
        <Picker
          label={t('issue.relationType')}
          placeholder={t('picker.relation')}
          options={relationKinds.map((k) => ({ value: k, label: t(`relation.${k}`), icon: relationIcon[k] }))}
          selected={[kind]}
          onPick={(v) => setKind(v as RelationKind)}
        >
          {relationIcon[kind]}
          {t(`relation.${kind}`)}
          <ChevronDown size={14} strokeWidth={1.5} aria-hidden="true" className="text-mute" />
        </Picker>
        <input
          value={target}
          onChange={(e) => setTarget(e.target.value)}
          placeholder={t('issue.relationTarget')}
          aria-label={t('issue.relationTarget')}
          className="h-7 w-32 rounded-r1 bg-sunk px-2 font-mono text-sm uppercase hover:bg-sel"
        />
        <Button type="submit" className="h-7 px-2 text-sm" disabled={!target.trim()}>
          {t('issue.addRelation')}
        </Button>
      </form>
    </section>
  );
}
