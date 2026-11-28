import { useMutation, useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import {
  addAiConnectionMutation,
  listAiProvidersOptions,
  removeAiConnectionMutation,
  removeAiRouteMutation,
  setAiConnectionKeyMutation,
  setAiRouteMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import type { AiCapability, AiConnectionResponse, AiConnectionStatus, AiProviderResponse, AiRouteResponse } from '../../api/gen/types.gen';
import { useToast } from '../../components/Toasts';
import { Button } from '../../components/ui';
import { capabilities, errorText, useRefreshAi } from './ai';

/** What the dialog does: the connect flow from step 1, or one action on a connection that exists. */
export type WizardMode =
  | { kind: 'connect' }
  | { kind: 'replace'; connection: AiConnectionResponse }
  | { kind: 'routes'; connection: AiConnectionResponse };

type View = 'guide' | 'key' | 'routes' | 'custom' | 'replace';

const field = 'h-[var(--control)] min-w-0 rounded-r1 border border-line bg-panel px-2';
const scaleway = 'scaleway';
const custom = 'openai-compatible';

/**
 * "Go unlimited" (prototype 03): 1) make a Scaleway key, 2) paste and check it, 3) choose what runs on it; or a custom
 * EU endpoint instead of step 2. Step 2 adds the connection so the vendor really checks the key, without routing
 * anything; step 3 routes what was ticked. Cancel before the end removes what this dialog added, so nothing is half set up.
 */
export function AiWizard({ workspace, mode, routes, onClose }: { workspace: string; mode: WizardMode; routes: AiRouteResponse[]; onClose(): void }) {
  const { t } = useTranslation();
  const refresh = useRefreshAi();
  const { data: providers } = useQuery(listAiProvidersOptions({ path: { workspace } }));
  const remove = useMutation(removeAiConnectionMutation());
  const [view, setView] = useState<View>(mode.kind === 'connect' ? 'guide' : mode.kind);
  // The connection this dialog added and hasn't finished setting up; a ref too, so a retry removes the right one.
  const [attempt, setAttempt] = useState<AiConnectionResponse | null>(null);
  const attemptRef = useRef<AiConnectionResponse | null>(null);

  const discard = async (connection: AiConnectionResponse | null) => {
    if (connection) await remove.mutateAsync({ path: { workspace, id: connection.id } }).catch(() => undefined);
  };
  /** Replaces the attempt, removing the one before it (a retry with another key or address). */
  const replaceAttempt = (next: AiConnectionResponse | null) => {
    const previous = attemptRef.current;
    attemptRef.current = next;
    setAttempt(next);
    return discard(previous);
  };
  const cancel = () => {
    void replaceAttempt(null).then(refresh);
    onClose();
  };
  const finish = () => {
    refresh();
    onClose();
  };

  const connection = mode.kind === 'connect' ? attempt : mode.connection;
  const provider = (id: string) => providers?.find((p) => p.id === id);
  const label =
    view === 'replace' && connection
      ? t('ai.wizard.replaceTitle', { name: connection.name })
      : view === 'custom'
        ? t('ai.custom.title')
        : view === 'routes' && connection
          ? t('ai.wizard.routesTitle', { name: connection.name })
          : t('ai.wizard.label');
  const step = view === 'guide' ? 1 : view === 'key' ? 2 : 3;

  return (
    <Dialog label={label} focusKey={view} onClose={cancel}>
      {mode.kind === 'connect' && view !== 'custom' && <Stepper step={step} />}
      {view === 'guide' && <GuideStep onCustom={() => setView('custom')} onCancel={cancel} onNext={() => setView('key')} />}
      {view === 'key' && (
        <KeyStep
          workspace={workspace}
          attempt={attempt}
          onAttempt={replaceAttempt}
          onBack={() => setView('guide')}
          onNext={() => setView('routes')}
        />
      )}
      {view === 'routes' && connection && (
        <RoutesStep
          workspace={workspace}
          connection={connection}
          provider={provider(connection.providerType)}
          routes={routes}
          fresh={mode.kind === 'connect'}
          onCancel={cancel}
          onDone={finish}
        />
      )}
      {view === 'custom' && (
        <CustomStep
          workspace={workspace}
          attempt={attempt}
          onAttempt={replaceAttempt}
          onBack={() => setView('guide')}
          onDone={finish}
        />
      )}
      {view === 'replace' && connection && <ReplaceStep workspace={workspace} connection={connection} onCancel={onClose} onDone={finish} />}
    </Dialog>
  );
}

/** A modal over the page: focus moves in, stays in, and returns to the opener; Esc cancels. */
function Dialog({ label, focusKey, onClose, children }: { label: string; focusKey: string; onClose(): void; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const focusable = () => [...(ref.current?.querySelectorAll<HTMLElement>('input, select, textarea, button, a[href]') ?? [])].filter((el) => !el.hasAttribute('disabled'));

  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    return () => opener?.focus();
  }, []);

  useEffect(() => {
    const first = ref.current?.querySelector<HTMLElement>('[data-autofocus]') ?? focusable()[0];
    first?.focus();
  }, [focusKey]);

  const onKeyDown = (e: KeyboardEvent) => {
    // A modal: the app's single-key shortcuts don't reach the page behind it.
    e.stopPropagation();
    if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    }
    if (e.key === 'Tab') {
      const all = focusable();
      const [first, last] = [all[0], all[all.length - 1]];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last?.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first?.focus();
      }
    }
  };

  return (
    <div className="fixed inset-0 z-40 flex items-start justify-center overflow-auto bg-scrim px-4 pt-[10vh] pb-4">
      <div
        ref={ref}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        onKeyDown={onKeyDown}
        className="flex w-full max-w-[520px] min-w-0 flex-col gap-3 rounded-r2 border border-line bg-panel p-4 text-ink shadow-float"
      >
        {children}
      </div>
    </div>
  );
}

