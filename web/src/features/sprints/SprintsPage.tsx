import { useMutation, useQuery, useQueryClient, type Query } from '@tanstack/react-query';
import { Link, useParams, useSearch } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  closeSprintMutation,
  createSprintMutation,
  listProjectsOptions,
  listSprintsOptions,
  startSprintMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import type { SprintResponse } from '../../api/gen/types.gen';
import { useIssues } from '../../api/issues';
import { Topbar } from '../../app/AppShell';
import { Button } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { useDate, useHours } from '../../lib/format';
import { Board } from '../issues/Board';

const isSprintOrIssueQuery = (q: Query) => {
  const id = (q.queryKey[0] as { _id?: string } | undefined)?._id;
  return id === 'listSprints' || id === 'listIssues' || id === 'getSprint';
};

/** Sprint board = board filtered by the active sprint, with total and done hours (task 010). */
export function SprintsPage() {
  const { t } = useTranslation();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const search = useSearch({ strict: false }) as { project?: string };
  const { data: projects } = useQuery(listProjectsOptions({ path: { workspace } }));
  const project = search.project ?? projects?.[0]?.key;
  const { data: sprints } = useQuery({
    ...listSprintsOptions({ path: { workspace }, query: { project } }),
    enabled: !!project,
  });
  const active = sprints?.find((s) => s.state === 'active');
  const planned = sprints?.filter((s) => s.state === 'planned') ?? [];

  const tabs = (
    <div role="tablist" aria-label={t('nav.projects')} className="flex rounded-r1 bg-sunk p-0.5">
      {projects?.map((p) => (
        <Link
          key={p.key}
          role="tab"
          aria-selected={p.key === project}
          to="."
          search={{ project: p.key }}
          className={`h-6 rounded-[4px] px-3 leading-6 ${p.key === project ? 'bg-panel font-semibold shadow-sm' : 'text-mute hover:text-ink'}`}
        >
          {p.key}
        </Link>
      ))}
    </div>
  );

  return (
    <>
      <Topbar crumbs={[t('sprint.title')]} tabs={tabs} />
      <main className="flex min-h-0 flex-1 flex-col overflow-auto">
        {project && (
          <>
            {active ? (
              <ActiveSprint workspace={workspace} sprint={active} hasNext={planned.length > 0} />
            ) : (
              <p className="px-4 pt-4 text-mute">{t('sprint.noneActive')}</p>
            )}
            <Planned workspace={workspace} project={project} sprints={planned} canStart={!active} />
          </>
        )}
      </main>
    </>
  );
}

