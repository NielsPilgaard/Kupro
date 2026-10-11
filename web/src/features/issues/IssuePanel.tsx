import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { X } from 'lucide-react';
import { useEffect, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { addIssueRelationMutation, getIssueQueryKey, listIssueActivityQueryKey, listSprintsOptions, removeIssueRelationMutation } from '../../api/gen/@tanstack/react-query.gen';
import type { IssueDetail, IssueType, RelationKind, UpdateIssueRequest } from '../../api/gen/types.gen';
import { useMarkdownUpload } from '../../api/attachments';
import { useIssue, useUpdateIssue } from '../../api/issues';
import { useUi, type PalettePage } from '../../app/UiContext';
import { Avatar, Button, Kbd, LabelPill, PriorityIcon, StatusRing, TypeTag } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { useHours } from '../../lib/format';
import { types } from '../../lib/issues';
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

  const pick = (page: PalettePage) => () => {
    ui.setActiveKey(issue.key);
    ui.openPalette(page);
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-5 overflow-auto p-4">
      <div className="flex items-start gap-2">
        <button type="button" onClick={pick('state')} className="mt-1.5 rounded-full" aria-label={t('issue.changeState')}>
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
          <PickerButton onClick={pick('state')} shortcut="S">
            <StatusRing state={issue.state} /> {t(`state.${issue.state}`)}
          </PickerButton>
        </Field>
        <Field label={t('issue.priority')}>
          <PickerButton onClick={pick('priority')} shortcut="P">
            <PriorityIcon priority={issue.priority} /> {t(`priority.${issue.priority}`)}
          </PickerButton>
        </Field>
        <Field label={t('issue.assignee')}>
          <PickerButton onClick={pick('assign')} shortcut="A">
            <Avatar member={issue.assignee} size={18} /> {issue.assignee?.name ?? t('issue.unassigned')}
          </PickerButton>
        </Field>
        <Field label={t('issue.labels')}>
          <PickerButton onClick={pick('label')} shortcut="L">
            {issue.labels.length === 0 ? <span className="text-mute">{t('issue.noLabels')}</span> : issue.labels.map((l) => <LabelPill key={l} name={l} />)}
          </PickerButton>
        </Field>
        <Field label={t('issue.type')}>
          <select
            value={issue.type}
            aria-label={t('issue.type')}
            onChange={(e) => save({ type: e.target.value as IssueType })}
            className="h-7 rounded-r1 border border-line bg-panel px-1"
          >
            {types.map((type) => (
              <option key={type} value={type}>
                {t(`type.${type}`)}
              </option>
            ))}
          </select>
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
  return (
    <select
      value={issue.sprintId ?? ''}
      aria-label={t('issue.sprint')}
      onChange={(e) => onChange(e.target.value || null)}
      className="h-7 max-w-full rounded-r1 border border-line bg-panel px-1"
    >
      <option value="">{t('issue.noSprint')}</option>
      {sprints?.map((s) => (
        <option key={s.id} value={s.id}>
          {s.name}
          {s.state === 'active' ? ` (${t('sprint.active')})` : ''}
        </option>
      ))}
    </select>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-sm text-mute">{label}</dt>
      <dd className="min-w-0">{children}</dd>
    </>
  );
}

function PickerButton({ onClick, shortcut, children }: { onClick(): void; shortcut: string; children: ReactNode }) {
  return (
    <button type="button" onClick={onClick} className="group flex h-7 max-w-full items-center gap-1.5 rounded-r1 px-1.5 hover:bg-sel">
      {children}
      <span className="opacity-0 group-hover:opacity-100">
        <Kbd>{shortcut}</Kbd>
      </span>
    </button>
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
      className="h-7 w-20 rounded-r1 border border-line bg-panel px-2 font-mono tabular-nums"
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
      className="h-7 rounded-r1 border border-line bg-panel px-1 font-mono text-sm"
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
          <span className="w-28 shrink-0 text-sm text-mute">{t(`relation.${r.type}`)}</span>
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
        <select value={kind} onChange={(e) => setKind(e.target.value as RelationKind)} aria-label={t('issue.relationType')} className="h-7 rounded-r1 border border-line bg-panel px-1 text-sm">
          {relationKinds.map((k) => (
            <option key={k} value={k}>
              {t(`relation.${k}`)}
            </option>
          ))}
        </select>
        <input
          value={target}
          onChange={(e) => setTarget(e.target.value)}
          placeholder={t('issue.relationTarget')}
          aria-label={t('issue.relationTarget')}
          className="h-7 w-32 rounded-r1 border border-line bg-panel px-2 font-mono text-sm uppercase"
        />
        <Button type="submit" className="h-7 px-2 text-sm" disabled={!target.trim()}>
          {t('issue.addRelation')}
        </Button>
      </form>
    </section>
  );
}
