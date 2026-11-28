import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { Mic } from 'lucide-react';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { getMeOptions } from '../../api/gen/@tanstack/react-query.gen';
import type { VoiceInterpretation, VoiceTarget } from '../../api/gen/types.gen';
import { applyOperation, interpretAudio, interpretText, refreshIssues, transcribe, VoiceError, type VoiceScreen } from '../../api/voice';
import { useToast } from '../../components/Toasts';
import { recordUtterance, type Recording } from '../../lib/microphone';
import { VoiceCard, type CardState, type Selection } from './VoiceCard';
import { appliesAtOnce, blockingCodes, dictationField, insertAtCursor, replaceRange, routeFor, type DictationField } from './voice';

type Phase = 'idle' | 'listening' | 'thinking';

interface VoiceApi {
  style: 'topBar' | 'thumb';
  phase: Phase;
  /** Start listening (button down, hotkey down). */
  press(): void;
  /** Stop and send (button up, hotkey up). */
  release(): void;
}

const VoiceContext = createContext<VoiceApi | null>(null);

/** Null outside a workspace, where there is no voice control. */
// eslint-disable-next-line react-refresh/only-export-components
export const useVoice = () => useContext(VoiceContext);

/** A short tap latches the microphone open until the next tap, for people who can't hold a button. */
const TAP_MS = 300;

/**
 * Voice control everywhere in a workspace (task 036): push to talk with the person's button style or by holding
 * Ctrl+Space. A focused text field gets dictation; otherwise the utterance is read into changes (shown on a card, applied
 * through the issue endpoints with undo), a place (opened at once), or nothing. No always-on listening.
 */