function Stepper({ step }: { step: number }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-2">
      <div className="flex gap-1.5" aria-hidden="true">
        {[1, 2, 3].map((i) => (
          <span key={i} className={`h-1 flex-1 rounded-[2px] ${i <= step ? 'bg-acc' : 'bg-line'}`} />
        ))}
      </div>
      <span className="font-mono text-sm text-mute">{t('ai.wizard.step', { step, total: 3 })}</span>
    </div>
  );
}

function Footer({ children }: { children: ReactNode }) {
  return <div className="flex flex-wrap items-center justify-end gap-2 pt-1">{children}</div>;
}

function GuideStep({ onCustom, onCancel, onNext }: { onCustom(): void; onCancel(): void; onNext(): void }) {
  const { t } = useTranslation();
  return (
    <>
      <h2 className="text-lg font-semibold">{t('ai.wizard.guideTitle')}</h2>
      <p className="text-mute">{t('ai.wizard.guideIntro')}</p>
      {/*
        TODO(task 035, ADR-022 IMP-002): confirm against Scaleway's console before launch: the menu path, the bearer
        type, and the exact permission set name (believed to be "GenerativeApisModelAccess"). Keep it out of the UI
        text until confirmed.
      */}
      <ol className="flex list-decimal flex-col gap-1.5 rounded-r2 border border-line bg-sunk py-3 pr-3 pl-8 marker:font-semibold marker:text-acc-ink">
        <li>{t('ai.wizard.guide1')}</li>
        <li>{t('ai.wizard.guide2')}</li>
        <li>{t('ai.wizard.guide3')}</li>
        <li>{t('ai.wizard.guide4')}</li>
      </ol>
      <a href="https://console.scaleway.com/" target="_blank" rel="noreferrer" className="self-start text-acc-ink underline">
        {t('ai.wizard.console')}
      </a>
      <Footer>
        <Button variant="ghost" className="mr-auto text-acc-ink!" onClick={onCustom}>
          {t('ai.wizard.custom')}
        </Button>
        <Button variant="ghost" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button variant="primary" onClick={onNext}>
          {t('ai.wizard.haveKey')}
        </Button>
      </Footer>
    </>
  );
}

