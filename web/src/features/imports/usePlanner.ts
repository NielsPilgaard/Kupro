import { useCallback, useEffect, useRef, useState } from 'react';
import type { ImportMapping, ImportPlan } from '../../api/gen/types.gen';
import { getImportRun, planImport, setImportMapping } from '../../api/gen/sdk.gen';

export interface PlannerState {
  plan?: ImportPlan;
  /** The plan before the last edit, for the rail's change column. */
  previous?: ImportPlan;
  planning: boolean;
  error?: string;
  savedAt?: number;
}

const POLL_MS = 1000;
const DEBOUNCE_MS = 600;
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

/**
 * The live dry run behind the wizard's rail: each edit saves the mapping as a draft, then plans over the stored
 * snapshot (ADR-019 Pipeline). Edits during a run queue up; only the newest mapping is saved and planned next.
 */
export function usePlanner(workspace: string, importId: string | undefined) {
  const [state, setState] = useState<PlannerState>({ planning: false });
  const pending = useRef<{ mapping: ImportMapping | null } | null>(null);
  const busy = useRef(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const alive = useRef(true);

  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
      clearTimeout(timer.current);
    };
  }, []);

  const loop = useCallback(async () => {
    if (busy.current || !importId) return;
    busy.current = true;
    const path = { workspace, id: importId };
    try {
      while (pending.current && alive.current) {
        const { mapping } = pending.current;
        pending.current = null;
        setState((s) => ({ ...s, planning: true, error: undefined }));
        if (mapping) {
          await setImportMapping({ path, body: mapping, throwOnError: true });
          setState((s) => ({ ...s, savedAt: Date.now() }));
        }

        // Runs queue behind each other, so a refusal (no mapping, import closed) is final: show it, don't retry.
        const queued = await planImport({ path });
        if (!queued.data) throw new Error((queued.error as { detail?: string } | undefined)?.detail ?? `HTTP ${queued.response?.status}`);

        let detail = (await getImportRun({ path: { ...path, runId: queued.data.id }, throwOnError: true })).data;
        while ((detail.run.state === 'queued' || detail.run.state === 'running') && alive.current) {
          await sleep(POLL_MS);
          detail = (await getImportRun({ path: { ...path, runId: queued.data.id }, throwOnError: true })).data;
        }

        const plan = detail.plan ?? undefined;
        setState((s) => ({
          ...s,
          plan: plan ?? s.plan,
          previous: plan ? s.plan : s.previous,
          planning: !!pending.current,
          error: detail.run.state === 'failed' ? (detail.run.error ?? 'failed') : undefined,
        }));
      }
    } catch (e) {
      setState((s) => ({ ...s, planning: false, error: e instanceof Error ? e.message : String((e as { detail?: string })?.detail ?? e) }));
    } finally {
      busy.current = false;
      // An edit that arrived while the last request failed still deserves a plan.
      if (pending.current && alive.current) timer.current = setTimeout(() => void loop(), DEBOUNCE_MS);
    }
  }, [workspace, importId]);

  /** Save this mapping (or just re-plan with null) after a short pause; the newest call wins. */
  const request = useCallback(
    (mapping: ImportMapping | null, delay = DEBOUNCE_MS) => {
      const keep = pending.current?.mapping;
      pending.current = { mapping: mapping ?? keep ?? null };
      clearTimeout(timer.current);
      timer.current = setTimeout(() => void loop(), delay);
    },
    [loop],
  );

  return { ...state, request };
}
