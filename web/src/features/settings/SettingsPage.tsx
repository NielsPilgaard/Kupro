import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  createApiTokenMutation,
  getMeOptions,
  getMeQueryKey,
  listApiTokensOptions,
  listApiTokensQueryKey,
  listWorkspacesOptions,
  revokeApiTokenMutation,
  updateMeMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import { Button } from '../../components/ui';
import { useDate } from '../../lib/format';

/** Profile (language) and API tokens for the CLI, CI and agents. */
export function SettingsPage() {
  const { t } = useTranslation();
  const { data: workspaces } = useQuery(listWorkspacesOptions());
  return (
    <main className="mx-auto flex w-full max-w-2xl flex-col gap-8 overflow-auto p-6">
      <div className="flex items-center gap-3">
        <h1 className="font-display text-xl font-bold">{t('settings.title')}</h1>
        {workspaces?.[0] && (
          <Link to="/w/$workspace" params={{ workspace: workspaces[0].slug }} className="ml-auto text-acc-ink underline">
            {t('settings.back')}
          </Link>
        )}
      </div>
      <Profile />
      <Tokens />
    </main>
  );
}

function Profile() {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const { data: me } = useQuery(getMeOptions());
  const update = useMutation({
    ...updateMeMutation(),
    onSuccess: (profile) => {
      qc.setQueryData(getMeQueryKey(), profile);
      void i18n.changeLanguage(profile.locale);
    },
  });
  if (!me) return null;
  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">{t('settings.profile')}</h2>
      <p className="text-mute">
        {me.displayName} · {me.email}
      </p>
      <label className="flex items-center gap-3">
        <span className="w-32 text-sm">{t('settings.language')}</span>
        <select
          value={me.locale}
          onChange={(e) => update.mutate({ body: { locale: e.target.value } })}
          className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2"
        >
          <option value="en">English</option>
          <option value="da">Dansk</option>
        </select>
      </label>
    </section>
  );
}

function Tokens() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const date = useDate();
  const { data: tokens } = useQuery(listApiTokensOptions());
  const [name, setName] = useState('');
  const [secret, setSecret] = useState<string | null>(null);
  const refresh = () => void qc.invalidateQueries({ queryKey: listApiTokensQueryKey() });
  const create = useMutation({
    ...createApiTokenMutation(),
    onSuccess: (created) => {
      setSecret(created.secret);
      setName('');
      refresh();
    },
  });
  const revoke = useMutation({ ...revokeApiTokenMutation(), onSettled: refresh });

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">{t('settings.tokens')}</h2>
      <p className="text-sm text-mute">{t('settings.tokensHint')}</p>
      {secret && (
        <div className="flex flex-col gap-1 rounded-r2 border border-acc-ink p-3">
          <span className="text-sm font-semibold">{t('settings.tokenOnce')}</span>
          <code className="font-mono text-sm break-all select-all">{secret}</code>
        </div>
      )}
      <form
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim()) create.mutate({ body: { name: name.trim(), kind: 'personal' } });
        }}
      >
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={t('settings.tokenName')}
          aria-label={t('settings.tokenName')}
          className="h-[var(--control)] flex-1 rounded-r1 border border-line bg-panel px-2"
        />
        <Button variant="primary" type="submit" disabled={!name.trim()}>
          {t('settings.createToken')}
        </Button>
      </form>
      <ul className="flex flex-col">
        {tokens?.map((token) => (
          <li key={token.id} className="flex h-[var(--row)] items-center gap-3 border-b border-line">
            <span className="font-mono text-sm text-mute">{token.hint}</span>
            <span className="min-w-0 flex-1 truncate">
              {token.name}
              {token.agentName && <span className="text-mute"> · {token.agentName}</span>}
            </span>
            <span className="text-sm text-mute">
              {token.lastUsedAt ? t('settings.lastUsed', { date: date(token.lastUsedAt) }) : t('settings.neverUsed')}
            </span>
            <Button variant="ghost" className="h-7 px-2 text-bad" onClick={() => revoke.mutate({ path: { id: token.id } })}>
              {t('settings.revoke')}
            </Button>
          </li>
        ))}
      </ul>
    </section>
  );
}