function ActiveSprint({ workspace, sprint, hasNext }: { workspace: string; sprint: SprintResponse; hasNext: boolean }) {
  const { t } = useTranslation();
  const hours = useHours();
  const date = useDate();
  const qc = useQueryClient();
  const toast = useToast();
  const [closing, setClosing] = useState(false);
  const { data } = useIssues(workspace, { sprint: sprint.id, sort: 'rank' });
  const close = useMutation({
    ...closeSprintMutation(),
    onSuccess: (result) => {
      setClosing(false);
      toast.info(t('sprint.closed', { name: result.sprint.name, count: result.moved, target: result.movedTo?.name ?? t('sprint.backlog') }));
      void qc.invalidateQueries({ predicate: isSprintOrIssueQuery });
    },
    onError: () => toast.error(t('sprint.closeFailed')),
  });
  const unfinished = data?.items.filter((i) => i.state !== 'done' && i.state !== 'canceled').length ?? 0;
  const progress = sprint.effort > 0 ? Math.round((sprint.doneEffort / sprint.effort) * 100) : 0;

  return (
    <section aria-label={sprint.name} className="flex min-h-[420px] flex-col">
      <header className="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 pt-4">
        <h2 className="font-display text-lg font-bold">{sprint.name}</h2>
        <span className="font-mono text-sm text-mute">
          {date(sprint.startDate)} – {date(sprint.endDate)}
        </span>
        <span className="font-mono text-sm tabular-nums" title={t('sprint.hoursHint')}>
          {t('sprint.hours', { done: hours(sprint.doneEffort), total: hours(sprint.effort) })}
        </span>
        <span className="h-1.5 w-32 overflow-hidden rounded-full bg-sunk" role="progressbar" aria-valuenow={progress} aria-valuemin={0} aria-valuemax={100}>
          <span className="block h-full bg-ok" style={{ width: `${progress}%` }} />
        </span>
        {sprint.goal && <span className="text-mute">{sprint.goal}</span>}
        <Button className="ml-auto" onClick={() => setClosing(true)}>
          {t('sprint.close')}
        </Button>
      </header>
      {closing && (
        <div role="dialog" aria-label={t('sprint.close')} className="mx-4 mt-3 flex flex-wrap items-center gap-2 rounded-r2 border border-line bg-panel p-3">
          <span className="flex-1">{t('sprint.closePrompt', { count: unfinished })}</span>
          <Button
            variant="primary"
            disabled={!hasNext || close.isPending}
            title={hasNext ? undefined : t('sprint.noNext')}
            onClick={() => close.mutate({ path: { workspace, id: sprint.id }, body: { moveUnfinishedTo: 'next' } })}
          >
            {t('sprint.toNext')}
          </Button>
          <Button disabled={close.isPending} onClick={() => close.mutate({ path: { workspace, id: sprint.id }, body: { moveUnfinishedTo: 'backlog' } })}>
            {t('sprint.toBacklog')}
          </Button>
          <Button variant="ghost" onClick={() => setClosing(false)}>
            {t('common.cancel')}
          </Button>
        </div>
      )}
      <div className="min-h-0 flex-1">{data && <Board workspace={workspace} issues={data.items} />}</div>
    </section>
  );
}

function Planned({ workspace, project, sprints, canStart }: { workspace: string; project: string; sprints: SprintResponse[]; canStart: boolean }) {
  const { t } = useTranslation();
  const date = useDate();
  const hours = useHours();
  const qc = useQueryClient();
  const toast = useToast();
  const refresh = () => void qc.invalidateQueries({ predicate: isSprintOrIssueQuery });
  const start = useMutation({ ...startSprintMutation(), onSuccess: refresh, onError: () => toast.error(t('sprint.startFailed')) });
  const create = useMutation({ ...createSprintMutation(), onSuccess: refresh, onError: () => toast.error(t('sprint.createFailed')) });
  const [name, setName] = useState('');
  const [startDate, setStartDate] = useState('');
  const [endDate, setEndDate] = useState('');

  return (
    <section className="flex flex-col gap-2 border-t border-line p-4" aria-label={t('sprint.planned')}>
      <h2 className="font-semibold">{t('sprint.planned')}</h2>
      {sprints.map((s) => (
        <div key={s.id} className="flex h-[var(--row)] items-center gap-3 border-b border-line">
          <span className="font-semibold">{s.name}</span>
          <span className="font-mono text-sm text-mute">
            {date(s.startDate)} – {date(s.endDate)}
          </span>
          <span className="font-mono text-sm text-mute tabular-nums">{t('sprint.issueCount', { count: s.issueCount, hours: hours(s.effort) })}</span>
          {canStart && (
            <Button className="ml-auto h-7" onClick={() => start.mutate({ path: { workspace, id: s.id } })}>
              {t('sprint.start')}
            </Button>
          )}
        </div>
      ))}
      <form
        className="flex flex-wrap items-center gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim() && startDate && endDate) {
            create.mutate({ path: { workspace }, body: { project, name: name.trim(), startDate, endDate } });
            setName('');
          }
        }}
      >
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder={t('sprint.namePlaceholder')} aria-label={t('sprint.name')} className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2" />
        <input type="date" value={startDate} onChange={(e) => setStartDate(e.target.value)} aria-label={t('issue.startDate')} className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2 font-mono text-sm" />
        <input type="date" value={endDate} onChange={(e) => setEndDate(e.target.value)} aria-label={t('issue.dueDate')} className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2 font-mono text-sm" />
        <Button type="submit" disabled={!name.trim() || !startDate || !endDate}>
          {t('sprint.plan')}
        </Button>
      </form>
    </section>
  );
}
