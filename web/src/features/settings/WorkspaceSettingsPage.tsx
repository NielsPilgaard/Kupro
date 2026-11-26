import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { getWorkspaceOptions, getWorkspaceQueryKey, updateWorkspaceMutation } from '../../api/gen/@tanstack/react-query.gen';
import { Topbar } from '../../app/AppShell';
import { useToast } from '../../components/Toasts';

/** `/w/:workspace/settings`, Owners: workspace-wide switches. Time registration is off by default (ADR-021 Workspace setting). */
export function WorkspaceSettingsPage() {
  const { t } = useTranslation();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const qc = useQueryClient();
  const toast = useToast();
  const { data: info } = useQuery(getWorkspaceOptions({ path: { workspace } }));
  const update = useMutation({
    ...updateWorkspaceMutation(),
    onSuccess: (_d, vars) => {
      toast.info(t(vars.body.timeRegistration ? 'workspaceSettings.time.turnedOn' : 'workspaceSettings.time.turnedOff', { name: info?.name }));
    },
    onError: () => toast.error(t('workspaceSettings.failed')),
    onSettled: () => void qc.invalidateQueries({ queryKey: getWorkspaceQueryKey({ path: { workspace } }) }),
  });
  const on = update.isPending ? !!update.variables?.body.timeRegistration : !!info?.timeRegistration;
  const owner = info?.role === 'owner';

  return (
    <>
      <Topbar crumbs={[info?.name ?? workspace, t('nav.workspaceSettings')]} />
      <main className="mx-auto flex w-full max-w-2xl flex-col gap-6 overflow-auto p-6">
        {info && !owner && <p className="text-mute">{t('workspaceSettings.ownersOnly')}</p>}
        {owner && (
          <section className="flex flex-col gap-3 rounded-r2 border border-line bg-panel p-4">
            <div className="flex items-start gap-4">
              <div className="flex flex-1 flex-col gap-1">
                <h2 id="time-registration" className="font-display text-lg font-semibold">
                  {t('workspaceSettings.time.title')}
                </h2>
                <p className="text-sm text-mute">{t('workspaceSettings.time.intro')}</p>
              </div>
              <button
                type="button"
                role="switch"
                aria-checked={on}
                aria-labelledby="time-registration"
                disabled={update.isPending}
                onClick={() => update.mutate({ path: { workspace }, body: { timeRegistration: !on } })}
                className="relative h-[22px] w-[38px] shrink-0 rounded-full bg-line transition-colors duration-[var(--dur-1)] aria-checked:bg-acc"
              >
                <span
                  aria-hidden="true"
                  className={`absolute top-[3px] size-4 rounded-full bg-panel transition-[left] duration-[var(--dur-1)] ${on ? 'left-[19px]' : 'left-[3px]'}`}
                />
              </button>
            </div>
            <ul className="list-disc pl-5 text-sm text-mute">
              <li>{t('workspaceSettings.time.private')}</li>
              <li>{t('workspaceSettings.time.locked')}</li>
              <li>{t('workspaceSettings.time.kept')}</li>
            </ul>
          </section>
        )}
      </main>
    </>
  );
}