/** The vendor's answer to a check, in the connection's own words: works, refused with its error, or couldn't check. */
export function CheckResult({ connection }: { connection: AiConnectionResponse }) {
  const { t } = useTranslation();
  const error = connection.lastError ?? '';
  const [symbol, tone, text, hint] =
    connection.status === 'healthy'
      ? ['✓', 'text-ok', t('ai.check.works', { name: connection.name }), null]
      : connection.status === 'rejected'
        ? ['×', 'text-bad', t('ai.check.rejected', { name: connection.name, error }), t('ai.check.rejectedHint')]
        : ['!', 'text-warn', t('ai.check.unverified', { error }), t('ai.check.unverifiedHint')];
  return (
    <div className="flex items-start gap-2 text-sm" data-testid="check-result">
      <span className={`w-3.5 shrink-0 text-center font-bold ${tone}`} aria-hidden="true">
        {symbol}
      </span>
      <span className="flex min-w-0 flex-col gap-0.5">
        <span className="break-words">{text}</span>
        {hint && <span className="text-mute">{hint}</span>}
      </span>
    </div>
  );
}

function Checking({ name }: { name: string }) {
  const { t } = useTranslation();
  return (
    <p className="flex items-center gap-2 text-sm text-mute">
      <span className="size-3.5 shrink-0 animate-spin rounded-full border-2 border-line border-t-acc" aria-hidden="true" />
      {t('ai.wizard.checking', { name })}
    </p>
  );
}

type AttemptProps = { workspace: string; attempt: AiConnectionResponse | null; onAttempt(next: AiConnectionResponse | null): Promise<void> };

function KeyStep({ workspace, attempt, onAttempt, onBack, onNext }: AttemptProps & { onBack(): void; onNext(): void }) {
  const { t } = useTranslation();
  const [key, setKey] = useState('');
  const [projectId, setProjectId] = useState('');
  const add = useMutation(addAiConnectionMutation());
  const works = attempt?.status === 'healthy';

  const check = async (e?: FormEvent) => {
    e?.preventDefault();
    if (!key.trim() || add.isPending) return;
    add.reset();
    await onAttempt(null);
    // No useFor: nothing is routed until step 3, where people choose.
    const added = await add
      .mutateAsync({ path: { workspace }, body: { providerType: scaleway, key: key.trim(), projectId: projectId.trim() || null } })
      .catch(() => null);
    if (added) await onAttempt(added);
  };

  return (
    <form className="flex flex-col gap-3" onSubmit={(e) => void check(e)}>
      <h2 className="text-lg font-semibold">{t('ai.wizard.keyTitle')}</h2>
      <label className="flex flex-col gap-1">
        <span className="text-sm font-semibold">{t('ai.wizard.secretKey')}</span>
        <input
          data-autofocus
          type="password"
          autoComplete="off"
          spellCheck={false}
          value={key}
          onChange={(e) => setKey(e.target.value)}
          className={`${field} font-mono`}
        />
        <span className="text-sm text-mute">{t('ai.wizard.secretKeyHint')}</span>
      </label>
      <label className="flex flex-col gap-1">
        <span className="text-sm font-semibold">{t('ai.wizard.projectId')}</span>
        <input value={projectId} onChange={(e) => setProjectId(e.target.value)} spellCheck={false} className={`${field} font-mono`} />
        <span className="text-sm text-mute">{t('ai.wizard.projectIdHint')}</span>
      </label>
      <div aria-live="polite" className="flex flex-col gap-2">
        {add.isPending && <Checking name="Scaleway" />}
        {!add.isPending && attempt && <CheckResult connection={attempt} />}
        {add.isError && (
          <p role="alert" className="text-sm text-bad">
            {errorText(t, add.error)}
          </p>
        )}
      </div>
      <Footer>
        <Button className="mr-auto" onClick={onBack}>
          {t('ai.wizard.back')}
        </Button>
        {works ? (
          <>
            <Button type="submit" disabled={!key.trim() || add.isPending}>
              {t('ai.wizard.checkAgain')}
            </Button>
            <Button variant="primary" onClick={onNext}>
              {t('ai.wizard.continue')}
            </Button>
          </>
        ) : (
          <Button variant="primary" type="submit" disabled={!key.trim() || add.isPending}>
            {attempt ? t('ai.wizard.checkAgain') : t('ai.wizard.check')}
          </Button>
        )}
      </Footer>
    </form>
  );
}