export function VoiceProvider({ workspace, children }: { workspace: string; children: ReactNode }) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();
  const navigate = useNavigate();
  const { project } = useParams({ strict: false }) as { project?: string };
  const { issue } = useSearch({ strict: false }) as { issue?: string };
  const { data: me } = useQuery(getMeOptions());
  const style = me?.voiceButton ?? 'topBar';

  const [phase, setPhaseState] = useState<Phase>('idle');
  const [card, setCard] = useState<CardState | null>(null);
  const [busy, setBusy] = useState(false);
  const [announce, setAnnounce] = useState('');
  const recording = useRef<Promise<Recording | null> | null>(null);
  // The recording's time limit sends what was said, as letting go would.
  const releaseRef = useRef<() => void>(() => {});
  const field = useRef<DictationField | null>(null);
  const dictated = useRef<{ field: DictationField; range: { start: number; end: number }; said: string; tidied: string } | null>(null);
  // Read synchronously by press and release, which can come faster than a render.
  const phaseRef = useRef<Phase>('idle');
  const setPhase = useCallback((next: Phase) => {
    phaseRef.current = next;
    setPhaseState(next);
  }, []);

  const language = i18n.language === 'da' ? 'da' : 'en';
  const screen = useMemo<VoiceScreen>(() => ({ issue, project, language }), [issue, project, language]);

  const close = useCallback(() => {
    setCard(null);
    field.current?.focus();
  }, []);

  const failed = useCallback((error: unknown) => {
    const code = error instanceof VoiceError ? error.code : 'failed';
    setCard({ kind: 'blocked', code: blockingCodes.includes(code) ? code : 'failed' });
    setAnnounce(t('voice.blocked.failed'));
  }, [t]);

  const placeName = useCallback(
    (target: VoiceTarget) => {
      const place =
        target.view === 'issue' ? target.issue! : t(`voice.place.${target.view}`, { project: target.project ?? '' });
      return target.assignee ? t('voice.place.for', { place, name: target.assignee.name }) : place;
    },
    [t],
  );

  const apply = useCallback(
    async (selections: Selection[]) => {
      setBusy(true);
      const undos: (() => Promise<void>)[] = [];
      try {
        for (const s of selections) undos.push(await applyOperation(workspace, s.op, s.key));
      } catch (error) {
        toast.error(t('voice.applyFailed', { detail: error instanceof VoiceError ? error.message : '' }));
      } finally {
        setBusy(false);
        refreshIssues(qc);
      }
      if (undos.length === 0) return;
      setCard(null);
      const keys = [...new Set(selections.slice(0, undos.length).map((s) => s.key ?? t('voice.newIssue')))].join(', ');
      const message = t('voice.applied', { count: undos.length, keys });
      setAnnounce(message);
      toast.info(message, {
        label: t('voice.undo'),
        run: () =>
          void (async () => {
            try {
              for (const undo of undos.reverse()) await undo();
              toast.info(t('voice.undone'));
            } catch {
              toast.error(t('voice.undoFailed'));
            } finally {
              refreshIssues(qc);
            }
          })(),
      });
    },
    [workspace, qc, toast, t],
  );

  const handle = useCallback(
    (reading: VoiceInterpretation) => {
      if (reading.kind === 'navigate' && reading.navigate) {
        setCard(null);
        void navigate(routeFor(workspace, reading.navigate) as Parameters<typeof navigate>[0]);
        const message = t('voice.opened', { place: placeName(reading.navigate) });
        setAnnounce(message);
        toast.info(message);
        return;
      }
      if (appliesAtOnce(reading, me?.voiceAutoApply ?? false)) {
        void apply(reading.operations.map((op) => ({ op })));
        return;
      }
      setCard({ kind: 'reading', reading });
      setAnnounce(reading.kind === 'none' ? t(`voice.none.${reading.reason ?? 'notUnderstood'}`) : reading.text);
    },
    [workspace, navigate, placeName, toast, t, apply, me?.voiceAutoApply],
  );

  const press = useCallback(() => {
    if (phaseRef.current !== 'idle') return;
    field.current = dictationField(document.activeElement);
    setPhase('listening');
    setCard(style === 'topBar' || field.current ? { kind: 'listening', dictating: !!field.current } : null);
    setAnnounce(t(field.current ? 'voice.status.dictating' : 'voice.status.listening'));
    recording.current = recordUtterance(() => releaseRef.current()).catch(() => {
      setPhase('idle');
      setCard({ kind: 'blocked', code: 'microphone' });
      setAnnounce(t('voice.blocked.microphoneTitle'));
      return null;
    });
  }, [style, t, setPhase]);

  const release = useCallback(() => {
    if (phaseRef.current !== 'listening' || !recording.current) return;
    const pending = recording.current;
    recording.current = null;
    setPhase('thinking');
    setAnnounce(t('voice.status.thinking'));
    setCard((c) => (c ? { kind: 'thinking' } : c));
    void (async () => {
      const rec = await pending;
      if (!rec) {
        setPhase('idle');
        return;
      }
      const wav = await rec.stop();
      try {
        if (!wav) {
          setCard({ kind: 'reading', reading: { kind: 'none', text: '', operations: [], navigate: null, reason: 'nothingHeard', extractor: 'keyword', budgetSpent: false } });
          return;
        }
        const target = field.current;
        if (target) {
          const heard = await transcribe(workspace, language, wav);
          if (!heard.text) {
            setCard(null);
            return;
          }
          target.focus();
          const range = insertAtCursor(target, heard.text);
          setAnnounce(t('voice.dictated'));
          if (heard.tidied) {
            dictated.current = { field: target, range, said: heard.text, tidied: heard.tidied };
            setCard({ kind: 'tidy', said: heard.text, tidied: heard.tidied });
          } else setCard(null);
          return;
        }
        handle(await interpretAudio(workspace, screen, wav));
      } catch (error) {
        failed(error);
      } finally {
        setPhase('idle');
      }
    })();
  }, [workspace, language, screen, handle, failed, t, setPhase]);

  useEffect(() => {
    releaseRef.current = release;
  }, [release]);

  const type = useCallback(
    (text: string) => {
      setPhase('thinking');
      setCard({ kind: 'thinking' });
      interpretText(workspace, screen, text)
        .then(handle, failed)
        .finally(() => setPhase('idle'));
    },
    [workspace, screen, handle, failed, setPhase],
  );

  const tidy = useCallback(
    (accept: boolean) => {
      const d = dictated.current;
      dictated.current = null;
      setCard(null);
      if (!d) return;
      d.field.focus();
      if (accept && !replaceRange(d.field, d.range, d.said, d.tidied)) toast.info(t('voice.tidy.gone'));
    },
    [toast, t],
  );

  // Hold Ctrl+Space to talk, with either button style. Works in text fields too: that is dictation.
  useEffect(() => {
    let held = false;
    const down = (e: KeyboardEvent) => {
      if (e.code !== 'Space' || !e.ctrlKey || e.repeat) return;
      e.preventDefault();
      held = true;
      press();
    };
    const up = (e: KeyboardEvent) => {
      if (!held || (e.code !== 'Space' && e.key !== 'Control')) return;
      held = false;
      release();
    };
    window.addEventListener('keydown', down);
    window.addEventListener('keyup', up);
    return () => {
      window.removeEventListener('keydown', down);
      window.removeEventListener('keyup', up);
    };
  }, [press, release]);

  // The thumb button sits where toasts would; they move up above it.
  useEffect(() => {
    if (style !== 'thumb') return;
    document.documentElement.style.setProperty('--toast-bottom', '6.5rem');
    return () => {
      document.documentElement.style.removeProperty('--toast-bottom');
    };
  }, [style]);

  const api = useMemo<VoiceApi>(() => ({ style, phase, press, release }), [style, phase, press, release]);

  return (
    <VoiceContext.Provider value={api}>
      {children}
      {style === 'thumb' && <ThumbButton />}
      {card && (
        <VoiceCard
          state={card}
          variant={style === 'thumb' ? 'sheet' : 'card'}
          busy={busy}
          onApply={(s) => void apply(s)}
          onType={type}
          onTidy={tidy}
          onClose={close}
        />
      )}
      <div className="sr-only" aria-live="polite" role="status">
        {announce}
      </div>
    </VoiceContext.Provider>
  );
}

