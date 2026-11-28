import { useMutation, useQuery, useQueryClient, type Query } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useUi } from '../app/UiContext';
import { useToast } from '../components/Toasts';
import { isIssueList } from './issues';
import type { IssueState } from './gen/types.gen';
import {
  deleteTimeEntryMutation,
  getIssueQueryKey,
  getIssueTimeOptions,
  getWorkspaceOptions,
  getWorkspaceQueryKey,
  listIssueActivityQueryKey,
  logTimeMutation,
  updateTimeEntryMutation,
} from './gen/@tanstack/react-query.gen';

/** Done or Canceled: time on it is locked (ADR-021). */
export const closed = (state: IssueState) => state === 'done' || state === 'canceled';

/** Time registration is on for the workspace, and the caller can log time: guests have no entries (ADR-021). */
export function useTimeRegistration(workspace: string) {
  const { data: info } = useQuery(getWorkspaceOptions({ path: { workspace } }));
  return {
    on: !!info?.timeRegistration,
    canLog: !!info?.timeRegistration && info.role !== 'guest',
    owner: info?.role === 'owner',
    loaded: !!info,
  };
}

export function useIssueTime(workspace: string, key: string, enabled = true) {
  return useQuery({ ...getIssueTimeOptions({ path: { workspace, key } }), enabled });
}

const timeQueries = new Set(['listMyTime', 'getIssueTime', 'listTimeEntries', 'listTimeChanges']);
const isTimeQuery = (q: Query) => timeQueries.has((q.queryKey[0] as { _id?: string } | undefined)?._id ?? '');

/** Problem codes the time endpoints answer with (task 034), each with its own message. */
const knownErrors = new Set(['time_locked', 'day_full', 'future_date', 'not_your_entry', 'time_entry_not_found', 'time_registration_off']);

/** The message for a failed time write: the API's code when we know it, else a generic one. */
export function useTimeError() {
  const { t } = useTranslation();
  return (error: unknown): string => {
    const code = (error as { code?: string } | null)?.code;
    return code && knownErrors.has(code) ? t(`time.errors.${code}` as 'time.errors.failed') : t('time.errors.failed');
  };
}

/** Log, edit and delete: every write refreshes the timesheet, the issue's time and, when it closed one, the issue. */
export function useTimeWrites(workspace: string) {
  const qc = useQueryClient();
  const toast = useToast();
  const message = useTimeError();
  const refresh = () => void qc.invalidateQueries({ predicate: isTimeQuery });
  const onError = (error: unknown) => toast.error(message(error));
  const log = useMutation({
    ...logTimeMutation(),
    onSuccess: (_entry, vars) => {
      if (!vars.body.state) return;
      void qc.invalidateQueries({ predicate: isIssueList });
      void qc.invalidateQueries({ queryKey: getIssueQueryKey({ path: { workspace, key: vars.body.issue } }) });
      void qc.invalidateQueries({ queryKey: listIssueActivityQueryKey({ path: { workspace, key: vars.body.issue } }) });
    },
    onSettled: refresh,
  });
  const update = useMutation({ ...updateTimeEntryMutation(), onError, onSettled: refresh });
  const remove = useMutation({ ...deleteTimeEntryMutation(), onError, onSettled: refresh });
  return { log, update, remove };
}

/**
 * Setting Done or Canceled asks for time in the same save when the person has logged none on the issue (ADR-021
 * Locking); otherwise, and whenever time registration is off, `proceed` runs straight away.
 */
export function useCloseWithTime(workspace: string) {
  const qc = useQueryClient();
  const ui = useUi();
  return async (key: string, state: IssueState, proceed: () => void, onSaved?: () => void) => {
    if (!closed(state)) return proceed();
    try {
      // Fetched, not read from the hook: a close before the workspace has loaded must still ask.
      const info = await qc.fetchQuery({ ...getWorkspaceOptions({ path: { workspace } }), queryKey: getWorkspaceQueryKey({ path: { workspace } }), staleTime: 60_000 });
      if (!info.timeRegistration || info.role === 'guest') return proceed();
      const mine = await qc.fetchQuery({ ...getIssueTimeOptions({ path: { workspace, key } }), staleTime: 10_000 });
      if (mine.myMinutes > 0 || mine.locked) return proceed();
    } catch {
      return proceed();
    }
    ui.openLogTime({ issue: key, state: state as 'done' | 'canceled', onSkip: proceed, onSaved });
  };
}