/**
 * Step 3, and "Change what runs where" later: one route per capability for every language (the web shows no
 * per-language routes, ADR-022 decision 3). Unticking only removes a route that points here.
 */
function RoutesStep({
  workspace,
  connection,
  provider,
  routes,
  fresh,
  onCancel,
  onDone,
}: {
  workspace: string;
  connection: AiConnectionResponse;
  provider: AiProviderResponse | undefined;
  routes: AiRouteResponse[];
  fresh: boolean;
  onCancel(): void;
  onDone(): void;
}) {
  const { t } = useTranslation();
  const toast = useToast();
  const path = { workspace };
  const caps = provider?.capabilities ?? capabilities;
  const current = (c: AiCapability) => routes.find((r) => r.capability === c && r.language === null);
  const mine = (c: AiCapability) => current(c)?.connectionId === connection.id;
  // A custom endpoint has no tested defaults, so each route names its model.
  const needsModel = (c: AiCapability) => (c === 'transcription' ? provider?.transcriptionModel : provider?.chatModel) == null;
  const [picked, setPicked] = useState<Record<string, boolean>>(() => Object.fromEntries(caps.map((c) => [c, fresh || mine(c)])));
  const [models, setModels] = useState<Record<string, string>>(() => Object.fromEntries(caps.map((c) => [c, mine(c) ? (current(c)?.model ?? '') : ''])));
  const set = useMutation(setAiRouteMutation());
  const unset = useMutation(removeAiRouteMutation());
  const [failed, setFailed] = useState<unknown>(null);
  const [saving, setSaving] = useState(false);
  const missingModel = caps.some((c) => picked[c] && needsModel(c) && !models[c]?.trim());

  const save = async () => {
    setSaving(true);
    setFailed(null);
    try {
      for (const c of caps) {
        const model = needsModel(c) ? (models[c] ?? '').trim() : undefined;
        if (picked[c] && (!mine(c) || (model && model !== current(c)?.model))) {
          await set.mutateAsync({ path, body: { capability: c, connectionId: connection.id, ...(model ? { model } : {}) } });
        } else if (!picked[c] && mine(c)) {
          await unset.mutateAsync({ path, query: { capability: c } });
        }
      }
      const count = caps.filter((c) => picked[c]).length;
      toast.info(count > 0 ? t('ai.wizard.live', { name: connection.name }) : t('ai.wizard.nothingRuns', { name: connection.name }));
      onDone();
    } catch (error) {
      setFailed(error);
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <h2 className="text-lg font-semibold">{t('ai.wizard.routesTitle', { name: connection.name })}</h2>
      <fieldset className="flex flex-col gap-2">
        <legend className="sr-only">{t('ai.wizard.routesTitle', { name: connection.name })}</legend>
        {caps.map((c) => {
          const route = current(c);
          const elsewhere = route?.connectionId && route.connectionId !== connection.id ? route.connectionName : null;
          return (
            <div key={c} className="flex flex-col gap-1">
              <label className="flex cursor-pointer items-start gap-2 rounded-r1 px-1 py-1 hover:bg-sel">
                <input type="checkbox" checked={picked[c]} onChange={(e) => setPicked({ ...picked, [c]: e.target.checked })} className="mt-1" />
                <span className="flex min-w-0 flex-col">
                  <span className="font-semibold">{t(`ai.capability.${c}`)}</span>
                  <span className="text-sm text-mute">{t(`ai.capabilityHint.${c}`)}</span>
                  {elsewhere && <span className="text-sm text-mute">{t('ai.wizard.nowOn', { name: elsewhere })}</span>}
                </span>
              </label>
              {picked[c] && mine(c) && route && !route.testedModel && route.model && <Untested model={route.model} />}
              {picked[c] && needsModel(c) && (
                <label className="flex flex-col gap-1 pl-7">
                  <span className="text-sm text-mute">{t('ai.wizard.model', { capability: t(`ai.capability.${c}`) })}</span>
                  <input value={models[c]} onChange={(e) => setModels({ ...models, [c]: e.target.value })} spellCheck={false} className={`${field} font-mono`} />
                </label>
              )}
            </div>
          );
        })}
      </fieldset>
      <p className="rounded-r2 border border-line bg-sunk p-3 text-sm">{t('ai.wizard.routesExplain', { name: connection.name })}</p>
      {missingModel && <p className="text-sm text-mute">{t('ai.errors.model_required')}</p>}
      {failed !== null && (
        <p role="alert" className="text-sm text-bad">
          {errorText(t, failed)}
        </p>
      )}
      <Footer>
        <Button variant="ghost" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button variant="primary" onClick={() => void save()} disabled={saving || missingModel}>
          {t('ai.wizard.done')}
        </Button>
      </Footer>
    </>
  );
}

/** "Not tested by Kupro" next to a route on a model other than the provider's tested default (ADR-022 decision 8). */
export function Untested({ model }: { model: string }) {
  const { t } = useTranslation();
  return (
    <span className="flex flex-wrap items-center gap-2 text-sm">
      <StatusChip tone="warn" symbol="!" label={t('ai.connection.untested')} />
      <span className="text-mute">{t('ai.connection.untestedHint', { model })}</span>
    </span>
  );
}

const tones = {
  ok: ['bg-ok/14 text-ink', 'text-ok'],
  warn: ['bg-warn/14 text-ink', 'text-warn'],
  bad: ['bg-bad/14 text-bad', 'text-bad'],
  acc: ['bg-sel text-acc-ink', ''],
  neutral: ['bg-mute/14 text-mute', ''],
} as const;

/** Status chip (design guide): a symbol first, so it reads without colour. */
export function StatusChip({ tone, symbol, label }: { tone: keyof typeof tones; symbol?: string; label: string }) {
  const [fill, mark] = tones[tone];
  return (
    <span className={`inline-flex h-5 shrink-0 items-center gap-1 rounded-r1 px-1.5 text-xs font-semibold ${fill}`}>
      {symbol && (
        <span className={mark} aria-hidden="true">
          {symbol}
        </span>
      )}
      {label}
    </span>
  );
}

const statusTone: Record<AiConnectionStatus, ['ok' | 'warn' | 'bad', string]> = {
  healthy: ['ok', '✓'],
  rejected: ['bad', '×'],
  unverified: ['warn', '!'],
};

export function ConnectionStatus({ status }: { status: AiConnectionStatus }) {
  const { t } = useTranslation();
  const [tone, symbol] = statusTone[status];
  return <StatusChip tone={tone} symbol={symbol} label={t(`ai.status.${status}`)} />;
}

/** "Use another EU endpoint instead": any OpenAI-compatible API, with the Owner's EU confirmation (ADR-022 decision 6). */
function CustomStep({ workspace, attempt, onAttempt, onBack, onDone }: AttemptProps & { onBack(): void; onDone(): void }) {
  const { t } = useTranslation();
  const toast = useToast();
  const [name, setName] = useState('');
  const [endpoint, setEndpoint] = useState('');
  const [key, setKey] = useState('');
  const [transcription, setTranscription] = useState(true);
  const [transcriptionModel, setTranscriptionModel] = useState('');
  const [chat, setChat] = useState(false);
  const [chatModel, setChatModel] = useState('');
  const [eu, setEu] = useState(false);
  const [blocked, setBlocked] = useState<string | null>(null);
  const add = useMutation(addAiConnectionMutation());

  const save = async (e: FormEvent) => {
    e.preventDefault();
    if (add.isPending) return;
    const servesNothing = !transcription && !chat;
    const unnamed = (transcription && !transcriptionModel.trim()) || (chat && !chatModel.trim());
    const reason = !endpoint.trim()
      ? t('ai.errors.endpoint_not_https')
      : !key.trim()
        ? t('ai.errors.key_required')
        : servesNothing || unnamed
          ? t('ai.custom.needServes')
          : !eu
            ? t('ai.errors.eu_confirmation_required')
            : null;
    setBlocked(reason);
    if (reason) return;
    add.reset();
    await onAttempt(null);
    const useFor: AiCapability[] = [...(transcription ? ['transcription' as const] : []), ...(chat ? (['meetingProposals', 'voiceCommands'] as const) : [])];
    const added = await add
      .mutateAsync({
        path: { workspace },
        body: {
          providerType: custom,
          name: name.trim() || null,
          endpoint: endpoint.trim(),
          key: key.trim(),
          euConfirmed: true,
          useFor,
          transcriptionModel: transcription ? transcriptionModel.trim() : null,
          chatModel: chat ? chatModel.trim() : null,
        },
      })
      .catch(() => null);
    if (!added) return;
    if (added.status === 'healthy') {
      // The API routed what was ticked because the check passed; keep the connection and close.
      toast.info(t('ai.custom.saved', { name: added.name }));
      onDone();
    } else {
      await onAttempt(added);
    }
  };

  const check = (checked: boolean, onChange: (v: boolean) => void, label: string, model: string, onModel: (v: string) => void, modelLabel: string) => (
    <div className="flex flex-col gap-1">
      <label className="flex items-start gap-2">
        <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="mt-1" />
        <span>{label}</span>
      </label>
      {checked && (
        <label className="flex flex-col gap-1 pl-6">
          <span className="text-sm text-mute">{modelLabel}</span>
          <input value={model} onChange={(e) => onModel(e.target.value)} spellCheck={false} placeholder={t('ai.custom.modelPlaceholder')} className={`${field} font-mono`} />
        </label>
      )}
    </div>
  );

  return (
    <form className="flex flex-col gap-3" onSubmit={(e) => void save(e)}>
      <h2 className="text-lg font-semibold">{t('ai.custom.title')}</h2>
      <p className="text-sm text-mute">{t('ai.custom.intro')}</p>
      <label className="flex flex-col gap-1">
        <span className="text-sm font-semibold">{t('ai.custom.name')}</span>
        <input data-autofocus value={name} onChange={(e) => setName(e.target.value)} placeholder={t('ai.custom.namePlaceholder')} className={field} />
      </label>
      <label className="flex flex-col gap-1">
        <span className="text-sm font-semibold">{t('ai.custom.endpoint')}</span>
        <input
          type="url"
          inputMode="url"
          spellCheck={false}
          value={endpoint}
          onChange={(e) => setEndpoint(e.target.value)}
          placeholder="https://ai.example.eu/v1"
          className={`${field} font-mono`}
        />
      </label>
      <label className="flex flex-col gap-1">
        <span className="text-sm font-semibold">{t('ai.custom.key')}</span>
        <input type="password" autoComplete="off" spellCheck={false} value={key} onChange={(e) => setKey(e.target.value)} className={`${field} font-mono`} />
        <span className="text-sm text-mute">{t('ai.custom.keyHint')}</span>
      </label>
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-1 text-sm font-semibold">{t('ai.custom.serves')}</legend>
        {check(transcription, setTranscription, t('ai.custom.transcription'), transcriptionModel, setTranscriptionModel, t('ai.custom.transcriptionModel'))}
        {check(chat, setChat, t('ai.custom.chat'), chatModel, setChatModel, t('ai.custom.chatModel'))}
        <span className="text-sm text-mute">{t('ai.custom.movesHint')}</span>
      </fieldset>
      <label className="flex items-start gap-2 rounded-r2 border border-line bg-sunk p-3">
        <input type="checkbox" checked={eu} onChange={(e) => setEu(e.target.checked)} className="mt-1" />
        <span className="flex flex-col gap-0.5">
          <span className="font-semibold">{t('ai.custom.eu')}</span>
          <span className="text-sm text-mute">{t('ai.custom.euHint')}</span>
        </span>
      </label>
      <div aria-live="polite" className="flex flex-col gap-2">
        {add.isPending && <Checking name={name.trim() || t('ai.custom.theEndpoint')} />}
        {!add.isPending && attempt && <CheckResult connection={attempt} />}
        {(blocked || add.isError) && (
          <p role="alert" className="text-sm text-bad">
            {blocked ?? errorText(t, add.error)}
          </p>
        )}
      </div>
      <Footer>
        <Button className="mr-auto" onClick={onBack}>
          {t('ai.wizard.back')}
        </Button>
        <Button variant="primary" type="submit" disabled={add.isPending}>
          {t('ai.custom.save')}
        </Button>
      </Footer>
    </form>
  );
}

/** Replace key: saved and checked with the vendor; a key that works turns a rejected connection back on. */
function ReplaceStep({ workspace, connection, onCancel, onDone }: { workspace: string; connection: AiConnectionResponse; onCancel(): void; onDone(): void }) {
  const { t } = useTranslation();
  const toast = useToast();
  const refresh = useRefreshAi();
  const [key, setKey] = useState('');
  const [result, setResult] = useState<AiConnectionResponse | null>(null);
  const save = useMutation(setAiConnectionKeyMutation());

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!key.trim() || save.isPending) return;
    const saved = await save.mutateAsync({ path: { workspace, id: connection.id }, body: { key: key.trim() } }).catch(() => null);
    if (!saved) return;
    refresh();
    if (saved.status === 'healthy') {
      toast.info(t('ai.wizard.replaced', { name: saved.name }));
      onDone();
    } else {
      setResult(saved);
    }
  };

  return (
    <form className="flex flex-col gap-3" onSubmit={(e) => void submit(e)}>
      <h2 className="text-lg font-semibold">{t('ai.wizard.replaceTitle', { name: connection.name })}</h2>
      <label className="flex flex-col gap-1">
        <span className="text-sm font-semibold">{t('ai.wizard.secretKey')}</span>
        <input
          data-autofocus
          type="password"
          autoComplete="off"
          spellCheck={false}
          value={key}
          onChange={(e) => setKey(e.target.value)}
          className={`${field} font-mono`}
        />
        <span className="text-sm text-mute">{t('ai.wizard.secretKeyHint')}</span>
      </label>
      <div aria-live="polite" className="flex flex-col gap-2">
        {save.isPending && <Checking name={connection.name} />}
        {!save.isPending && result && <CheckResult connection={result} />}
        {save.isError && (
          <p role="alert" className="text-sm text-bad">
            {errorText(t, save.error)}
          </p>
        )}
      </div>
      <Footer>
        <Button variant="ghost" onClick={onCancel}>
          {result ? t('common.close') : t('common.cancel')}
        </Button>
        <Button variant="primary" type="submit" disabled={!key.trim() || save.isPending}>
          {t('ai.wizard.replaceSave')}
        </Button>
      </Footer>
    </form>
  );
}
