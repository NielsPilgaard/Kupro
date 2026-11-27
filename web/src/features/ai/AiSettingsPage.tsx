import { useMutation, useQuery } from '@tanstack/react-query';
import { useParams } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  checkAiConnectionMutation,
  getAiStatusOptions,
  getMeetingSettingsOptions,
  getWorkspaceOptions,
  listMembersOptions,
  removeAiConnectionMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import type { AiConnectionResponse, AiRouteResponse, AiStatusResponse, MeetingUsageResponse } from '../../api/gen/types.gen';
import { Topbar } from '../../app/AppShell';
import { useToast } from '../../components/Toasts';
import { Button } from '../../components/ui';
import { useDate, useDateTime, useHours } from '../../lib/format';
import { errorText, useRefreshAi } from './ai';
import { AiWizard, ConnectionStatus, StatusChip, Untested, type WizardMode } from './AiWizard';

const card = 'flex flex-col gap-3 rounded-r2 border border-line bg-panel p-4';

/**
 * `/w/:workspace/settings/ai` (task 035, prototype 03 "usage first"): this month's hours on Kupro's included budget and
 * on the workspace's own key, "Go unlimited" for people with billing access, and the workspace's connections. Everyone
 * else sees the same read-only, with who to ask. Never shows a price for the vendor (the owner's pick note).
 */
export function AiSettingsPage() {
  const { t, i18n } = useTranslation();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const path = { workspace };
  const { data: info } = useQuery(getWorkspaceOptions({ path }));
  const member = !!info && info.role !== 'guest';
  const canEdit = info?.canManageBilling ?? false;
  const status = useQuery({ ...getAiStatusOptions({ path }), enabled: member });
  const { data: settings } = useQuery({ ...getMeetingSettingsOptions({ path }), enabled: member });
  const { data: members } = useQuery({ ...listMembersOptions({ path }), enabled: member && !canEdit });
  const [wizard, setWizard] = useState<WizardMode | null>(null);

  const billing = (members ?? [])
    .filter((m) => m.kind === 'human' && m.status === 'active' && (m.role === 'owner' || m.canManageBilling))
    .map((m) => m.name);
  const ask = billing.length > 0 ? new Intl.ListFormat(i18n.language, { type: 'disjunction' }).format(billing) : null;
  const data = status.data;
  const routed = (c: AiConnectionResponse) => data?.routes.some((r) => r.connectionId === c.id) ?? false;
  const rejected = data?.connections.filter((c) => c.status === 'rejected' && routed(c)) ?? [];

  return (
    <>
      <Topbar crumbs={[t('settings.title'), t('ai.title')]} />
      <main className="mx-auto flex w-full max-w-3xl min-w-0 flex-col gap-4 overflow-auto p-4 sm:p-6">
        <p className="text-mute">{t('ai.lede')}</p>
        {info?.role === 'guest' && <p>{t('ai.guest')}</p>}
        {status.isError && (
          <p role="alert" className="text-bad">
            {t('ai.loadFailed')}
          </p>
        )}
        {member && !data && !status.isError && <p className="text-mute">{t('common.loading')}</p>}
        {data && (
          <>
            {rejected.map((c) => (
              <RejectedBanner
                key={c.id}
                workspace={workspace}
                connection={c}
                canEdit={canEdit}
                ask={ask}
                onReplace={() => setWizard({ kind: 'replace', connection: c })}
              />
            ))}
            <Usage status={data} usage={settings?.usage ?? null} />
            {canEdit && data.connections.length === 0 && <GoUnlimited usage={settings?.usage ?? null} onStart={() => setWizard({ kind: 'connect' })} />}
            {!canEdit && (
              <p className="rounded-r2 border border-line bg-sunk p-3 text-sm" data-testid="ai-read-only">
                {t('ai.readOnly')} {ask && t('ai.askBilling', { names: ask })}
              </p>
            )}
            {data.connections.map((c) => (
              <ConnectionCard key={c.id} workspace={workspace} connection={c} routes={data.routes} canEdit={canEdit} onWizard={setWizard} />
            ))}
            {canEdit && data.connections.length > 0 && (
              <Button className="self-start" onClick={() => setWizard({ kind: 'connect' })}>
                {t('ai.connection.add')}
              </Button>
            )}
          </>
        )}
      </main>
      {wizard && data && <AiWizard workspace={workspace} mode={wizard} routes={data.routes} onClose={() => setWizard(null)} />}
    </>
  );
}

