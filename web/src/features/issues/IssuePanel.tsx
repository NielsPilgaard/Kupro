import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { Ban, ChevronDown, Copy, IterationCw, Link2, OctagonAlert, Tag, X } from 'lucide-react';
import { useEffect, useState, type ReactNode } from 'react';
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

/** Right-hand panel over list/board (min 480 px), full width on narrow screens (design guide). */
export function IssuePanel({ workspace, issueKey, meName }: { workspace: string; issueKey: string; meName?: string }) {
  const { t } = useTranslation();
  const ui = useUi();
  const navigate = useNavigate();
  const { data: issue, isError } = useIssue(workspace, issueKey);
  const [editing, setEditing] = useState(false);
  const { setActiveKey } = ui;

  useEffect(() => setActiveKey(issueKey), [issueKey, setActiveKey]);
  useShortcuts({ e: () => setEditing(true) });

  const close = () => void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: undefined }) });

  return (
    <aside
      aria-label={t('issue.detail', { key: issueKey })}
      className="flex w-[min(560px,100%)] shrink-0 flex-col border-l border-line bg-panel max-md:fixed max-md:inset-0 max-md:z-30 max-md:w-full"
    >
      <header className="flex h-[var(--topbar)] shrink-0 items-center gap-2 border-b border-line px-4">
        <span className="font-mono text-sm text-mute">{issueKey}</span>
        {issue && <TypeTag type={issue.type} />}
        <button type="button" onClick={close} className="ml-auto rounded-r1 p-1 text-mute hover:bg-sel hover:text-ink" aria-label={t('common.close')}>
          <X size={16} strokeWidth={1.5} />
        </button>
      </header>
      {isError && <p className="p-4 text-bad">{t('errors.issueNotFound', { key: issueKey })}</p>}
      {issue && (
        <IssueBody key={issue.key} workspace={workspace} issue={issue} editing={editing} setEditing={setEditing} meName={meName} />
      )}
    </aside>
  );
}

function IssueBody({
  workspace,
  issue,
  editing,
  setEditing,
}: {
  workspace: string;
  issue: IssueDetail;
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
  const [description, setDescription] = useState(issue.description ?? '');
  const upload = useMarkdownUpload(workspace, issue.key, setDescription);
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

  // A person answers for the work, so only humans are offered; an existing agent assignee stays visible.
  const assignees: PickerOption[] = [
    { value: NONE, label: t('issue.unassigned'), icon: <Avatar member={null} size={20} /> },
    ...(members ?? [])
      .filter((m) => m.status === 'active' && (m.kind === 'human' || m.id === issue.assignee?.id))
      .map((m) => ({ value: m.id, label: m.name, icon: <Avatar member={{ id: m.id, name: m.name, kind: m.kind }} size={20} /> })),
  ];
  const labelNames = [...new Set([...issue.labels, ...(labels ?? []).map((l) => l.name)])];

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-5 overflow-auto p-4">
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

      <dl className="grid grid-cols-[110px_minmax(0,1fr)] items-center gap-x-3 gap-y-1.5">
        <Field label={t('issue.state')}>
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
        </Field>
        <Field label={t('issue.priority')}>
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
        </Field>
        <Field label={t('issue.assignee')}>
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
        </Field>
        <Field label={t('issue.labels')}>
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
        </Field>
        <Field label={t('issue.type')}>
          <Picker
            label={t('issue.type')}
            placeholder={t('picker.type')}
            options={types.map((x) => ({ value: x, label: t(`type.${x}`), icon: <TypeIcon type={x} /> }))}
            selected={[issue.type]}
            onPick={(v) => save({ type: v as IssueType })}
          >
            <TypeIcon type={issue.type} /> {t(`type.${issue.type}`)}
          </Picker>
        </Field>
        <Field label={t('issue.effort')}>
          <span className="flex items-center gap-2">
            <NumberInput
              value={issue.effort}
              label={t('issue.effort')}
              onCommit={(v) => save(v === null ? { clear: ['effort'] } : { effort: v })}
            />
            {issue.childEffort != null && (
              <span className="text-sm text-mute">{t('issue.childEffort', { hours: hours(issue.childEffort) })}</span>
            )}
          </span>
        </Field>
        <Field label={t('issue.dates')}>
          <span className="flex items-center gap-2">
            <DateInput value={issue.startDate} label={t('issue.startDate')} onCommit={(v) => save(v ? { startDate: v } : { clear: ['startDate'] })} />
            <span className="text-mute">→</span>
            <DateInput value={issue.dueDate} label={t('issue.dueDate')} onCommit={(v) => save(v ? { dueDate: v } : { clear: ['dueDate'] })} />
          </span>
        </Field>
        <Field label={t('issue.sprint')}>
          <SprintSelect workspace={workspace} issue={issue} onChange={(id) => save(id ? { sprint: id } : { clear: ['sprint'] })} />
        </Field>
        {issue.parent && (
          <Field label={t('issue.parent')}>
            <IssueLink issueRef={issue.parent} />
          </Field>
        )}
      </dl>

      <section className="flex flex-col gap-2">
        <div className="flex items-center gap-2">
          <h3 className="text-sm font-semibold text-mute">{t('issue.description')}</h3>
          {!editing && (
            <Button variant="ghost" className="ml-auto h-6 px-2 text-sm" onClick={() => setEditing(true)}>
              {t('common.edit')} <Kbd>E</Kbd>
            </Button>
          )}
        </div>
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
            className="min-h-40 rounded-r1 border border-line bg-panel p-2 font-mono text-md"
          />
        ) : issue.description ? (
          <div onDoubleClick={() => setEditing(true)}>
            <RichMarkdown workspace={workspace}>{issue.description}</RichMarkdown>
          </div>
        ) : (
          <button type="button" onClick={() => setEditing(true)} className="rounded-r1 border border-dashed border-line p-3 text-left text-mute">
            {t('issue.descriptionPlaceholder')}
          </button>
        )}
      </section>

      {issue.children.length > 0 && (
        <section className="flex flex-col gap-1">
          <h3 className="text-sm font-semibold text-mute">{t('issue.children', { count: issue.children.length })}</h3>
          {issue.children.map((child) => (
            <IssueLink key={child.key} issueRef={child} />
          ))}
        </section>
      )}

      <Relations workspace={workspace} issue={issue} />

      <p className="text-sm text-mute">{t('issue.createdBy', { name: issue.createdBy.name })}</p>

      <ActivityFeed workspace={workspace} issueKey={issue.key} />
    </div>
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

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-sm text-mute">{label}</dt>
      <dd className="min-w-0">{children}</dd>
    </>
  );
}

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
