import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
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
import type { FeedChange, FeedItem, IssuePriority, IssueState, IssueType, RelationKind, Surface } from '../../api/gen/types.gen';
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

/** History and comments, oldest first, with a composer at the bottom (task 008). */
export function ActivityFeed({ workspace, issueKey }: { workspace: string; issueKey: string }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();
  const { data: items } = useQuery(listIssueActivityOptions({ path: { workspace, key: issueKey } }));
  const { data: me } = useQuery(getMeOptions());
  const { data: members } = useMembers(workspace);
  const myId = members?.find((m) => m.kind === 'human' && m.email === me?.email)?.id;
  const refresh = () => void qc.invalidateQueries({ queryKey: listIssueActivityQueryKey({ path: { workspace, key: issueKey } }) });
  const onError = () => toast.error(t('feed.failed'));
  const add = useMutation({ ...addCommentMutation(), onSuccess: refresh, onError });

  return (
    <section className="flex flex-col gap-3" aria-label={t('feed.title')}>
      <h3 className="text-sm font-semibold text-mute">{t('feed.title')}</h3>
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

function EventItem({ item }: { item: FeedItem }) {
  const { t } = useTranslation();
  const ago = useRelativeTime();
  const name = item.actor.name;

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
      default: {
        if (c.field === 'description') return t('feed.description', { name });
        const label = c.field in fieldLabels ? t(fieldLabels[c.field as keyof typeof fieldLabels]) : c.field;
        return t('feed.changed', { name, field: label.toLowerCase(), from: value(c.field, c.from), to: value(c.field, c.to) });
      }
    }
  };
  const lines = item.kind === 'created' ? [t('feed.created', { name })] : item.changes.map(line);

  return (
    <li className="flex gap-2 text-sm text-mute">
      <Avatar member={item.actor} size={16} />
      <span className="min-w-0 flex-1">
        {lines.map((l, i) => (
          <span key={i} className="block">
            {l}
          </span>
        ))}
      </span>
      <Meta at={item.at} via={item.via} ago={ago} />
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
        <span className="font-semibold">{item.actor.name}</span>
        {item.editedAt && <span className="text-mute">{t('feed.edited')}</span>}
        <Meta at={item.at} via={item.via} ago={ago} />
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
    </li>
  );
}

function Meta({ at, via, ago }: { at: string; via: Surface; ago(iso: string): string }) {
  const { t } = useTranslation();
  return (
    <span className="ml-auto shrink-0 text-sm text-mute" title={new Date(at).toLocaleString()}>
      {ago(at)}
      {via !== 'web' && ` · ${t('feed.via', { surface: t(`feed.surface.${via}`) })}`}
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