/** Minutes under an hour, hours above: "41 min", "12.4h". */
function useDuration() {
  const { t, i18n } = useTranslation();
  const hours = useHours();
  const number = new Intl.NumberFormat(i18n.language, { maximumFractionDigits: 0 });
  return (minutes: number) => (minutes < 60 ? t('ai.minutes', { value: number.format(minutes) }) : hours(minutes / 60));
}

/** Leads the page: Kupro's included hours (the meeting budget, task 017), then what ran on the workspace's own key. */
function Usage({ status, usage }: { status: AiStatusResponse; usage: MeetingUsageResponse | null }) {
  const { t, i18n } = useTranslation();
  const hours = useHours();
  const date = useDate();
  const duration = useDuration();
  const number = new Intl.NumberFormat(i18n.language);
  const month = new Intl.DateTimeFormat(i18n.language, { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(status.period));
  const own = status.usage.find((u) => u.paidBy === 'workspace');
  const ownRoutes = status.routes.some((r) => r.paidBy === 'workspace');
  const percent = usage && usage.hoursIncluded > 0 ? Math.min(100, (usage.hoursUsed / usage.hoursIncluded) * 100) : usage?.spent ? 100 : 0;

  return (
    <section aria-labelledby="ai-usage" className={card} data-testid="ai-usage">
      <div className="flex flex-wrap items-center gap-2">
        <h2 id="ai-usage" className="text-lg font-semibold first-letter:uppercase">
          {month}
        </h2>
        <span className="ml-auto">
          {ownRoutes ? <StatusChip tone="acc" label={t('ai.usage.unlimited')} /> : <StatusChip tone="neutral" label={t('ai.usage.included')} />}
        </span>
      </div>
      <div className="flex flex-col gap-1">
        <span className="text-sm text-mute">{t('ai.usage.kupro')}</span>
        {usage ? (
          <>
            <p className="flex flex-wrap items-baseline gap-x-2">
              <span className="font-display text-2xl font-bold">{hours(usage.hoursUsed)}</span>
              <span className="text-mute">{t('ai.usage.ofHours', { hours: hours(usage.hoursIncluded) })}</span>
            </p>
            <div
              role="progressbar"
              aria-label={t('ai.usage.kupro')}
              aria-valuemin={0}
              aria-valuemax={usage.hoursIncluded}
              aria-valuenow={usage.hoursUsed}
              aria-valuetext={t('ai.usage.meter', { used: hours(usage.hoursUsed), included: hours(usage.hoursIncluded) })}
              className="h-1.5 overflow-hidden rounded-[3px] bg-sunk"
            >
              <div className={`h-full ${usage.spent ? 'bg-warn' : 'bg-acc'}`} style={{ width: `${percent}%` }} />
            </div>
            <span className="text-sm text-mute">
              {usage.spent ? t('ai.usage.spent', { date: date(usage.resetsOn) }) : t('ai.usage.resets', { date: date(usage.resetsOn) })}
            </span>
          </>
        ) : (
          <span>{t('ai.usage.noBudget')}</span>
        )}
      </div>
      {(own || ownRoutes) && (
        <div className="flex flex-col gap-1 border-t border-line pt-3" data-testid="ai-usage-own">
          <span className="text-sm text-mute">{t('ai.usage.own')}</span>
          <p className="flex flex-wrap items-baseline gap-x-2">
            <span className="font-display text-2xl font-bold">{duration(own?.audioMinutes ?? 0)}</span>
            <span className="text-mute">{t('ai.usage.audio')}</span>
          </p>
          <span className="text-sm text-mute">
            {t('ai.usage.ownHint', { tokens: number.format((own?.inputTokens ?? 0) + (own?.outputTokens ?? 0)) })}
          </span>
        </div>
      )}
    </section>
  );
}

function GoUnlimited({ usage, onStart }: { usage: MeetingUsageResponse | null; onStart(): void }) {
  const { t } = useTranslation();
  const hours = useHours();
  return (
    <section className="flex flex-wrap items-center gap-4 rounded-r2 border border-line bg-panel p-4">
      <div className="flex min-w-0 flex-1 basis-56 flex-col gap-1">
        <h2 className="text-lg font-semibold">
          {usage ? t('ai.unlimited.title', { hours: hours(usage.hoursIncluded) }) : t('ai.unlimited.titleNoBudget')}
        </h2>
        <p className="text-mute">{t('ai.unlimited.body')}</p>
      </div>
      <Button variant="primary" onClick={onStart}>
        {t('ai.unlimited.start')}
      </Button>
    </section>
  );
}

/** The key stopped working: say what is off, that Kupro doesn't borrow its own key, and what to do (ADR-022 decision 7). */
function RejectedBanner({
  workspace,
  connection,
  canEdit,
  ask,
  onReplace,
}: {
  workspace: string;
  connection: AiConnectionResponse;
  canEdit: boolean;
  ask: string | null;
  onReplace(): void;
}) {
  const { t } = useTranslation();
  const dateTime = useDateTime();
  const [confirming, setConfirming] = useState(false);
  return (
    <section role="alert" className="flex flex-col gap-2 rounded-r2 border border-bad/35 bg-bad/12 p-3 text-sm" data-testid="ai-rejected">
      <h2 className="flex items-center gap-2 font-semibold">
        <span className="text-bad" aria-hidden="true">
          ×
        </span>
        {t('ai.rejected.title', { name: connection.name })}
      </h2>
      {connection.lastError && (
        <p className="break-words">
          {connection.lastCheckedAt
            ? t('ai.rejected.seen', { error: connection.lastError, date: dateTime(connection.lastCheckedAt) })
            : connection.lastError}
        </p>
      )}
      <p>{t('ai.rejected.body')}</p>
      {canEdit ? (
        <>
          <div className="flex flex-wrap gap-2">
            <Button variant="primary" onClick={onReplace}>
              {t('ai.connection.replaceKey')}
            </Button>
            <Button onClick={() => setConfirming(true)}>{t('ai.rejected.goBack')}</Button>
          </div>
          {confirming && <Disconnect workspace={workspace} connection={connection} onCancel={() => setConfirming(false)} />}
        </>
      ) : (
        ask && <p>{t('ai.rejected.ask', { names: ask })}</p>
      )}
    </section>
  );
}

/** In-page confirmation (never window.confirm): the connection, its key and its routes go. */
function Disconnect({ workspace, connection, onCancel }: { workspace: string; connection: AiConnectionResponse; onCancel(): void }) {
  const { t } = useTranslation();
  const toast = useToast();
  const refresh = useRefreshAi();
  const remove = useMutation({
    ...removeAiConnectionMutation(),
    onSuccess: () => {
      toast.info(t('ai.connection.disconnected', { name: connection.name }));
      refresh();
    },
  });
  return (
    <div role="alertdialog" aria-label={t('ai.connection.disconnectTitle', { name: connection.name })} className="flex flex-col gap-2 rounded-r1 bg-sel p-2 text-sm">
      <span>{t('ai.connection.disconnectConfirm', { name: connection.name })}</span>
      {remove.isError && (
        <span role="alert" className="text-bad">
          {errorText(t, remove.error)}
        </span>
      )}
      <div className="flex flex-wrap justify-end gap-2">
        <Button variant="ghost" onClick={onCancel} autoFocus>
          {t('common.cancel')}
        </Button>
        <Button variant="primary" disabled={remove.isPending} onClick={() => remove.mutate({ path: { workspace, id: connection.id } })}>
          {t('ai.connection.disconnect')}
        </Button>
      </div>
    </div>
  );
}

function ConnectionCard({
  workspace,
  connection,
  routes,
  canEdit,
  onWizard,
}: {
  workspace: string;
  connection: AiConnectionResponse;
  routes: AiRouteResponse[];
  canEdit: boolean;
  onWizard(mode: WizardMode): void;
}) {
  const { t } = useTranslation();
  const toast = useToast();
  const refresh = useRefreshAi();
  const dateTime = useDateTime();
  const [confirming, setConfirming] = useState(false);
  const check = useMutation({
    ...checkAiConnectionMutation(),
    onSuccess: (checked) => {
      if (checked.status === 'healthy') toast.info(t('ai.connection.works', { name: checked.name }));
      else toast.error(checked.lastError ?? t(`ai.status.${checked.status}`));
    },
    onError: (error) => toast.error(errorText(t, error)),
    onSettled: refresh,
  });
  const runs = routes.filter((r) => r.connectionId === connection.id);

  return (
    <section aria-labelledby={`ai-${connection.id}`} className={card} data-testid="ai-connection">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <h2 id={`ai-${connection.id}`} className="min-w-0 truncate text-lg font-semibold">
          {connection.name}
        </h2>
        <span className="font-mono text-sm text-mute">
          {connection.keyHint ? <span aria-label={t('ai.connection.key', { hint: connection.keyHint })}>••••{connection.keyHint}</span> : t('ai.connection.keySaved')}
        </span>
        <span className="ml-auto">
          <ConnectionStatus status={connection.status} />
        </span>
      </div>
      {connection.endpoint && <span className="font-mono text-sm break-all text-mute">{connection.endpoint}</span>}
      {connection.status !== 'healthy' && connection.lastError && (
        <p className="text-sm break-words">
          {connection.lastCheckedAt ? t('ai.connection.lastError', { error: connection.lastError, date: dateTime(connection.lastCheckedAt) }) : connection.lastError}
        </p>
      )}
      {runs.length > 0 ? (
        <div className="flex flex-col gap-1">
          <span className="text-sm text-mute">{t('ai.connection.runs')}</span>
          <ul className="flex flex-col gap-1.5">
            {runs.map((r) => (
              <li key={`${r.capability}:${r.language ?? ''}`} className="flex flex-col gap-1">
                <span className="flex flex-wrap items-baseline gap-x-2">
                  <span>{t(`ai.capability.${r.capability}`)}</span>
                  {r.language && <span className="text-sm text-mute">{t('ai.connection.language', { language: r.language })}</span>}
                  {r.model && <span className="font-mono text-sm text-mute">{r.model}</span>}
                </span>
                {!r.testedModel && r.model && <Untested model={r.model} />}
              </li>
            ))}
          </ul>
        </div>
      ) : (
        <p className="text-sm text-mute">{t('ai.connection.runsNothing')}</p>
      )}
      {canEdit && (
        <div className="flex flex-wrap gap-2">
          <Button onClick={() => onWizard({ kind: 'replace', connection })}>{t('ai.connection.replaceKey')}</Button>
          <Button onClick={() => check.mutate({ path: { workspace, id: connection.id } })} disabled={check.isPending}>
            {check.isPending ? t('ai.connection.checking') : t('ai.connection.check')}
          </Button>
          <Button onClick={() => onWizard({ kind: 'routes', connection })}>{t('ai.connection.routes')}</Button>
          <Button className="text-bad! sm:ml-auto" onClick={() => setConfirming(true)}>
            {t('ai.connection.disconnect')}
          </Button>
        </div>
      )}
      {confirming && <Disconnect workspace={workspace} connection={connection} onCancel={() => setConfirming(false)} />}
    </section>
  );
}
