import { useQuery } from '@tanstack/react-query';
import { useNavigate, useParams } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { listProjectsOptions } from '../../api/gen/@tanstack/react-query.gen';
import type { IssuePriority, IssueState, IssueType } from '../../api/gen/types.gen';
import { useCreateIssue, useMembers } from '../../api/issues';
import { useUi } from '../../app/UiContext';
import { Button } from '../../components/ui';
import { priorities, states, types } from '../../lib/issues';

/** `C` anywhere: title first, Enter creates, Ctrl+Enter from any field. Esc cancels. */
export function CreateIssueDialog({ workspace }: { workspace: string }) {
  const { t } = useTranslation();
  const ui = useUi();
  const navigate = useNavigate();
  const params = useParams({ strict: false }) as { project?: string };
  const { data: projects } = useQuery(listProjectsOptions({ path: { workspace } }));
  const { data: members } = useMembers(workspace);
  const create = useCreateIssue(workspace);
  const dialog = useRef<HTMLDialogElement>(null);

  const [title, setTitle] = useState('');
  // Empty means "the project you're looking at" (or the first one).
  const [chosenProject, setProject] = useState('');
  const project = chosenProject || params.project || projects?.[0]?.key || '';
  const [type, setType] = useState<IssueType>('task');
  const [state, setState] = useState<IssueState>('todo');
  const [priority, setPriority] = useState<IssuePriority>('none');
  const [assignee, setAssignee] = useState('');
  const [labels, setLabels] = useState('');
  const [description, setDescription] = useState('');

  useEffect(() => {
    if (ui.createOpen) {
      dialog.current?.showModal();
    } else {
      dialog.current?.close();
    }
  }, [ui.createOpen]);

  const reset = () => {
    setTitle('');
    setDescription('');
    setLabels('');
    setType('task');
    setPriority('none');
    setAssignee('');
    setProject('');
  };

  const submit = () => {
    if (!title.trim() || !project) return;
    create.mutate(
      {
        path: { workspace },
        body: {
          project,
          title: title.trim(),
          type,
          state,
          priority,
          assignee: assignee || null,
          description: description || null,
          labels: labels.split(',').map((l) => l.trim()).filter(Boolean),
        },
      },
      {
        onSuccess: (issue) => {
          ui.setActiveKey(issue.key);
          void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: issue.key }) });
        },
      },
    );
    reset();
    ui.setCreateOpen(false);
  };

  const field = 'h-[var(--control)] rounded-r1 border border-line bg-panel px-2';

  return (
    <dialog
      ref={dialog}
      aria-label={t('issue.new')}
      onClose={() => ui.setCreateOpen(false)}
      onKeyDown={(e) => {
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
          e.preventDefault();
          submit();
        }
      }}
      className="m-auto mt-16 w-[min(640px,calc(100%-32px))] rounded-r2 border border-line bg-panel p-0 text-ink shadow-float backdrop:bg-scrim"
    >
      {projects?.length === 0 ? (
        <p className="p-4 text-mute">{t('issue.needProject')}</p>
      ) : (
        <form
          method="dialog"
          className="flex flex-col gap-3 p-4"
          onSubmit={(e) => {
            e.preventDefault();
            submit();
          }}
        >
          <input
            autoFocus
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder={t('issue.titlePlaceholder')}
            aria-label={t('issue.title')}
            className="h-10 bg-transparent font-display text-lg font-bold outline-none focus-visible:outline-none"
          />
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder={t('issue.descriptionPlaceholder')}
            aria-label={t('issue.description')}
            rows={4}
            className="rounded-r1 border border-line bg-panel p-2 text-md"
          />
          <div className="flex flex-wrap gap-2">
            <select aria-label={t('issue.project')} value={project} onChange={(e) => setProject(e.target.value)} className={field}>
              {projects?.map((p) => (
                <option key={p.key} value={p.key}>
                  {p.key} · {p.name}
                </option>
              ))}
            </select>
            <select aria-label={t('issue.type')} value={type} onChange={(e) => setType(e.target.value as IssueType)} className={field}>
              {types.map((x) => (
                <option key={x} value={x}>
                  {t(`type.${x}`)}
                </option>
              ))}
            </select>
            <select aria-label={t('issue.state')} value={state} onChange={(e) => setState(e.target.value as IssueState)} className={field}>
              {states.map((x) => (
                <option key={x} value={x}>
                  {t(`state.${x}`)}
                </option>
              ))}
            </select>
            <select aria-label={t('issue.priority')} value={priority} onChange={(e) => setPriority(e.target.value as IssuePriority)} className={field}>
              {priorities.map((x) => (
                <option key={x} value={x}>
                  {t(`priority.${x}`)}
                </option>
              ))}
            </select>
            <select aria-label={t('issue.assignee')} value={assignee} onChange={(e) => setAssignee(e.target.value)} className={field}>
              <option value="">{t('issue.unassigned')}</option>
              <option value="me">{t('issue.me')}</option>
              {members
                ?.filter((m) => m.status === 'active')
                .map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name}
                  </option>
                ))}
            </select>
            <input
              value={labels}
              onChange={(e) => setLabels(e.target.value)}
              placeholder={t('issue.labelsPlaceholder')}
              aria-label={t('issue.labels')}
              className={`${field} min-w-32 flex-1`}
            />
          </div>
          <div className="flex items-center justify-end gap-2">
            <Button variant="ghost" onClick={() => ui.setCreateOpen(false)}>
              {t('common.cancel')}
            </Button>
            <Button variant="primary" type="submit" disabled={!title.trim()}>
              {t('issue.create')} <kbd className="border-on-acc/40 text-on-acc/80">Ctrl ↵</kbd>
            </Button>
          </div>
        </form>
      )}
    </dialog>
  );
}

