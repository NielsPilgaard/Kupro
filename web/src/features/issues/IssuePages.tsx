import { useQuery } from '@tanstack/react-query';
import { Link, useParams, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { getMeOptions, getProjectOptions } from '../../api/gen/@tanstack/react-query.gen';
import { useIssues } from '../../api/issues';
import { Topbar } from '../../app/AppShell';
import { Board } from './Board';
import { IssueList } from './IssueList';

export type View = 'list' | 'board';

/** Project issues as list or board; the view lives in the URL so it survives reloads and links. */
export function ProjectIssuesPage() {
  const { t } = useTranslation();
  const { workspace, project } = useParams({ strict: false }) as { workspace: string; project: string };
  const { view = 'list' } = useSearch({ strict: false }) as { view?: View };
  const { data: info } = useQuery(getProjectOptions({ path: { workspace, key: project } }));
  const { data, isPending, isError } = useIssues(workspace, { project: [project], sort: view === 'board' ? 'rank' : 'updated' });

  const tabs = (
    <div role="tablist" aria-label={t('views.label')} className="flex rounded-r1 bg-sunk p-0.5">
      {(['list', 'board'] as const).map((v) => (
        <Link
          key={v}
          role="tab"
          aria-selected={view === v}
          to="."
          search={(prev: Record<string, unknown>) => ({ ...prev, view: v })}
          className={`h-6 rounded-[4px] px-3 leading-6 ${view === v ? 'bg-panel font-semibold shadow-sm' : 'text-mute hover:text-ink'}`}
        >
          {t(`views.${v}`)}
        </Link>
      ))}
    </div>
  );

  return (
    <>
      <Topbar crumbs={[info?.name ?? project, t('views.issues')]} tabs={tabs} />
      <main className="min-h-0 flex-1 overflow-auto">
        {isPending && <p className="p-8 text-center text-mute">{t('common.loading')}</p>}
        {isError && <p className="p-8 text-center text-bad">{t('errors.loadFailed')}</p>}
        {data &&
          (view === 'board' ? (
            <Board workspace={workspace} issues={data.items} />
          ) : (
            <IssueList issues={data.items} emptyText={t('issue.emptyProject')} />
          ))}
      </main>
    </>
  );
}

export function MyIssuesPage() {
  const { t } = useTranslation();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const { data: me } = useQuery(getMeOptions());
  const { data, isPending } = useIssues(workspace, { assignee: 'me' });
  return (
    <>
      <Topbar crumbs={[t('nav.myIssues')]} />
      <main className="min-h-0 flex-1 overflow-auto">
        {isPending ? (
          <p className="p-8 text-center text-mute">{t('common.loading')}</p>
        ) : (
          <IssueList issues={data?.items ?? []} emptyText={t('issue.emptyMine', { name: me?.displayName })} />
        )}
      </main>
    </>
  );
}
