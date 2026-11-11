import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useMarkdownUpload } from '../../api/attachments';
import {
  addCommentMutation,
  deleteCommentMutation,
  getMeOptions,
  listIssueActivityOptions,
  listIssueActivityQueryKey,
  updateCommentMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import { listIssueActivity } from '../../api/gen/sdk.gen';
import type { FeedChange, FeedItem, FeedMeeting, IssuePriority, IssueState, IssueType, RelationKind, Surface, CloseOutcome } from '../../api/gen/types.gen';
import { useMembers } from '../../api/issues';
import { Avatar, Button, Kbd } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { useRelativeTime } from '../../lib/format';
import { RichMarkdown } from './RichMarkdown';

const fieldLabels = {
  title: 'issue.title',
  state: 'issue.state',
  priority: 'issue.priority',
  assignee: 'issue.assignee',
  labels: 'issue.labels',
  type: 'issue.type',
  effort: 'issue.effort',
  startDate: 'issue.startDate',
  dueDate: 'issue.dueDate',
  parent: 'issue.parent',
  sprint: 'issue.sprint',
} as const;

/** Items per request; a full page means there may be older ones (an import can leave thousands). */
const pageSize = 500;

/** History and comments, oldest first, with a composer at the bottom (task 008). */
export function ActivityFeed({ workspace, issueKey }: { workspace: string; issueKey: string }) {
  // Older pages belong to one issue: a new key starts over.
  return <Feed key={issueKey} workspace={workspace} issueKey={issueKey} />;
}

function Feed({ workspace, issueKey }: { workspace: string; issueKey: string }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();
  const { data: latest } = useQuery(listIssueActivityOptions({ path: { workspace, key: issueKey }, query: { limit: pageSize } }));
  const [older, setOlder] = useState<{ items: FeedItem[]; more: boolean }>({ items: [], more: true });
  const items = latest && [...older.items, ...latest];
  const canShowOlder = (latest?.length ?? 0) >= pageSize && older.more;
  const showOlder = useMutation({
    mutationFn: async () => {
      const { data } = await listIssueActivity({ path: { workspace, key: issueKey }, query: { before: items?.[0]?.id, limit: pageSize }, throwOnError: true });
      return data;
    },
    onSuccess: (page) => setOlder((o) => ({ items: [...page, ...o.items], more: page.length >= pageSize })),
    onError: () => toast.error(t('feed.failed')),
  });
  const { data: me } = useQuery(getMeOptions());
  const { data: members } = useMembers(workspace);
  const myId = members?.find((m) => m.kind === 'human' && m.email === me?.email)?.id;
  const refresh = () => void qc.invalidateQueries({ queryKey: listIssueActivityQueryKey({ path: { workspace, key: issueKey } }) });
  const onError = () => toast.error(t('feed.failed'));
  const add = useMutation({ ...addCommentMutation(), onSuccess: refresh, onError });

  return (
    <section className="flex flex-col gap-3" aria-label={t('feed.title')}>
      <h3 className="text-sm font-semibold text-mute">{t('feed.title')}</h3>
      {canShowOlder && (
        <Button variant="ghost" className="self-start" disabled={showOlder.isPending} onClick={() => showOlder.mutate()}>
          {t('feed.showOlder')}
        </Button>
      )}
      <ol className="flex flex-col gap-3">
        {items?.map((item) =>
          item.kind === 'comment' ? (
            <CommentItem key={item.id} workspace={workspace} issueKey={issueKey} item={item} mine={item.actor.id === myId} onChanged={refresh} />
          ) : (
            <EventItem key={item.id} item={item} />
          ),
        )}
      </ol>
      <CommentEditor
        workspace={workspace}
        issueKey={issueKey}
        initial=""
        submitLabel={t('feed.comment')}
        busy={add.isPending}
        onSubmit={(body, reset) => add.mutate({ path: { workspace, key: issueKey }, body: { body } }, { onSuccess: reset })}
      />
    </section>
  );
}

/** An import's source by name: "Jira", "Azure DevOps". A kind Kupro doesn't know is shown as given. */
function useSourceName() {
  const { t } = useTranslation();
  return (kind?: string | null) => (kind ? t(`feed.source.${kind}`, { defaultValue: kind }) : t('feed.sourceUnknown'));
}

function EventItem({ item }: { item: FeedItem }) {
  const { t } = useTranslation();
  const ago = useRelativeTime();
  const sourceName = useSourceName();
  // An import keeps the source's name for someone the owner skipped; the shared placeholder only holds the entry.
  const name = item.sourceActor ?? item.actor.name;

  const value = (field: string, v: string | null) => {
    if (v === null) return t('feed.none');
    if (field === 'state') return t(`state.${v as IssueState}`);
    if (field === 'priority') return t(`priority.${v as IssuePriority}`);
    if (field === 'type') return t(`type.${v as IssueType}`);
    return v;
  };
  const line = (c: FeedChange) => {
    switch (item.kind) {
      case 'relationAdded':
        return t('feed.relationAdded', { name, relation: t(`relation.${c.field as RelationKind}`).toLowerCase(), key: c.to });
      case 'relationRemoved':
        return t('feed.relationRemoved', { name, relation: t(`relation.${c.field as RelationKind}`).toLowerCase(), key: c.from });
      case 'sourceChanged':
        // The source's own field name and values, as it showed them.
        return t('feed.sourceChanged', { name, field: c.field, source: sourceName(item.source), from: c.from ?? t('feed.none'), to: c.to ?? t('feed.none') });
      default: {
        if (c.field === 'description') return t('feed.description', { name });
        const label = c.field in fieldLabels ? t(fieldLabels[c.field as keyof typeof fieldLabels]) : c.field;
        return t('feed.changed', { name, field: label.toLowerCase(), from: value(c.field, c.from), to: value(c.field, c.to) });
      }
    }
  };
  const of = (field: string) => item.changes.find((c) => c.field === field);
  const lines =
    item.kind === 'created'
      ? [t('feed.created', { name })]
      : item.kind === 'imported'
        ? [t('feed.imported', { name, source: sourceName(item.source) })]
        : item.kind === 'sprintClosed'
        ? [t(`feed.sprintClosed.${of('outcome')?.to as CloseOutcome}`, { name, sprint: of('sprint')?.from, target: of('sprint')?.to ?? t('sprint.backlog') })]
        : item.kind === 'splitFrom'
          ? [t('feed.splitFrom', { name, key: of('issue')?.to })]
          : item.kind === 'splitInto'
            ? [t('feed.splitInto', { name, key: of('issue')?.to })]
            : item.changes.map(line);

  return (
    <li className="flex gap-2 text-sm text-mute">
      <Avatar member={item.actor} size={16} />
      <span className="min-w-0 flex-1">
        {lines.map((l, i) => (
          <span key={i} className="block">
            {l}
          </span>
        ))}
        {item.meeting && <MeetingNote meeting={item.meeting} />}
      </span>
      <Meta at={item.at} via={item.via} source={item.source} ago={ago} />
    </li>
  );
}

function CommentItem({
  workspace,
  issueKey,
  item,
  mine,
  onChanged,
}: {
  workspace: string;
  issueKey: string;
  item: FeedItem;
  mine: boolean;
  onChanged(): void;
}) {
  const { t } = useTranslation();
  const toast = useToast();
  const ago = useRelativeTime();
  const [editing, setEditing] = useState(false);
  const onError = () => toast.error(t('feed.failed'));
  const update = useMutation({ ...updateCommentMutation(), onSuccess: onChanged, onError });
  const remove = useMutation({ ...deleteCommentMutation(), onSuccess: onChanged, onError });

  return (
    <li className="flex flex-col gap-1.5 rounded-r2 border border-line bg-panel p-3">
      <div className="flex items-center gap-2 text-sm">
        <Avatar member={item.actor} size={18} />
        <span className="font-semibold">{item.sourceActor ?? item.actor.name}</span>
        {item.editedAt && <span className="text-mute">{t('feed.edited')}</span>}
        <Meta at={item.at} via={item.via} source={item.source} ago={ago} />
        {mine && !editing && (
          <span className="flex gap-1">
            <Button variant="ghost" className="h-6 px-2 text-sm" onClick={() => setEditing(true)}>
              {t('common.edit')}
            </Button>
            <Button
              variant="ghost"
              className="h-6 px-2 text-sm"
              onClick={() => window.confirm(t('feed.confirmDelete')) && remove.mutate({ path: { workspace, id: item.id } })}
            >
              {t('feed.delete')}
            </Button>
          </span>
        )}
      </div>
      {editing ? (
        <CommentEditor
          workspace={workspace}
          issueKey={issueKey}
          initial={item.body ?? ''}
          submitLabel={t('common.save')}
          busy={update.isPending}
          autoFocus
          onCancel={() => setEditing(false)}
          onSubmit={(body) => update.mutate({ path: { workspace, id: item.id }, body: { body } }, { onSuccess: () => setEditing(false) })}
        />
      ) : (
        <RichMarkdown workspace={workspace}>{item.body ?? ''}</RichMarkdown>
      )}
      {item.meeting && (
        <span className="text-sm text-mute">
          <MeetingNote meeting={item.meeting} />
        </span>
      )}
    </li>
  );
}

/** Where a meeting-agent change came from: the words that triggered it, who approved it, and a link to the meeting. */
function MeetingNote({ meeting }: { meeting: FeedMeeting }) {
  const { t } = useTranslation();
  return (
    <Link
      to="."
      search={(prev: Record<string, unknown>) => ({ ...prev, meeting: meeting.meetingId })}
      className="block hover:underline"
    >
      {meeting.snippet && <q className="italic">{meeting.snippet}</q>}
      {meeting.snippet && ' · '}
      {meeting.approvedBy ? t('meeting.approvedBy', { name: meeting.approvedBy.name }) : t('meeting.autoApplied')}
    </Link>
  );
}

function Meta({ at, via, source, ago }: { at: string; via: Surface; source?: string | null; ago(iso: string): string }) {
  const { t } = useTranslation();
  const sourceName = useSourceName();
  const how = via === 'import' && source ? t('feed.viaImport', { source: sourceName(source) }) : t('feed.via', { surface: t(`feed.surface.${via}`) });
  return (
    <span className="ml-auto shrink-0 text-sm text-mute" title={new Date(at).toLocaleString()}>
      {ago(at)}
      {via !== 'web' && ` · ${how}`}
    </span>
  );
}

/** Markdown textarea; paste or drop images to upload them. Ctrl/Cmd+Enter submits, Escape cancels. */
function CommentEditor({
  workspace,
  issueKey,
  initial,
  submitLabel,
  busy,
  autoFocus,
  onSubmit,
  onCancel,
}: {
  workspace: string;
  issueKey: string;
  initial: string;
  submitLabel: string;
  busy: boolean;
  autoFocus?: boolean;
  onSubmit(body: string, reset: () => void): void;
  onCancel?(): void;
}) {
  const { t } = useTranslation();
  const [body, setBody] = useState(initial);
  const upload = useMarkdownUpload(workspace, issueKey, setBody);
  const submit = () => body.trim() && onSubmit(body.trim(), () => setBody(''));
  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      submit();
    } else if (e.key === 'Escape' && onCancel) {
      e.preventDefault();
      e.stopPropagation();
      onCancel();
    }
  };

  return (
    <div className="flex flex-col gap-2">
      <textarea
        value={body}
        autoFocus={autoFocus}
        aria-label={t('feed.commentLabel')}
        placeholder={t('feed.placeholder')}
        onChange={(e) => setBody(e.target.value)}
        onKeyDown={onKeyDown}
        {...upload}
        className="field-sizing-content min-h-20 rounded-r1 border border-line bg-panel p-2 text-md"
      />
      <div className="flex items-center justify-end gap-2">
        {onCancel && (
          <Button variant="ghost" onClick={onCancel}>
            {t('common.cancel')}
          </Button>
        )}
        <Button onClick={submit} disabled={busy || !body.trim()}>
          {submitLabel} <Kbd>Ctrl ↵</Kbd>
        </Button>
      </div>
    </div>
  );
}
