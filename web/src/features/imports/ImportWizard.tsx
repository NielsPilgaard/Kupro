import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { ArrowRight } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import {
  describeImportSourceOptions,
  describeImportSourceQueryKey,
  getImportCsvPreviewOptions,
  getImportCsvPreviewQueryKey,
  getImportMappingOptions,
  getImportOptions,
  getImportQueryKey,
  getImportRunOptions,
  getTrelloAuthorizeLinkOptions,
  listImportRunsOptions,
  listImportsQueryKey,
  listMembersOptions,
  listProjectsOptions,
  listProjectsQueryKey,
  setImportAutoSyncMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import { createImport, getImportMapping, runImport, setImportMapping, uploadImportSource } from '../../api/gen/sdk.gen';
import type { CsvColumns, ImportMapping, ImportPlan, ImportUpload } from '../../api/gen/types.gen';
import { Topbar } from '../../app/AppShell';
import { useToast } from '../../components/Toasts';
import { Button, Kbd } from '../../components/ui';
import { useHours } from '../../lib/format';
import { useShortcuts } from '../../lib/shortcuts';
import { Chip, OkLine, Phases, ProgressBar, ProjectSummary, SourceMark, WarnLine } from './parts';
import { control, sourceOf, sources, useSourceName, type ConnectKind, type FileKind, type SourceInfo, type SourceKind, type UrlKind } from './sources';
import {
  ColumnsStep,
  CsvColumnsTable,
  EffortStep,
  FieldsStep,
  HierarchyStep,
  PeopleStep,
  ProjectsStep,
  ReviewStep,
  StatesStep,
  StepHead,
  TypesStep,
  type StepProps,
} from './steps';
import { usePlanner } from './usePlanner';
import {
  blockerOf,
  delta,
  formatBytes,
  mergeProposal,
  openDecisions,
  peopleCounts,
  phaseOf,
  runPhases,
  stepsFor,
  type Blocker,
  type Decisions,
  type StepId,
} from './wizard';

export interface WizardSearch {
  step?: StepId;
  run?: string;
  total?: number;
  first?: boolean;
}

/**
 * `/w/:ws/imports/new` and `/w/:ws/imports/:id/setup` (task 023, prototype 11): guided steps on the left, the live
 * dry run in the rail. `?run=` shows a commit's progress and result instead.
 */
export function ImportWizardPage() {
  const { workspace, importId } = useParams({ strict: false }) as { workspace: string; importId?: string };
  const search = useSearch({ strict: false }) as WizardSearch;
  if (importId && search.run) return <RunView workspace={workspace} importId={importId} runId={search.run} total={search.total} first={!!search.first} />;
  return <Wizard key={importId ?? 'new'} workspace={workspace} importId={importId} step={search.step} />;
}

function Wizard({ workspace, importId, step: wanted }: { workspace: string; importId?: string; step?: StepId }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const toast = useToast();
  const sourceName = useSourceName();
  const path = { workspace, id: importId ?? '' };
  const enabled = !!importId;
  const { data: imp } = useQuery({ ...getImportOptions({ path }), enabled });
  const described = useQuery({ ...describeImportSourceOptions({ path }), enabled, staleTime: Infinity, retry: false });
  // A CSV's rows, encoding and samples: the upload answers with them, a reload reads them again.
  const { data: csvPreview } = useQuery({ ...getImportCsvPreviewOptions({ path }), enabled: described.isSuccess && imp?.sourceKind === 'csv', staleTime: Infinity });
  const mappingQuery = useQuery({ ...getImportMappingOptions({ path }), enabled: enabled && described.isSuccess, staleTime: Infinity });
  const { data: runs } = useQuery({ ...listImportRunsOptions({ path }), enabled });
  const { data: members = [] } = useQuery(listMembersOptions({ path: { workspace } }));
  const { data: projects = [] } = useQuery(listProjectsOptions({ path: { workspace } }));
  const planner = usePlanner(workspace, importId);

  const [draft, setDraft] = useState<ImportMapping>();
  const mapping = draft ?? mappingQuery.data?.mapping;
  const hadCommit = !!runs?.some((r) => r.kind === 'commit' && r.state === 'succeeded');
  const [chosen, setChosen] = useState<Partial<Decisions>>({});
  const decided: Decisions = {
    fold: chosen.fold ?? hadCommit,
    effort: chosen.effort ?? (hadCommit || mapping?.hoursPerPoint != null),
  };
  const connected = described.isSuccess;
  const plan = planner.plan;
  const steps = stepsFor(plan, described.data);
  const step: StepId = wanted && steps.includes(wanted) ? wanted : connected ? 'projects' : 'source';
  const index = steps.indexOf(step);
  const ctx = { connected, mapping, plan, decided, description: described.data };
  const open = openDecisions(steps, ctx);
  const blocker = blockerOf(step, ctx);
  const [nudge, setNudge] = useState(0);

  // The first plan: once the mapping is known, dry-run it. A proposal is saved as the draft first: plans read the saved mapping.
  const planned = useRef(false);
  const request = planner.request;
  useEffect(() => {
    if (!mappingQuery.data || planned.current) return;
    planned.current = true;
    request(mappingQuery.data.saved ? null : mappingQuery.data.mapping, 0);
  }, [mappingQuery.data, request]);

  const update = (fn: (m: ImportMapping) => ImportMapping) => {
    if (!mapping) return;
    const next = fn(mapping);
    const was = blockerOf(step, ctx);
    setDraft(next);
    planner.request(next);
    if (was && !blockerOf(step, { ...ctx, mapping: next })) setNudge((n) => n + 1);
  };
  const decide = (key: keyof Decisions, value: boolean) => {
    const was = blockerOf(step, ctx);
    setChosen((c) => ({ ...c, [key]: value }));
    if (was && !blockerOf(step, { ...ctx, decided: { ...decided, [key]: value } })) setNudge((n) => n + 1);
  };

  // Source step, before the import exists.
  const [kind, setKind] = useState<string>(imp?.sourceKind ?? 'azure-devops');
  const [form, setForm] = useState({ url: '', email: '', token: '', file: null as File | null });
  const [csvColumns, setCsvColumns] = useState<CsvColumns>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const connect = async () => {
    const source = sourceOf(kind)!;
    setBusy(true);
    setError(undefined);
    try {
      const token = source.file ? undefined : source.email && form.email.trim() ? `${form.email.trim()}:${form.token.trim()}` : form.token.trim();
      const created = await createImport({ path: { workspace }, body: { sourceKind: kind, baseUrl: form.url.trim() || null, token: token || null } });
      if (!created.data) throw created.error;
      const id = created.data.id;
      void qc.invalidateQueries({ queryKey: listImportsQueryKey({ path: { workspace } }) });
      if (source.file && form.file) {
        try {
          await upload(id, form.file);
        } catch (e) {
          // The import exists now: retry the upload on its own source step, not by creating another draft.
          toast.error(problemText(e) ?? t('imports.errors.failed'));
          void navigate({ to: '/w/$workspace/imports/$importId/setup', params: { workspace, importId: id }, search: { step: 'source' } });
          return;
        }
      }
      void navigate({
        to: '/w/$workspace/imports/$importId/setup',
        params: { workspace, importId: id },
        search: { step: kind === 'csv' ? 'source' : 'projects' },
      });
    } catch (e) {
      setError(problemText(e) ?? t('imports.errors.failed'));
    } finally {
      setBusy(false);
    }
  };

  const upload = async (id: string, file: File) => {
    const result = await uploadImportSource({
      path: { workspace, id },
      body: file as never,
      bodySerializer: null,
      headers: { 'Content-Type': file.type || 'application/octet-stream' },
    });
    if (!result.data) throw result.error;
    const done = result.data as ImportUpload;
    if (done.csv) qc.setQueryData(getImportCsvPreviewQueryKey({ path: { workspace, id } }), done.csv);
  };

  const reupload = async (file: File) => {
    if (!importId) return;
    setBusy(true);
    setError(undefined);
    try {
      await upload(importId, file);
      setDraft(undefined);
      setCsvColumns(undefined);
      planned.current = false;
      await qc.invalidateQueries({ queryKey: describeImportSourceQueryKey({ path }) });
      await qc.resetQueries({ queryKey: getImportMappingOptions({ path }).queryKey });
    } catch (e) {
      setError(problemText(e) ?? t('imports.errors.failed'));
    } finally {
      setBusy(false);
    }
  };

  // Leaving a CSV's source step with changed columns: save them, and re-propose types, states and people from them.
  const applyCsvColumns = async () => {
    if (!importId || !mapping || !csvColumns) return true;
    setBusy(true);
    try {
      await setImportMapping({ path, body: { ...mapping, csv: csvColumns }, throwOnError: true });
      const proposed = (await getImportMapping({ path, query: { propose: true }, throwOnError: true })).data.mapping;
      const merged = mergeProposal(mapping, proposed);
      setDraft(merged);
      setCsvColumns(undefined);
      planner.request(merged, 0);
      await qc.invalidateQueries({ queryKey: describeImportSourceQueryKey({ path }) });
      return true;
    } catch (e) {
      setError(problemText(e) ?? t('imports.errors.failed'));
      return false;
    } finally {
      setBusy(false);
    }
  };

  // Every way off the source step (footer, stepper, rail) saves changed CSV columns first.
  const leave = async (target: StepId) => {
    if (!connected && target !== 'source') return;
    if (step === 'source' && target !== 'source' && csvColumns && !(await applyCsvColumns())) return;
    void navigate({ to: '.', search: { step: target }, replace: false });
  };
  const go = (target: StepId) => void leave(target);

  const start = async () => {
    // Commit only what the rail shows: a plan of the current mapping, not one still saving, planning or failed.
    if (!importId || open.length || !planner.current) return;
    setBusy(true);
    setError(undefined);
    try {
      if (draft) await setImportMapping({ path, body: draft, throwOnError: true });
      const run = await runImport({ path, throwOnError: true });
      void qc.invalidateQueries({ queryKey: listProjectsQueryKey({ path: { workspace } }) });
      // The cached mapping is the first proposal; the run view needs the saved one (its project keys).
      void qc.invalidateQueries({ queryKey: getImportMappingOptions({ path }).queryKey });
      void navigate({ to: '.', search: { run: run.data.id, total: plan?.create.count, first: !hadCommit || undefined } });
    } catch (e) {
      setError(problemText(e) ?? t('imports.errors.failed'));
    } finally {
      setBusy(false);
    }
  };

  const sourceReady = sourceOf(kind)?.file ? !!form.file : !!form.token.trim() && (sourceOf(kind)?.url !== 'required' || !!form.url.trim());

  const next = async () => {
    if (busy) return;
    // Before the import exists, the source blocker only clears by connecting.
    if (step === 'source' && !importId) return void (sourceReady && connect());
    if (blocker) return;
    if (step === 'review') return void start();
    const following = steps[index + 1];
    if (following) go(following);
  };

  useShortcuts({ 'mod+enter': () => void next() });

  // Focus the step heading on step change, so screen readers hear where they are.
  useEffect(() => {
    document.getElementById('step-heading')?.focus();
  }, [step]);

  const kindNow = imp?.sourceKind ?? kind;
  const stepProps: StepProps | null =
    mapping && described.data
      ? { mapping, update, description: described.data, plan, members, projects, decided, decide, go, steps, open }
      : null;
  const title = imp ? sourceName(imp.sourceKind) : t('importWizard.newFrom', { source: sourceName(kind) });

  let view: ReactNode;
  if (step === 'source') {
    view = connected ? (
      <ConnectedSource
        workspace={workspace}
        kind={kindNow}
        baseUrl={imp?.baseUrl}
        counts={described.data}
        busy={busy}
        onUpload={(f) => void reupload(f)}
        csv={
          kindNow === 'csv' && mapping?.csv ? (
            <CsvColumnsTable
              columns={csvColumns ?? mapping.csv}
              preview={csvPreview}
              onChange={setCsvColumns}
            />
          ) : null
        }
      />
    ) : importId ? (
      <SourceUpload kind={kindNow} busy={busy} onFile={(f) => void reupload(f)} />
    ) : (
      <SourcePicker workspace={workspace} kind={kind} onKind={setKind} form={form} onForm={setForm} />
    );
  } else if (stepProps) {
    const Step = { projects: ProjectsStep, columns: ColumnsStep, hierarchy: HierarchyStep, types: TypesStep, states: StatesStep, people: PeopleStep, fields: FieldsStep, effort: EffortStep, review: ReviewStep }[step];
    view = <Step {...stepProps} />;
  } else {
    view = <p className="text-mute">{t('common.loading')}</p>;
  }

  return (
    <>
      <Topbar
        crumbs={[t('imports.title'), title]}
        actions={
          planner.savedAt ? (
            <span className="text-sm text-mute" aria-live="polite">
              {t('importWizard.saved')}
            </span>
          ) : null
        }
      />
      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_340px] max-[900px]:grid-cols-1 max-[900px]:overflow-auto">
        <section aria-label={t('importWizard.setup')} className="flex min-h-0 min-w-0 flex-col">
          <Stepper steps={steps} current={step} status={(s) => (s === 'source' ? (connected ? 'done' : 'todo') : !connected ? 'locked' : blockerOf(s, ctx) ? 'attn' : steps.indexOf(s) < index ? 'done' : 'todo')} onGo={go} />
          <div className="@container flex min-h-0 flex-1 flex-col gap-3.5 overflow-auto px-5 pt-4 pb-6 max-[900px]:overflow-visible">
            {view}
            {(error || described.error) && step === 'source' && (
              <p role="alert" className="text-sm text-bad">
                {error ?? problemText(described.error)}
              </p>
            )}
            {error && step !== 'source' && (
              <p role="alert" className="text-sm text-bad">
                {error}
              </p>
            )}
          </div>
          <Footer
            steps={steps}
            index={index}
            blocker={blocker}
            plan={plan}
            planCurrent={planner.current}
            planError={planner.error}
            open={open}
            connected={connected || sourceReady}
            fileSource={!!sourceOf(kindNow)?.file}
            hadCommit={hadCommit}
            busy={busy}
            nudge={nudge}
            onBack={() => index > 0 && go(steps[index - 1]!)}
            onNext={() => void next()}
            onGo={go}
          />
        </section>
        <Rail steps={steps} step={step} connected={connected} planner={planner} mapping={mapping} decided={decided} open={open} onGo={go} />
      </div>
    </>
  );
}

