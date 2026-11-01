import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Navigate, useNavigate, useParams } from '@tanstack/react-router';
import { useState, type FormEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import {
  acceptInviteMutation,
  createProjectMutation,
  createWorkspaceMutation,
  listInvitesOptions,
  listProjectsOptions,
  listProjectsQueryKey,
  listWorkspacesOptions,
  listWorkspacesQueryKey,
} from '../../api/gen/@tanstack/react-query.gen';
import { Topbar } from '../../app/AppShell';
import { Button } from '../../components/ui';
import { Wordmark } from '../../components/Wordmark';
import { keyOf, slugOf } from '../../lib/format';

/** `/`: your workspace, or onboarding when you have none. */
export function HomePage() {
  const { data: workspaces, isPending } = useQuery(listWorkspacesOptions());
  if (isPending) return null;
  const first = workspaces?.[0];
  return first ? <Navigate to="/w/$workspace" params={{ workspace: first.slug }} replace /> : <Onboarding />;
}

/** `/w/:workspace`: first project's issues, or a prompt to create one. */
export function WorkspaceIndexPage() {
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const { data: projects, isPending } = useQuery(listProjectsOptions({ path: { workspace } }));
  if (isPending) return null;
  const first = projects?.[0];
  return first ? (
    <Navigate to="/w/$workspace/p/$project" params={{ workspace, project: first.key }} search={{ view: 'list' }} replace />
  ) : (
    <Navigate to="/w/$workspace/new-project" params={{ workspace }} replace />
  );
}

function Onboarding() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const { data: invites } = useQuery(listInvitesOptions());
  const [name, setName] = useState('');
  const slug = slugOf(name);
  const create = useMutation({
    ...createWorkspaceMutation(),
    onSuccess: (w) => {
      void qc.invalidateQueries({ queryKey: listWorkspacesQueryKey() });
      void navigate({ to: '/w/$workspace', params: { workspace: w.slug } });
    },
  });
  const accept = useMutation({
    ...acceptInviteMutation(),
    onSuccess: (w) => {
      void qc.invalidateQueries({ queryKey: listWorkspacesQueryKey() });
      void navigate({ to: '/w/$workspace', params: { workspace: w.slug } });
    },
  });

  return (
    <Centered>
      <Wordmark />
      <h1 className="font-display text-2xl font-bold">{t('onboarding.title')}</h1>
      {invites && invites.length > 0 && (
        <div className="flex w-full flex-col gap-2">
          {invites.map((invite) => (
            <div key={invite.memberId} className="flex items-center gap-3 rounded-r2 border border-line bg-panel p-3">
              <span className="flex-1">{t('onboarding.invited', { name: invite.workspaceName })}</span>
              <Button variant="primary" onClick={() => accept.mutate({ path: { memberId: invite.memberId } })}>
                {t('onboarding.join')}
              </Button>
            </div>
          ))}
        </div>
      )}
      <SimpleForm
        label={t('onboarding.workspaceName')}
        hint={slug ? t('onboarding.slug', { slug }) : t('onboarding.workspaceHint')}
        value={name}
        onChange={setName}
        submit={t('onboarding.create')}
        busy={create.isPending}
        error={create.isError ? t('onboarding.slugTaken') : undefined}
        onSubmit={() => slug.length >= 2 && create.mutate({ body: { name: name.trim(), slug } })}
      />
    </Centered>
  );
}

export function NewProjectPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const [name, setName] = useState('');
  const key = keyOf(name);
  const create = useMutation({
    ...createProjectMutation(),
    onSuccess: (p) => {
      void qc.invalidateQueries({ queryKey: listProjectsQueryKey({ path: { workspace } }) });
      void navigate({ to: '/w/$workspace/p/$project', params: { workspace, project: p.key }, search: { view: 'list' } });
    },
  });

  return (
    <>
      <Topbar crumbs={[t('nav.newProject')]} />
      <Centered>
        <h1 className="font-display text-2xl font-bold">{t('project.newTitle')}</h1>
        <SimpleForm
          label={t('project.name')}
          hint={key.length >= 2 ? t('project.keyHint', { key }) : t('project.nameHint')}
          value={name}
          onChange={setName}
          submit={t('project.create')}
          busy={create.isPending}
          error={create.isError ? t('project.keyTaken', { key }) : undefined}
          onSubmit={() => key.length >= 2 && create.mutate({ path: { workspace }, body: { key, name: name.trim() } })}
        />
      </Centered>
    </>
  );
}

function Centered({ children }: { children: ReactNode }) {
  return (
    <main className="mx-auto flex w-full max-w-md flex-1 flex-col items-center justify-center gap-6 p-4 2xl:max-w-lg 3xl:max-w-xl 3xl:gap-8">
      {children}
    </main>
  );
}

function SimpleForm(props: {
  label: string;
  hint: string;
  value: string;
  onChange(v: string): void;
  submit: string;
  busy: boolean;
  error?: string;
  onSubmit(): void;
}) {
  return (
    <form
      className="flex w-full flex-col gap-2 2xl:gap-3"
      onSubmit={(e: FormEvent) => {
        e.preventDefault();
        props.onSubmit();
      }}
    >
      <label className="flex flex-col gap-1">
        <span className="text-sm font-semibold 2xl:text-md">{props.label}</span>
        <input
          autoFocus
          value={props.value}
          onChange={(e) => props.onChange(e.target.value)}
          className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2 2xl:h-10 2xl:px-3 2xl:text-lg"
        />
      </label>
      <p className={`text-sm 2xl:text-md ${props.error ? 'text-bad' : 'text-mute'}`}>{props.error ?? props.hint}</p>
      <Button variant="primary" type="submit" disabled={props.busy} className="self-start 2xl:h-10 2xl:px-4 2xl:text-md">
        {props.submit}
      </Button>
    </form>
  );
}