/**
 * Hold to talk. A short tap or the keyboard (Enter, Space) latches it open until the next press. The pointer goes down
 * without taking focus, so a focused text field still gets the dictation.
 */
function usePushToTalk(voice: VoiceApi) {
  const downAt = useRef(0);
  const latched = useRef(false);
  return {
    onPointerDown: (e: React.PointerEvent<HTMLButtonElement>) => {
      e.preventDefault();
      e.currentTarget.setPointerCapture?.(e.pointerId);
      if (latched.current) {
        latched.current = false;
        voice.release();
        return;
      }
      downAt.current = Date.now();
      voice.press();
    },
    onPointerUp: () => {
      if (latched.current || downAt.current === 0) return;
      if (Date.now() - downAt.current < TAP_MS) latched.current = true;
      else voice.release();
      downAt.current = 0;
    },
    onPointerCancel: () => {
      downAt.current = 0;
      latched.current = false;
      voice.release();
    },
    onClick: (e: React.MouseEvent<HTMLButtonElement>) => {
      // Keyboard activation (detail 0) toggles; pointer clicks are handled above.
      if (e.detail !== 0) return;
      if (voice.phase === 'listening') voice.release();
      else voice.press();
    },
    'aria-pressed': voice.phase === 'listening',
  };
}

function useLabel(phase: Phase) {
  const { t } = useTranslation();
  return t(phase === 'listening' ? 'voice.button.listening' : phase === 'thinking' ? 'voice.button.thinking' : 'voice.button.idle');
}

/** Prototype 01: a Talk button in the top bar, next to search. Renders nothing for the thumb style or outside a workspace. */
export function TopBarMic() {
  const { t } = useTranslation();
  const voice = useVoice();
  const handlers = usePushToTalk(voice ?? { style: 'topBar', phase: 'idle', press: () => {}, release: () => {} });
  const label = useLabel(voice?.phase ?? 'idle');
  if (!voice || voice.style !== 'topBar') return null;
  const live = voice.phase === 'listening';
  return (
    <button
      type="button"
      data-voice-ui=""
      {...handlers}
      aria-label={label}
      title={label}
      className={`inline-flex h-[var(--control)] touch-none items-center gap-2 rounded-full border px-2.5 select-none sm:pr-1.5 ${
        live ? 'border-acc bg-acc text-on-acc' : 'border-line bg-panel text-ink hover:border-acc'
      }`}
    >
      {voice.phase === 'thinking' ? (
        <span className="size-3.5 animate-spin rounded-full border-2 border-line border-t-acc" aria-hidden="true" />
      ) : (
        <Mic size={16} strokeWidth={1.5} aria-hidden="true" />
      )}
      <span className="max-sm:hidden">{t('voice.talk')}</span>
      {!live && (
        <span className="max-sm:hidden">
          <kbd>Ctrl Space</kbd>
        </span>
      )}
    </button>
  );
}

/** Prototype 03: a large round button within thumb reach, on every screen of the workspace. */
function ThumbButton() {
  const voice = useVoice()!;
  const handlers = usePushToTalk(voice);
  const label = useLabel(voice.phase);
  const live = voice.phase === 'listening';
  return (
    <button
      type="button"
      data-voice-ui=""
      {...handlers}
      aria-label={label}
      title={label}
      className={`fixed right-4 bottom-[max(1rem,env(safe-area-inset-bottom))] z-30 grid size-16 touch-none place-items-center rounded-full shadow-float select-none ${
        live ? 'scale-110 bg-acc text-on-acc ring-4 ring-sel' : 'bg-acc text-on-acc hover:brightness-110'
      } transition-transform duration-[var(--dur-1)]`}
    >
      {voice.phase === 'thinking' ? (
        <span className="size-6 animate-spin rounded-full border-2 border-on-acc border-t-transparent" aria-hidden="true" />
      ) : (
        <Mic size={28} strokeWidth={1.75} aria-hidden="true" />
      )}
    </button>
  );
}