function problemText(e: unknown): string | undefined {
  const p = e as { detail?: string; title?: string; message?: string } | null | undefined;
  return p?.detail ?? p?.title ?? p?.message ?? undefined;
}

type StepStatus = 'done' | 'attn' | 'todo' | 'locked';

/** Numbered pills joined by rules; scrolls sideways, never wraps (design guide: stepper). */
function Stepper({ steps, current, status, onGo }: { steps: StepId[]; current: StepId; status(s: StepId): StepStatus; onGo(s: StepId): void }) {
  const { t } = useTranslation();
  return (
    <nav aria-label={t('importWizard.stepsLabel')} className="shrink-0 overflow-x-auto border-b border-line bg-bg px-4 py-2">
      <ol className="flex items-center">
        {steps.map((s, i) => {
          const st = status(s);
          const isCurrent = s === current;
          const prev = i > 0 ? status(steps[i - 1]!) : null;
          const number = isCurrent ? i + 1 : st === 'done' ? '✓' : st === 'attn' ? '!' : i + 1;
          const circle = isCurrent
            ? 'border-acc bg-acc text-on-acc'
            : st === 'done'
              ? 'border-ok text-ok'
              : st === 'attn'
                ? 'border-warn bg-warn/18 text-ink'
                : 'border-line';
          return (
            <li key={s} className="flex shrink-0 items-center">
              {i > 0 && <span aria-hidden="true" className={`mx-0.5 h-px w-3.5 ${prev === 'done' ? 'bg-ok' : 'bg-line'}`} />}
              <button
                type="button"
                disabled={st === 'locked'}
                aria-current={isCurrent ? 'step' : undefined}
                aria-label={`${t('importWizard.stepLabel', { n: i + 1, label: t(`importWizard.steps.${s}`) })}${st === 'locked' ? '' : st === 'done' ? t('importWizard.stepDone') : st === 'attn' ? t('importWizard.stepAttn') : ''}`}
                onClick={() => onGo(s)}
                className={`inline-flex h-7 items-center gap-1.5 rounded-full pr-2.5 pl-1 whitespace-nowrap hover:enabled:bg-sel hover:enabled:text-ink disabled:opacity-55 ${isCurrent ? 'bg-sel font-semibold text-ink' : st === 'done' || st === 'attn' ? 'text-ink' : 'text-mute'}`}
              >
                <span aria-hidden="true" className={`inline-flex size-5 shrink-0 items-center justify-center rounded-full border-[1.5px] font-mono text-xs font-semibold ${circle}`}>
                  {number}
                </span>
                {t(`importWizard.steps.${s}`)}
              </button>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

function blockerText(t: TFunction, blocker: Blocker, plan?: ImportPlan) {
  if (blocker === 'projectKeys') return t('importWizard.blocked.projectKeys', { keys: plan?.projectKeysTaken?.join(', ') });
  if (blocker === 'projectsMissing') return t('importWizard.blocked.projectsMissing', { keys: plan?.projectsMissing?.join(', ') });
  return t(`importWizard.blocked.${blocker}`);
}

/** Back, what comes next or why not, and the one primary named after where it goes (design guide: step footer). */
function Footer(props: {
  steps: StepId[];
  index: number;
  blocker: Blocker | null;
  plan?: ImportPlan;
  planCurrent: boolean;
  planError?: string;
  open: StepId[];
  connected: boolean;
  fileSource: boolean;
  hadCommit: boolean;
  busy: boolean;
  nudge: number;
  onBack(): void;
  onNext(): void;
  onGo(s: StepId): void;
}) {
  const { t, i18n } = useTranslation();
  const { steps, index, blocker, plan, open } = props;
  const step = steps[index]!;
  const nextStep = steps[index + 1];
  const last = step === 'review';
  const number = new Intl.NumberFormat(i18n.language);
  let label: string;
  let message: ReactNode;
  let disabled = props.busy;
  if (step === 'source' && blocker) {
    label = t(props.fileSource ? 'importWizard.uploadContinue' : 'importWizard.connectContinue');
    disabled ||= !props.connected;
    message = props.connected ? <NextLine step={nextStep} /> : <WarnLine>{t('importWizard.blocked.source')}</WarnLine>;
  } else if (last) {
    label = props.hadCommit ? t('importWizard.runAgain') : t('importWizard.importN', { count: plan?.create.count ?? 0, n: number.format(plan?.create.count ?? 0) });
    disabled ||= open.length > 0 || !props.planCurrent;
    message = open.length ? (
      <WarnLine>
        {t('importWizard.decisionsLeft', { count: open.length })}{' '}
        {open.map((s, i) => (
          <span key={s}>
            {i > 0 && ', '}
            <button type="button" className="underline decoration-line underline-offset-3" onClick={() => props.onGo(s)}>
              {t(`importWizard.steps.${s}`)}
            </button>
          </span>
        ))}
      </WarnLine>
    ) : props.planError ? (
      <WarnLine>{t('importRail.failed', { error: props.planError })}</WarnLine>
    ) : !props.planCurrent ? (
      <span className="text-sm text-mute">{t('importRail.planning')}</span>
    ) : (
      <OkLine>{t('importWizard.ready')}</OkLine>
    );
  } else {
    label = t('importWizard.continueTo', { label: t(`importWizard.steps.${nextStep ?? step}`) });
    disabled ||= !!blocker;
    message = blocker ? <WarnLine>{blockerText(t, blocker, plan)}</WarnLine> : <NextLine step={nextStep} />;
  }

  return (
    <div className="relative flex shrink-0 items-center gap-3 border-t border-line bg-panel px-5 py-3 max-[900px]:sticky max-[900px]:bottom-0 max-[900px]:z-[2]">
      <div aria-hidden="true" className="absolute inset-x-0 -top-px h-0.5">
        <i className="block h-full bg-acc transition-[width] duration-[var(--dur-2)]" style={{ width: `${(index / steps.length) * 100}%` }} />
      </div>
      <Button onClick={props.onBack} disabled={index === 0}>
        {t('importWizard.back')}
      </Button>
      <div className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="text-xs text-mute">{t('importWizard.stepOf', { n: index + 1, total: steps.length, label: t(`importWizard.steps.${step}`) })}</span>
        <span className="min-w-0 text-sm">{message}</span>
      </div>
      <span className="text-xs text-mute max-[900px]:hidden">
        <Kbd>Ctrl</Kbd> <Kbd>Enter</Kbd>
      </span>
      <Button key={props.nudge} variant="primary" className={`h-[34px] px-4 ${props.nudge ? 'nudge' : ''}`} disabled={disabled} onClick={props.onNext}>
        {label}
        {!last && <ArrowRight size={14} strokeWidth={1.5} aria-hidden="true" />}
      </Button>
    </div>
  );
}

function NextLine({ step }: { step?: StepId }) {
  const { t } = useTranslation();
  if (!step) return null;
  return (
    <span className="block truncate">
      <b>{t('importWizard.next', { label: t(`importWizard.steps.${step}`) })}</b> <span className="text-mute">· {t(`importWizard.hints.${step}`)}</span>
    </span>
  );
}

type RowKey = 'create' | 'update' | 'skipped' | 'reparented' | 'promoted' | 'checklists' | 'placeholders' | 'invites' | 'labels' | 'sprints' | 'effort' | 'comments' | 'attachments' | 'timeEntries';

/** The live dry run (design guide: split pane with live rail). */
function Rail(props: {
  steps: StepId[];
  step: StepId;
  connected: boolean;
  planner: ReturnType<typeof usePlanner>;
  mapping?: ImportMapping;
  decided: Decisions;
  open: StepId[];
  onGo(s: StepId): void;
}) {
  const { t, i18n } = useTranslation();
  const hours = useHours();
  const { plan, previous, planning, error } = props.planner;
  const number = new Intl.NumberFormat(i18n.language);
  const people = peopleCounts(props.mapping);
  const waitsOnFold = props.steps.includes('hierarchy') && !props.decided.fold;

  if (!props.connected) {
    return (
      <aside aria-label={t('importRail.title')} className="flex flex-col gap-3.5 overflow-auto border-l border-line bg-panel p-4 max-[900px]:border-t max-[900px]:border-l-0">
        <h2 className="text-lg font-semibold">{t('importRail.title')}</h2>
        <p className="text-mute">{t('importRail.empty')}</p>
        <div className="flex flex-col gap-1 text-sm text-mute">
          <span>{t('importRail.then', { count: props.steps.length - 1 })}</span>
          {props.steps.slice(1).map((s, i) => (
            <span key={s}>
              {i + 2}. {t(`importWizard.steps.${s}`)}
            </span>
          ))}
        </div>
      </aside>
    );
  }

  const rows: [RowKey, number | undefined, number | undefined, string?][] = plan
    ? [
        ['create', plan.create.count, previous?.create.count],
        ...(plan.update.count ? [['update', plan.update.count, previous?.update.count] as [RowKey, number, number | undefined]] : []),
        ['skipped', plan.skippedByScope.count, previous?.skippedByScope.count],
        ['reparented', waitsOnFold ? undefined : plan.reparented.count, previous?.reparented.count],
        ['promoted', waitsOnFold ? undefined : plan.promoted.count, previous?.promoted.count],
        ['checklists', plan.foldedChecklists.count, previous?.foldedChecklists.count],
        ['placeholders', people.placeholders, undefined],
        ['invites', people.invites, undefined],
        ['labels', plan.labelsToCreate ?? 0, previous?.labelsToCreate],
        ['sprints', plan.sprintsToCreate ?? 0, previous?.sprintsToCreate],
        ['effort', plan.effortHours, previous?.effortHours, hours(plan.effortHours)],
        ['comments', plan.comments, previous?.comments],
        ['attachments', plan.attachments, previous?.attachments, `${number.format(plan.attachments)} · ${formatBytes(plan.attachmentBytes, i18n.language)}`],
        ...(plan.worklogs ? [['timeEntries', plan.worklogs, previous?.worklogs] as [RowKey, number, number | undefined]] : []),
      ]
    : [];

  return (
    <aside
      aria-label={t('importRail.title')}
      aria-live="polite"
      className="flex flex-col gap-3.5 overflow-auto border-l border-line bg-panel p-4 max-[900px]:border-t max-[900px]:border-l-0"
    >
      <div className="flex items-center gap-2">
        <h2 className="flex-1 text-lg font-semibold">{t('importRail.title')}</h2>
        {planning && <Chip tone="acc">{t('importRail.planning')}</Chip>}
      </div>
      <span className="text-xs text-mute">{t('importRail.lede')}</span>
      {props.open.length > 0 ? (
        <div className="flex flex-col gap-1.5 rounded-r2 border border-[color-mix(in_srgb,var(--warn)_45%,var(--line))] bg-[color-mix(in_srgb,var(--warn)_8%,var(--panel))] px-3 py-2.5">
          <span className="text-sm font-semibold">{t('importRail.needsYou')}</span>
          {props.open.map((s) => (
            <button key={s} type="button" className="text-left text-sm underline decoration-line underline-offset-3" onClick={() => props.onGo(s)}>
              {t(`importRail.decision.${s as 'projects' | 'columns' | 'hierarchy' | 'effort'}`)}
            </button>
          ))}
        </div>
      ) : (
        <OkLine className="text-sm">{t('importRail.noOpen')}</OkLine>
      )}
      {error && (
        <div role="alert" className="flex flex-col gap-1.5 text-sm">
          <WarnLine>{t('importRail.failed', { error })}</WarnLine>
          <Button className="self-start" onClick={() => props.planner.request(null, 0)}>
            {t('common.retry')}
          </Button>
        </div>
      )}
      {!plan && !error && <p className="text-sm text-mute">{t('importRail.first')}</p>}
      {plan && (
        <div className={`grid grid-cols-[1fr_auto_auto] items-center gap-x-2.5 gap-y-1.5 ${planning ? 'opacity-60' : ''}`}>
          {rows.map(([key, value, before, shown]) => {
            const d = value === undefined ? null : delta(value, before);
            return (
              <span key={key} className="contents">
                <span>{t(`importRail.rows.${key}`)}</span>
                <span className="text-right font-mono tabular-nums">{value === undefined ? '—' : (shown ?? number.format(value))}</span>
                <span className="min-w-[46px] text-right font-mono text-xs text-acc-ink">{d === null ? '' : `${d > 0 ? '+' : '−'}${number.format(Math.abs(d))}`}</span>
              </span>
            );
          })}
        </div>
      )}
      {props.step !== 'review' && (
        <Button className="self-start" onClick={() => props.onGo('review')}>
          {t('importRail.skipToReview')}
        </Button>
      )}
    </aside>
  );
}

/** Source cards (product marks; WIP ones blocked) and the connect form or drop zone for the picked one. */
function SourcePicker({
  workspace,
  kind,
  onKind,
  form,
  onForm,
}: {
  workspace: string;
  kind: string;
  onKind(k: string): void;
  form: { url: string; email: string; token: string; file: File | null };
  onForm(f: { url: string; email: string; token: string; file: File | null }): void;
}) {
  const { t } = useTranslation();
  const source = sourceOf(kind)!;
  const name = t(`importSource.${kind as SourceKind}.name`);
  return (
    <>
      <StepHead title={t('importWizard.sourceTitle')} lede={t('importWizard.sourceLede')} />
      <div role="group" aria-label={t('importWizard.steps.source')} className="grid grid-cols-[repeat(auto-fill,minmax(220px,1fr))] gap-2">
        {sources.map((s) => (
          <button
            key={s.kind}
            type="button"
            aria-pressed={s.kind === kind}
            disabled={s.wip}
            title={s.wip ? t('imports.notYet') : undefined}
            onClick={() => onKind(s.kind)}
            className="group flex min-w-0 items-center gap-2.5 rounded-r2 border border-line bg-panel p-2.5 text-left hover:enabled:border-acc-ink disabled:cursor-not-allowed disabled:border-dashed disabled:bg-sunk aria-pressed:border-acc-ink aria-pressed:bg-sel aria-pressed:shadow-[inset_0_0_0_1px_var(--acc-ink)]"
          >
            <span>
              <SourceMark kind={s.kind} />
            </span>
            <span className="flex min-w-0 flex-1 flex-col">
              <b className="truncate group-disabled:text-mute">{t(`importSource.${s.kind}.name`)}</b>
              <span className="truncate text-xs text-mute">{s.wip ? t('imports.notYet') : t(`importSource.${s.kind}.sub`)}</span>
            </span>
            {s.wip && <Chip tone="wip">{t('imports.wip')}</Chip>}
          </button>
        ))}
      </div>
      <div className="flex max-w-[580px] flex-col gap-3.5 rounded-r2 border border-line bg-panel p-4">
        <div className="flex items-center gap-2">
          <SourceMark kind={kind} large />
          <div className="flex flex-col">
            <h2 className="text-lg font-semibold">{t(source.file ? 'importWizard.uploadFrom' : 'importWizard.connectTo', { source: name })}</h2>
            <span className="text-xs text-mute">{t(`importSource.${kind as SourceKind}.sub`)}</span>
          </div>
        </div>
        {source.file ? (
          <DropZone kind={kind} accept={source.file} file={form.file} onFile={(file) => onForm({ ...form, file })} />
        ) : (
          <ConnectForm workspace={workspace} source={source} form={form} onForm={onForm} />
        )}
      </div>
    </>
  );
}

function ConnectForm({ workspace, source, form, onForm }: { workspace: string; source: SourceInfo; form: { url: string; email: string; token: string; file: File | null }; onForm(f: typeof form): void }) {
  const { t } = useTranslation();
  const field = 'flex flex-col gap-1';
  return (
    <>
      {source.url && (
        <label className={field}>
          <span className="text-sm font-semibold">{t('importWizard.url')}</span>
          <input className={control} type="url" required={source.url === 'required'} placeholder="https://" value={form.url} onChange={(e) => onForm({ ...form, url: e.target.value })} />
          <small className="text-sm text-mute">{t(`importSource.${source.kind as UrlKind}.urlHint`)}</small>
        </label>
      )}
      {source.email && (
        <label className={field}>
          <span className="text-sm font-semibold">{t('importWizard.email')}</span>
          <input className={control} type="email" autoComplete="off" value={form.email} onChange={(e) => onForm({ ...form, email: e.target.value })} />
          <small className="text-sm text-mute">{t('importWizard.emailHint')}</small>
        </label>
      )}
      <label className={field}>
        <span className="text-sm font-semibold">{t(`importSource.${source.kind as ConnectKind}.token`)}</span>
        <input className={control} type="password" autoComplete="off" value={form.token} onChange={(e) => onForm({ ...form, token: e.target.value })} />
        <small className="text-sm text-mute">{t('importWizard.tokenHint')}</small>
      </label>
      {source.authorize && <TrelloAuthorize workspace={workspace} />}
      {source.local && <span className="text-sm text-mute">{t('importWizard.behindVpn', { kind: source.kind })}</span>}
    </>
  );
}

/** Trello tokens belong to Kupro's API key, so the token comes from Trello's authorize page (ADR-019 Source access). */
function TrelloAuthorize({ workspace }: { workspace: string }) {
  const { t } = useTranslation();
  const { data, isError } = useQuery({ ...getTrelloAuthorizeLinkOptions({ path: { workspace } }), staleTime: Infinity, retry: false });
  if (isError) return <span className="text-sm text-mute">{t('importWizard.trelloNoKey')}</span>;
  if (!data) return null;
  return (
    <span className="text-sm text-mute">
      <a className="text-acc-ink underline" href={data.url} target="_blank" rel="noreferrer">
        {t('importWizard.trelloAuthorize')}
      </a>{' '}
      {t('importWizard.trelloAuthorizeHint')}
    </span>
  );
}

function DropZone({ kind, accept, file, onFile }: { kind: string; accept: string; file: File | null; onFile(f: File): void }) {
  const { t, i18n } = useTranslation();
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  return (
    <div
      onDragOver={(e) => {
        e.preventDefault();
        setOver(true);
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(e) => {
        e.preventDefault();
        setOver(false);
        const f = e.dataTransfer.files[0];
        if (f) onFile(f);
      }}
      className={`flex flex-col items-center gap-1 rounded-r2 border-[1.5px] border-dashed bg-sunk p-5 text-center text-mute ${over ? 'border-acc-ink' : 'border-line'}`}
    >
      <span>
        {t(`importWizard.drop.${kind as FileKind}`)}{' '}
        <Button className="h-6 px-2 text-sm" onClick={() => input.current?.click()}>
          {t('importWizard.choose')}
        </Button>
      </span>
      <input
        ref={input}
        type="file"
        accept={accept}
        hidden
        onChange={(e) => {
          const f = e.target.files?.[0];
          if (f) onFile(f);
        }}
      />
      {file ? (
        <span className="text-ink">{t('importWizard.chosen', { name: file.name, size: formatBytes(file.size, i18n.language) })}</span>
      ) : (
        <span className="text-xs">{t(`importWizard.dropHint.${kind as FileKind}`)}</span>
      )}
    </div>
  );
}

/** An upload import whose file hasn't arrived (or failed to read): pick one again. */
function SourceUpload({ kind, busy, onFile }: { kind: string; busy: boolean; onFile(f: File): void }) {
  const { t } = useTranslation();
  const source = sourceOf(kind);
  return (
    <>
      <StepHead title={t('importWizard.uploadFrom', { source: t(`importSource.${kind as SourceKind}.name`) })} />
      {source?.file ? <DropZone kind={kind} accept={source.file} file={null} onFile={onFile} /> : <p className="text-mute">{t('importWizard.noSource')}</p>}
      {busy && <p className="text-sm text-mute">{t('common.loading')}</p>}
    </>
  );
}

function ConnectedSource(props: {
  workspace: string;
  kind: string;
  baseUrl?: string | null;
  counts?: { projects?: unknown[]; people?: unknown[]; itemTypes?: unknown[] };
  busy: boolean;
  onUpload(f: File): void;
  csv: ReactNode;
}) {
  const { t } = useTranslation();
  const source = sourceOf(props.kind);
  const input = useRef<HTMLInputElement>(null);
  return (
    <>
      <StepHead title={t('importWizard.steps.source')} lede={t('importWizard.connectedLede')} />
      <div className="flex flex-wrap items-center gap-3 rounded-r2 border border-line bg-panel p-4">
        <SourceMark kind={props.kind} large />
        <div className="flex min-w-0 flex-1 flex-col">
          <b className="truncate">
            {t(`importSource.${props.kind as SourceKind}.name`)}
            {props.baseUrl ? ` · ${props.baseUrl.replace(/^https?:\/\//, '')}` : ''}
          </b>
          <span className="text-xs text-mute">
            {t('importWizard.readSource', { projects: props.counts?.projects?.length ?? 0, people: props.counts?.people?.length ?? 0, types: props.counts?.itemTypes?.length ?? 0 })}
          </span>
        </div>
        {source?.file && (
          <>
            <Button className="h-6 px-2 text-sm" disabled={props.busy} onClick={() => input.current?.click()}>
              {t('importWizard.uploadAgain')}
            </Button>
            <input
              ref={input}
              type="file"
              accept={source.file}
              hidden
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) props.onUpload(f);
              }}
            />
          </>
        )}
        <Link to="/w/$workspace/imports/new" params={{ workspace: props.workspace }} className="inline-flex h-6 items-center rounded-r1 border border-line bg-panel px-2 text-sm hover:bg-sel">
          {t('importWizard.changeSource')}
        </Link>
      </div>
      {props.csv}
    </>
  );
}

/** A commit in progress, then its result (prototype 11's run and finished screens). The tab can close. */
function RunView({ workspace, importId, runId, total, first }: { workspace: string; importId: string; runId: string; total?: number; first: boolean }) {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const sourceName = useSourceName();
  const path = { workspace, id: importId };
  const { data: imp } = useQuery(getImportOptions({ path }));
  const { data: detail } = useQuery({
    ...getImportRunOptions({ path: { ...path, runId } }),
    refetchInterval: (q) => (q.state.data && (q.state.data.run.state === 'queued' || q.state.data.run.state === 'running') ? 1500 : false),
  });
  const { data: mapping } = useQuery({ ...getImportMappingOptions({ path }), staleTime: Infinity });
  const { data: projects = [] } = useQuery(listProjectsOptions({ path: { workspace } }));
  const autoSync = useMutation({
    ...setImportAutoSyncMutation(),
    onSuccess: (i) => qc.setQueryData(getImportQueryKey({ path }), i),
  });
  const number = new Intl.NumberFormat(i18n.language);
  const run = detail?.run;
  const done = run?.state === 'succeeded';
  const failed = run?.state === 'failed';

  // A first run from a token import syncs hourly unless the owner unticks it (the finished screen's checkbox, on).
  // Applied once: `first` leaves the URL in the same step, so a reload or Back can't undo the owner's later choice.
  const switched = useRef(false);
  const mutateAutoSync = autoSync.mutate;
  useEffect(() => {
    if (!done || !first || switched.current || !imp) return;
    switched.current = true;
    if (imp.hasToken && !imp.autoSync) mutateAutoSync({ path: { workspace, id: importId }, body: { enabled: true } });
    void navigate({ to: '.', search: { run: runId, total }, replace: true });
  }, [done, first, imp, mutateAutoSync, importId, workspace, navigate, runId, total]);

  // The run made the projects: refresh the sidebar for every source, file or token.
  useEffect(() => {
    if (done) void qc.invalidateQueries({ queryKey: listProjectsQueryKey({ path: { workspace } }) });
  }, [done, qc, workspace]);

  const issues = run?.counts?.issues ?? 0;
  const phase = run ? phaseOf(run) : 'read';
  const phases = runPhases.map((p) => ({ id: p, label: t(`importRun.phases.${p}`) }));
  const scope = mapping?.mapping.scope?.projects ?? [];
  const targetKeys = Object.entries(mapping?.mapping.projects ?? {})
    .filter(([id]) => scope.length === 0 || scope.includes(id))
    .map(([, p]) => p.key);
  const firstProject = projects.find((p) => targetKeys.includes(p.key));
  const placeholders = peopleCounts(mapping?.mapping).placeholders;

  const retry = async () => {
    const again = await runImport({ path, throwOnError: true });
    void navigate({ to: '.', search: { run: again.data.id, total, first } });
  };

  const progress = (
    <div className="flex max-w-[640px] flex-col gap-2 rounded-r2 border border-line bg-panel p-4">
      <div className="flex items-center gap-2">
        <b className="flex-1">{run?.state === 'queued' ? t('importRun.queued') : t(`importRun.phases.${phase}`)}</b>
        <span className="font-mono tabular-nums">{total ? `${number.format(issues)} / ${number.format(total)}` : number.format(issues)}</span>
      </div>
      <ProgressBar value={total ? issues / total : 0} />
      <Phases phases={phases} current={runPhases.indexOf(phase)} />
    </div>
  );

  return (
    <>
      <Topbar crumbs={[t('imports.title'), imp ? sourceName(imp.sourceKind) : '']} />
      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_340px] max-[900px]:grid-cols-1 max-[900px]:overflow-auto">
        <section className="flex min-h-0 min-w-0 flex-col gap-3.5 overflow-auto px-5 pt-4 pb-6">
          {!done && !failed && (
            <>
              <StepHead title={t('importRun.title')} lede={t('importRun.lede')} />
              {progress}
            </>
          )}
          {failed && (
            <>
              <StepHead title={t('importRun.failedTitle')} lede={t('importRun.failedLede')} />
              {run?.error && <WarnLine>{run.error}</WarnLine>}
              <div className="flex flex-wrap gap-2">
                <Button variant="primary" onClick={() => void retry()}>
                  {t('common.retry')}
                </Button>
                <Link to="/w/$workspace/imports/$importId" params={{ workspace, importId }} className="inline-flex h-[var(--control)] items-center rounded-r1 border border-line bg-panel px-3 hover:bg-sel">
                  {t('importRun.open')}
                </Link>
              </div>
            </>
          )}
          {done && (
            <>
              <StepHead title={t('importRun.doneTitle')} />
              <OkLine>
                {t('importRun.doneLine', {
                  issues: number.format(issues),
                  conflicts: number.format(run?.counts?.conflicts ?? 0),
                  placeholders: number.format(placeholders),
                })}
              </OkLine>
              {detail?.report?.projects && (
                <div className="max-w-[640px]">
                  <ProjectSummary projects={detail.report.projects} />
                </div>
              )}
              <div className="flex max-w-[640px] flex-col gap-3 rounded-r2 border border-line bg-panel p-4">
                <h2 className="text-lg font-semibold">{t('importRun.next')}</h2>
                <p>{t('importRun.nextText')}</p>
                {imp?.hasToken && (
                  <label className="flex items-center gap-2">
                    <input type="checkbox" checked={imp.autoSync} disabled={autoSync.isPending} onChange={(e) => autoSync.mutate({ path, body: { enabled: e.target.checked } })} />
                    {t('importRun.autoSync', { source: sourceName(imp.sourceKind) })}
                  </label>
                )}
                <div className="flex flex-wrap gap-2">
                  <Link
                    to="/w/$workspace/imports/$importId"
                    params={{ workspace, importId }}
                    className="inline-flex h-[var(--control)] items-center gap-2 rounded-r1 bg-acc px-3 font-semibold text-on-acc hover:brightness-110"
                  >
                    {t('importRun.open')}
                    <ArrowRight size={14} strokeWidth={1.5} aria-hidden="true" />
                  </Link>
                  {firstProject && (
                    <Link
                      to="/w/$workspace/p/$project"
                      params={{ workspace, project: firstProject.key }}
                      search={{ view: 'list' }}
                      className="inline-flex h-[var(--control)] items-center rounded-r1 border border-line bg-panel px-3 hover:bg-sel"
                    >
                      {t('importRun.goTo', { name: firstProject.name })}
                    </Link>
                  )}
                </div>
              </div>
            </>
          )}
        </section>
        <aside aria-live="polite" className="flex flex-col gap-3.5 overflow-auto border-l border-line bg-panel p-4 max-[900px]:border-t max-[900px]:border-l-0">
          <h2 className="text-lg font-semibold">{done ? t('importRun.railDone') : failed ? t('importRun.failedTitle') : t('importRun.title')}</h2>
          <div className="flex items-center gap-2">
            <span className="flex-1 text-sm">{done ? t('importRun.doneTitle') : t(`importRun.phases.${phase}`)}</span>
            <span className="font-mono tabular-nums">{number.format(issues)}</span>
          </div>
          <ProgressBar value={done ? 1 : total ? issues / total : 0} />
          <span className="text-sm text-mute">{done ? t('importRun.railNote') : t('importRun.lede')}</span>
          {run?.counts && (
            <dl className="grid grid-cols-[1fr_auto] gap-x-2.5 gap-y-1 text-sm">
              {(['comments', 'attachments', 'timeEntries', 'changes', 'updated', 'renumbered'] as const)
                .filter((k) => run.counts?.[k])
                .map((k) => (
                  <span key={k} className="contents">
                    <dt>{t(`importRun.counts.${k}`)}</dt>
                    <dd className="text-right font-mono tabular-nums">{number.format(run.counts![k] ?? 0)}</dd>
                  </span>
                ))}
            </dl>
          )}
        </aside>
      </div>
    </>
  );
}
