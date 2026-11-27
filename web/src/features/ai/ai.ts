import { useQueryClient, type Query } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import type { AiCapability } from '../../api/gen/types.gen';

/** Every capability, in the order the page shows them (ADR-022 decision 2). */
export const capabilities: AiCapability[] = ['transcription', 'meetingProposals', 'voiceCommands'];

// Routes decide whether the microphone shows, so meetings refetch with the AI status.
const aiOps = ['getAiStatus', 'getMeetingSettings', 'getMeeting', 'listMeetings'];
const opOf = (q: Query) => (q.queryKey[0] as { _id?: string } | undefined)?._id ?? '';

/** Refetches the AI status and what depends on it after a connection or route changes. */
export function useRefreshAi() {
  const qc = useQueryClient();
  return () => void qc.invalidateQueries({ predicate: (q) => aiOps.includes(opOf(q)) });
}

/** The `code` of a problem-details error from the API. */
export const problemCode = (error: unknown) => (error as { code?: string } | null)?.code;

const known = [
  'key_required',
  'key_storage_not_configured',
  'endpoint_not_https',
  'endpoint_not_public',
  'endpoint_not_found',
  'eu_confirmation_required',
  'model_required',
  'capability_unsupported',
  'ai_connection_not_found',
] as const;
type Known = (typeof known)[number];

/** What went wrong and what to do, for an error the AI endpoints answer with. */
export function errorText(t: TFunction, error: unknown) {
  const code = problemCode(error);
  return known.includes(code as Known) ? t(`ai.errors.${code as Known}`) : t('ai.errors.failed');
}
