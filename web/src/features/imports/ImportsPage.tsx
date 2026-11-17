import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { Flag, Pencil, RefreshCw, Undo2 } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import {
  checkImportUndoOptions,
  finishImportMutation,
  getImportOptions,
  getImportQueryKey,
  getImportRunOptions,
  listImportConflictsOptions,
  listImportConflictsQueryKey,
  listImportRunsOptions,
  listImportRunsQueryKey,
  listImportsOptions,
  listImportsQueryKey,
  listMembersOptions,
  listProjectsQueryKey,
  replaceImportTokenMutation,
  resolveImportConflictMutation,
  runImportMutation,
  setImportAutoSyncMutation,
  undoImportMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import type { ImportResponse, ImportRunResponse, ImportUndoCheck } from '../../api/gen/types.gen';
import { Topbar } from '../../app/AppShell';
import { Button } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { useDate, useDateTime, useRelativeTime } from '../../lib/format';
import { PlaceholderRow } from '../members/MembersPage';
import { Chip, Phases, SourceMark, Sparkline, TableCard, WarnLine } from './parts';
import { control, useSourceName } from './sources';
import { phaseOf, runPhases, sparkBars } from './wizard';

const active = (r?: ImportRunResponse | null) => r?.state === 'queued' || r?.state === 'running';
const poll = (runs?: ImportRunResponse[]) => (runs?.some(active) ? 2000 : false);

/** `/w/:ws/imports`: every import with its last run and a sparkline of runs (task 023, prototype 08). Owners only. */
export function ImportsPage() {
  const { t } = useTranslation();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const navigate = useNavigate();
  const { data: imports, isPending } = useQuery(listImportsOptions({ path: { workspace } }));

  return (
    <>
      <Topbar
        crumbs={[t('imports.title')]}
        actions={
          <Button onClick={() => void navigate({ to: '/w/$workspace/imports/new', params: { workspace } })}>{t('imports.new')}</Button>
        }
      />
      <main className="flex w-full max-w-5xl flex-col gap-4 overflow-auto p-6">
        <div className="flex flex-col gap-1">
          <h1 className="font-display text-xl font-bold">{t('imports.title')}</h1>
          <p className="text-mute">{t('imports.intro')}</p>
        </div>
        {!isPending && imports?.length === 0 && (
          <div className="flex flex-col items-start gap-3 rounded-r2 border border-line bg-panel p-4">
            <p>{t('imports.none')}</p>
            <Link to="/w/$workspace/imports/new" params={{ workspace }} className="inline-flex h-[var(--control)] items-center rounded-r1 bg-acc px-3 font-semibold text-on-acc hover:brightness-110">
              {t('imports.new')}
            </Link>
          </div>
        )}
        {imports && imports.length > 0 && (
          <ul className="flex flex-col rounded-r2 border border-line bg-panel">
            {imports.map((i) => (
              <ImportRow key={i.id} workspace={workspace} imp={i} />
            ))}
          </ul>
        )}
      </main>
    </>
  );
}

/** Status chip for a list row or the import's page. */
function useStatus() {
  const { t } = useTranslation();
  const date = useDate();
  return (i: ImportResponse, runs?: ImportRunResponse[]) => {
    const committed = runs?.some((r) => r.kind === 'commit' && r.state === 'succeeded') ?? i.lastRun?.kind === 'commit';
    if (i.state === 'finished') return { tone: 'neutral' as const, text: t('imports.status.finished', { date: date(i.finishedAt) }) };
    if (i.state === 'undone') return { tone: 'neutral' as const, text: t('imports.status.undone') };
    if (i.tokenRejectedAt) return { tone: 'bad' as const, text: t('imports.status.tokenRejected') };
    if (active(i.lastRun) && i.lastRun?.kind === 'commit') return { tone: 'acc' as const, text: t('imports.status.running') };
    if (i.lastRun?.kind === 'commit' && i.lastRun.state === 'failed') return { tone: 'bad' as const, text: t('imports.status.failed') };
    if (!committed) return { tone: 'warn' as const, text: t('imports.status.draft') };
    if (i.autoSync) return { tone: 'ok' as const, text: t('imports.status.syncing') };
    return { tone: 'ok' as const, text: t('imports.status.imported') };
  };
}

function ImportRow({ workspace, imp }: { workspace: string; imp: ImportResponse }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const subtitle = useImportSubtitle();
  const sourceName = useSourceName();
  const relative = useRelativeTime();
  const status = useStatus();
  const { data: runs } = useQuery({ ...listImportRunsOptions({ path: { workspace, id: imp.id } }), refetchInterval: (q) => poll(q.state.data) });
  const s = status(imp, runs);
  const committed = runs?.some((r) => r.kind === 'commit' && r.state === 'succeeded');
  const open = () => void navigate({ to: '/w/$workspace/imports/$importId', params: { workspace, importId: imp.id } });

  return (
    <li className="flex min-h-14 items-center gap-3 border-b border-line px-3 py-2 last:border-b-0 hover:bg-acc/5">
      <SourceMark kind={imp.sourceKind} large />
      <Link to="/w/$workspace/imports/$importId" params={{ workspace, importId: imp.id }} className="flex min-w-0 flex-1 flex-col">
        <b className="truncate">{sourceName(imp.sourceKind)}</b>
        <span className="truncate text-sm text-mute">{subtitle(imp)}</span>
      </Link>
      <div className="flex flex-col gap-0.5 max-[960px]:hidden">
        <Chip tone={s.tone} className="self-start">
          {s.text}
        </Chip>
        <span className="text-xs text-mute">{imp.lastRunAt ? t('imports.lastRun', { when: relative(imp.lastRunAt) }) : t('imports.neverRun')}</span>
      </div>
      <div className="w-36 max-[960px]:hidden">
        <Sparkline bars={sparkBars(runs ?? [])} />
      </div>
      <div className="flex gap-1 max-[960px]:hidden">
        {imp.state === 'active' && !committed ? (
          <Link
            to="/w/$workspace/imports/$importId/setup"
            params={{ workspace, importId: imp.id }}
            className="inline-flex h-6 items-center rounded-r1 border border-line bg-panel px-2 text-sm hover:bg-sel"
          >
            {t('imports.continueSetup')}
          </Link>
        ) : (
          <Button variant="ghost" className="h-6 px-2 text-sm" onClick={open}>
            {t('imports.report')}
          </Button>
        )}
      </div>
    </li>
  );
}

/** Under the source's name: the host it reads, or for an uploaded file when it started (the name alone repeats). */
function useImportSubtitle() {
  const { t } = useTranslation();
  const date = useDateTime();
  return (i: ImportResponse) => i.baseUrl?.replace(/^https?:\/\//, '') ?? t('imports.started', { date: date(i.createdAt) });
}

type Tab = 'runs' | 'conflicts' | 'people';

/** `/w/:ws/imports/:id`: runs, conflicts and placeholders, with Sync now, auto-sync, Undo and Finish (prototype 08). */
export function ImportPage() {
  const { t } = useTranslation();
  const { workspace, importId } = useParams({ strict: false }) as { workspace: string; importId: string };
  const search = useSearch({ strict: false }) as { tab?: Tab };
  const navigate = useNavigate();
  const sourceName = useSourceName();
  const subtitle = useImportSubtitle();
  const path = { workspace, id: importId };
  const { data: imp } = useQuery(getImportOptions({ path }));
  const { data: runs } = useQuery({ ...listImportRunsOptions({ path }), refetchInterval: (q) => poll(q.state.data) });
  const { data: conflicts } = useQuery(listImportConflictsOptions({ path }));
  const tab: Tab = search.tab ?? 'runs';
  const tabs: { id: Tab; label: string; count?: number }[] = [
    { id: 'runs', label: t('importPage.tabs.runs'), count: runs?.filter((r) => r.kind === 'commit').length },
    { id: 'conflicts', label: t('importPage.tabs.conflicts'), count: conflicts?.length },
    { id: 'people', label: t('importPage.tabs.people') },
  ];

  return (
    <>
      <Topbar crumbs={[t('imports.title'), imp ? sourceName(imp.sourceKind) : '']} />
      <main className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_300px] gap-4 overflow-auto p-6 max-[960px]:grid-cols-1">
        <div className="flex min-w-0 flex-col gap-3">
          <div className="flex items-center gap-3">
            {imp && <SourceMark kind={imp.sourceKind} large />}
            <div className="flex min-w-0 flex-col">
              <h1 className="truncate font-display text-xl font-bold">{imp ? sourceName(imp.sourceKind) : ''}</h1>
              <span className="truncate text-sm text-mute">{imp ? subtitle(imp) : ''}</span>
            </div>
          </div>
          <div role="tablist" className="flex gap-4 border-b border-line">
            {tabs.map((x) => (
              <button
                key={x.id}
                type="button"
                role="tab"
                aria-selected={tab === x.id}
                onClick={() => void navigate({ to: '.', search: { tab: x.id }, replace: true })}
                className={`-mb-px h-9 border-b-2 ${tab === x.id ? 'border-acc-ink font-semibold text-ink' : 'border-transparent text-mute hover:text-ink'}`}
              >
                {x.label}
                {x.count ? <span className="ml-1.5 text-xs text-mute">{x.count}</span> : null}
              </button>
            ))}
          </div>
          <div role="tabpanel" className="flex flex-col gap-3">
            {tab === 'runs' && <RunsTab workspace={workspace} importId={importId} runs={runs} />}
            {tab === 'conflicts' && <ConflictsTab workspace={workspace} importId={importId} sourceKind={imp?.sourceKind} />}
            {tab === 'people' && <PeopleTab workspace={workspace} />}
          </div>
        </div>
        {imp && <ConnectionCard workspace={workspace} imp={imp} runs={runs} />}
      </main>
    </>
  );
}

function RunsTab({ workspace, importId, runs }: { workspace: string; importId: string; runs?: ImportRunResponse[] }) {
  const { t, i18n } = useTranslation();
  const when = useDateTime();
  const [open, setOpen] = useState<string | null>(null);
  const number = new Intl.NumberFormat(i18n.language);
  const sorted = [...(runs ?? [])].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const running = sorted.find((r) => active(r) && r.kind === 'commit');
  const commits = sorted.filter((r) => r.kind === 'commit');
  const tone = { queued: 'neutral', running: 'acc', succeeded: 'ok', failed: 'bad' } as const;

  if (runs && runs.length === 0) return <p className="text-mute">{t('importRuns.none')}</p>;
  return (
    <>
      {running && (
        <div className="flex flex-col gap-2 rounded-r2 border border-line bg-panel p-4">
          <div className="flex items-center gap-2">
            <b className="flex-1">{running.state === 'queued' ? t('importRun.queued') : t(`importRun.phases.${phaseOf(running)}`)}</b>
            <span className="font-mono tabular-nums">{number.format(running.counts?.issues ?? 0)}</span>
          </div>
          <Phases phases={runPhases.map((p) => ({ id: p, label: t(`importRun.phases.${p}`) }))} current={runPhases.indexOf(phaseOf(running))} />
        </div>
      )}
      <TableCard>
        <thead>
          <tr>
            <th>{t('importRuns.col.run')}</th>
            <th>{t('importRuns.col.when')}</th>
            <th>{t('importRuns.col.kind')}</th>
            <th className="text-right!">{t('importRuns.col.created')}</th>
            <th className="text-right!">{t('importRuns.col.updated')}</th>
            <th className="text-right!">{t('importRuns.col.conflicts')}</th>
            <th>
              <span className="sr-only">{t('importRuns.details')}</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {sorted.slice(0, 20).map((r) => {
            const n = r.kind === 'commit' ? commits.length - commits.indexOf(r) : null;
            return (
              <tr key={r.id}>
                <td className="font-mono">{n ?? '·'}</td>
                <td>{when(r.createdAt)}</td>
                <td>
                  <span className="flex items-center gap-2">
                    <span className="text-mute">{t(`importRuns.kind.${r.kind}`)}</span>
                    {r.state !== 'succeeded' && <Chip tone={tone[r.state]}>{t(`importRuns.state.${r.state}`)}</Chip>}
                  </span>
                </td>
                <td className="text-right font-mono tabular-nums">{r.kind === 'commit' ? number.format(r.counts?.issues ?? 0) : ''}</td>
                <td className="text-right font-mono tabular-nums">{r.kind === 'commit' ? number.format(r.counts?.updated ?? 0) : ''}</td>
                <td className={`text-right font-mono tabular-nums ${r.counts?.conflicts ? 'text-bad' : ''}`}>{r.kind === 'commit' ? number.format(r.counts?.conflicts ?? 0) : ''}</td>
                <td>
                  <Button variant="ghost" className="h-6 px-2 text-sm" aria-expanded={open === r.id} onClick={() => setOpen(open === r.id ? null : r.id)}>
                    {t('imports.report')}
                  </Button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </TableCard>
      {open && <RunReport workspace={workspace} importId={importId} runId={open} />}
    </>
  );
}

function RunReport({ workspace, importId, runId }: { workspace: string; importId: string; runId: string }) {
  const { t, i18n } = useTranslation();
  const { data } = useQuery(getImportRunOptions({ path: { workspace, id: importId, runId } }));
  const number = new Intl.NumberFormat(i18n.language);
  if (!data) return null;
  const counts = Object.entries(data.run.counts ?? {}).filter(([k, v]) => typeof v === 'number' && v > 0 && k !== 'changesRead');
  return (
    <div className="flex flex-col gap-2 rounded-r2 border border-line bg-panel p-4 text-sm">
      {data.run.error && <WarnLine>{t('importRuns.error', { error: data.run.error })}</WarnLine>}
      {data.plan && <span>{t('importRuns.planLine', { create: number.format(data.plan.create.count), update: number.format(data.plan.update.count) })}</span>}
      {counts.length > 0 && (
        <dl className="grid grid-cols-[1fr_auto] gap-x-3 gap-y-1 sm:max-w-sm">
          {counts.map(([k, v]) => (
            <span key={k} className="contents">
              <dt>{t(`importRun.counts.${k}`, { defaultValue: k })}</dt>
              <dd className="text-right font-mono tabular-nums">{number.format(v as number)}</dd>
            </span>
          ))}
        </dl>
      )}
      {data.report && data.report.deletedInSource.count > 0 && (
        <WarnLine>
          {t('importRuns.deleted', { count: data.report.deletedInSource.count })}{' '}
          <span className="text-mute">{data.report.deletedInSource.samples.map((s) => s.key ?? s.title).join(', ')}</span>
        </WarnLine>
      )}
    </div>
  );
}

function ConflictsTab({ workspace, importId, sourceKind }: { workspace: string; importId: string; sourceKind?: string }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();
  const sourceName = useSourceName();
  const path = { workspace, id: importId };
  const { data: conflicts } = useQuery(listImportConflictsOptions({ path }));
  const resolve = useMutation({
    ...resolveImportConflictMutation(),
    onSuccess: (_, v) => toast.info(t(v.body.useSource ? 'importConflicts.usedSource' : 'importConflicts.kept')),
    onError: () => toast.error(t('imports.errors.failed')),
    onSettled: () => void qc.invalidateQueries({ queryKey: listImportConflictsQueryKey({ path }) }),
  });

  if (conflicts?.length === 0) return <p className="text-mute">{t('importConflicts.none')}</p>;
  return (
    <>
      <p className="text-sm text-mute">{t('importConflicts.lede')}</p>
      <TableCard>
        <thead>
          <tr>
            <th>{t('importConflicts.col.issue')}</th>
            <th>{t('importConflicts.col.field')}</th>
            <th>{t('importConflicts.col.kept')}</th>
            <th>{t('importConflicts.col.source', { source: sourceKind ? sourceName(sourceKind) : '' })}</th>
            <th>
              <span className="sr-only">{t('importConflicts.col.actions')}</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {conflicts?.map((c) => (
            <tr key={c.id}>
              <td>
                <Link to="." search={(prev: Record<string, unknown>) => ({ ...prev, issue: c.issueKey })} className="font-mono" title={c.issueTitle}>
                  {c.issueKey}
                </Link>
              </td>
              <td>{c.field}</td>
              <td className="max-w-48 truncate">{c.kupro ?? '—'}</td>
              <td className="max-w-48 truncate text-mute">{c.source ?? '—'}</td>
              <td>
                <span className="flex gap-1">
                  <Button className="h-6 px-2 text-sm" disabled={resolve.isPending} onClick={() => resolve.mutate({ path: { ...path, conflictId: c.id }, body: { useSource: false } })}>
                    {t('importConflicts.keep')}
                  </Button>
                  <Button variant="ghost" className="h-6 px-2 text-sm" disabled={resolve.isPending} onClick={() => resolve.mutate({ path: { ...path, conflictId: c.id }, body: { useSource: true } })}>
                    {t('importConflicts.useSource')}
                  </Button>
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </TableCard>
    </>
  );
}

function PeopleTab({ workspace }: { workspace: string }) {
  const { t } = useTranslation();
  const { data: members = [] } = useQuery(listMembersOptions({ path: { workspace } }));
  const [lastAction, setLastAction] = useState<string | null>(null);
  const people = members.filter((m) => m.kind === 'human');
  const placeholders = people.filter((m) => m.status === 'placeholder');
  const targets = people.filter((m) => m.status === 'active' || m.status === 'placeholder');
  if (placeholders.length === 0) return <p className="text-mute">{t('importPeople.none')}</p>;
  return (
    <>
      <p className="text-sm text-mute">{t('importPeople.lede')}</p>
      <ul className="flex flex-col">
        {placeholders.map((m) => (
          <PlaceholderRow
            key={m.id}
            member={m}
            targets={targets.filter((o) => o.id !== m.id)}
            workspace={workspace}
            isOwner
            lastAction={lastAction}
            onAction={setLastAction}
          />
        ))}
      </ul>
    </>
  );
}

/** The import's side card: source, token, auto-sync and the actions that end it. */
function ConnectionCard({ workspace, imp, runs }: { workspace: string; imp: ImportResponse; runs?: ImportRunResponse[] }) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();
  const date = useDate();
  const sourceName = useSourceName();
  const path = { workspace, id: imp.id };
  const number = new Intl.NumberFormat(i18n.language);
  const [confirm, setConfirm] = useState<'undo' | 'finish' | null>(null);
  const [token, setToken] = useState('');
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: getImportQueryKey({ path }) });
    void qc.invalidateQueries({ queryKey: listImportRunsQueryKey({ path }) });
    void qc.invalidateQueries({ queryKey: listImportsQueryKey({ path: { workspace } }) });
  };
  const fail = () => toast.error(t('imports.errors.failed'));
  const sync = useMutation({ ...runImportMutation(), onSuccess: () => toast.info(t('importPage.syncStarted')), onError: fail, onSettled: refresh });
  const autoSync = useMutation({ ...setImportAutoSyncMutation(), onError: fail, onSettled: refresh });
  const replace = useMutation({ ...replaceImportTokenMutation(), onSuccess: () => setToken(''), onError: fail, onSettled: refresh });
  const finish = useMutation({ ...finishImportMutation(), onSuccess: () => setConfirm(null), onError: fail, onSettled: refresh });
  const undo = useMutation({
    ...undoImportMutation(),
    onSuccess: (r) => {
      if (r.undone) {
        setConfirm(null);
        toast.info(t('importPage.undone', { count: r.issues }));
        void qc.invalidateQueries({ queryKey: listProjectsQueryKey({ path: { workspace } }) });
      }
    },
    onError: fail,
    onSettled: refresh,
  });
  const check = useQuery({ ...checkImportUndoOptions({ path }), enabled: confirm === 'undo', staleTime: 0 });
  const live = imp.state === 'active';
  const running = runs?.some(active);
  const committed = runs?.some((r) => r.kind === 'commit' && r.state === 'succeeded');
  const dl = 'grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm [&_dt]:text-mute';
  const sideButton = 'w-full justify-start';

  return (
    <aside className="flex flex-col gap-3 self-start rounded-r2 border border-line bg-panel p-4">
      <h2 className="font-semibold">{t('importPage.connection')}</h2>
      <dl className={dl}>
        <dt>{t('importPage.sourceLabel')}</dt>
        <dd>{sourceName(imp.sourceKind)}</dd>
        {imp.baseUrl && (
          <>
            <dt>{t('importPage.address')}</dt>
            <dd className="truncate">{imp.baseUrl.replace(/^https?:\/\//, '')}</dd>
          </>
        )}
        <dt>{t('importPage.token')}</dt>
        <dd>{imp.tokenRejectedAt ? t('importPage.tokenRejected', { date: date(imp.tokenRejectedAt) }) : imp.hasToken ? t('importPage.tokenKept') : t('importPage.tokenNone')}</dd>
        <dt>{t('importPage.created')}</dt>
        <dd>{date(imp.createdAt)}</dd>
        {imp.lastChangeAt && (
          <>
            <dt>{t('importPage.lastChange')}</dt>
            <dd>{date(imp.lastChangeAt)}</dd>
          </>
        )}
      </dl>
      {live && imp.tokenRejectedAt && (
        <form
          className="flex flex-col gap-1.5 rounded-r1 bg-sel p-2 text-sm"
          onSubmit={(e: FormEvent) => {
            e.preventDefault();
            if (token.trim()) replace.mutate({ path, body: { token: token.trim() } });
          }}
        >
          <span>{t('importPage.tokenRejectedText')}</span>
          <input type="password" autoComplete="off" className={control} value={token} onChange={(e) => setToken(e.target.value)} aria-label={t('importPage.replaceToken')} />
          <Button type="submit" variant="primary" className="self-start" disabled={!token.trim() || replace.isPending}>
            {t('importPage.replaceToken')}
          </Button>
        </form>
      )}
      {live && imp.hasToken && (
        <>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={imp.autoSync} disabled={autoSync.isPending || !committed} onChange={(e) => autoSync.mutate({ path, body: { enabled: e.target.checked } })} />
            {t('importPage.autoSync')}
          </label>
          <span className="text-xs text-mute">{t('importPage.autoSyncHint')}</span>
        </>
      )}
      {live && (
        <>
          {committed ? (
            <Button variant="primary" className={sideButton} disabled={running || sync.isPending} onClick={() => sync.mutate({ path })}>
              <RefreshCw size={14} strokeWidth={1.5} aria-hidden="true" />
              {t('imports.syncNow')}
            </Button>
          ) : (
            <Link
              to="/w/$workspace/imports/$importId/setup"
              params={{ workspace, importId: imp.id }}
              className="inline-flex h-[var(--control)] items-center gap-2 rounded-r1 bg-acc px-3 font-semibold text-on-acc hover:brightness-110"
            >
              {t('imports.continueSetup')}
            </Link>
          )}
          {committed && (
            <Link
              to="/w/$workspace/imports/$importId/setup"
              params={{ workspace, importId: imp.id }}
              search={{ step: 'review' }}
              className="inline-flex h-[var(--control)] items-center gap-2 rounded-r1 border border-line bg-panel px-3 hover:bg-sel"
            >
              <Pencil size={14} strokeWidth={1.5} aria-hidden="true" />
              {t('importPage.editMapping')}
            </Link>
          )}
          <hr className="border-line" />
          {committed && (
            <Button className={`${sideButton} text-bad`} disabled={running} onClick={() => setConfirm(confirm === 'undo' ? null : 'undo')} aria-expanded={confirm === 'undo'}>
              <Undo2 size={14} strokeWidth={1.5} aria-hidden="true" />
              {t('importPage.undo')}
            </Button>
          )}
          {confirm === 'undo' && (
            <UndoConfirm
              check={undo.data && !undo.data.undone ? undo.data.check : check.data}
              busy={undo.isPending || check.isPending}
              number={number}
              onConfirm={() => undo.mutate({ path })}
              onCancel={() => setConfirm(null)}
            />
          )}
          <Button className={sideButton} disabled={running} onClick={() => setConfirm(confirm === 'finish' ? null : 'finish')} aria-expanded={confirm === 'finish'}>
            <Flag size={14} strokeWidth={1.5} aria-hidden="true" />
            {t('importPage.finish')}
          </Button>
          {confirm === 'finish' && (
            <div role="alertdialog" aria-label={t('importPage.finish')} className="flex flex-col gap-2 rounded-r1 bg-sel p-2 text-sm">
              <span>{t('importPage.finishConfirm')}</span>
              <span className="flex gap-2">
                <Button variant="primary" disabled={finish.isPending} onClick={() => finish.mutate({ path })}>
                  {t('importPage.finish')}
                </Button>
                <Button variant="ghost" onClick={() => setConfirm(null)}>
                  {t('common.cancel')}
                </Button>
              </span>
            </div>
          )}
        </>
      )}
      <span className="text-xs text-mute">
        {t('importPage.cli')} <code className="rounded bg-sunk px-1 font-mono">kupro import report {imp.id}</code>
      </span>
    </aside>
  );
}

function UndoConfirm({
  check,
  busy,
  number,
  onConfirm,
  onCancel,
}: {
  check?: ImportUndoCheck;
  busy: boolean;
  number: Intl.NumberFormat;
  onConfirm(): void;
  onCancel(): void;
}) {
  const { t, i18n } = useTranslation();
  const list = (items: string[]) => new Intl.ListFormat(i18n.language, { type: 'conjunction' }).format(items);
  const blocked = (check?.blockerCount ?? 0) > 0;
  return (
    <div role="alertdialog" aria-label={t('importPage.undo')} className="flex flex-col gap-2 rounded-r1 bg-sel p-2 text-sm">
      <span>{t('importPage.undoConfirm')}</span>
      {check && check.editedCount > 0 && (
        <WarnLine>{t('importPage.edited', { count: check.editedCount, n: number.format(check.editedCount), keys: list(check.edited) })}</WarnLine>
      )}
      {blocked && (
        <WarnLine>
          {t('importPage.blocked', { count: check!.blockerCount, n: number.format(check!.blockerCount), list: list(check!.blockers.map((b) => b.issueKey)) })}
        </WarnLine>
      )}
      <span className="flex gap-2">
        <Button className="text-bad" disabled={busy || blocked || !check} onClick={onConfirm}>
          {t('importPage.undo')}
        </Button>
        <Button variant="ghost" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
      </span>
    </div>
  );
}
