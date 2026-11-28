import type { QueryClient } from '@tanstack/react-query';
import { addComment, createIssue, deleteComment, getIssue, interpretVoice, interpretVoiceAudio, transcribeVoice, updateIssue } from './gen/sdk.gen';
import type { IssueDetail, UpdateIssueRequest, VoiceInterpretation, VoiceOperation, VoiceTranscription } from './gen/types.gen';
import { isIssueList } from './issues';

/** Changes applied from voice say so in the activity feed: "via voice" (task 036). */
const viaVoice = { 'X-Kupro-Client': 'voice' };

/** The screen an utterance was spoken on: what "this one" means, and where boards and new issues go. */
export interface VoiceScreen {
  issue?: string;
  project?: string;
  language: 'en' | 'da';
}

/** An API refusal with its stable code (Problem Details). */
export class VoiceError extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

function fail(error: unknown): never {
  const problem = error as { code?: string; detail?: string } | null;
  throw new VoiceError(problem?.code ?? 'failed', problem?.detail ?? '');
}

// The audio endpoints read the raw request body (browser-only, not in the generated types), so the Blob goes as-is.
const wavBody = (wav: Blob) => ({ body: wav, bodySerializer: null, headers: { 'Content-Type': 'audio/wav' } }) as unknown as object;

export async function interpretAudio(workspace: string, screen: VoiceScreen, wav: Blob): Promise<VoiceInterpretation> {
  const { data, error } = await interpretVoiceAudio({ path: { workspace }, query: screen, ...wavBody(wav) });
  return data ?? fail(error);
}

export async function interpretText(workspace: string, screen: VoiceScreen, text: string): Promise<VoiceInterpretation> {
  const { data, error } = await interpretVoice({ path: { workspace }, body: { text, ...screen } });
  return data ?? fail(error);
}

export async function transcribe(workspace: string, language: string, wav: Blob): Promise<VoiceTranscription> {
  const { data, error } = await transcribeVoice({ path: { workspace }, query: { language, tidy: true }, ...wavBody(wav) });
  return data ?? fail(error);
}

/** Puts a field back as it was before a change, for an issue picked from the candidates (the server had no diff for it). */
export function undoFrom(issue: IssueDetail, op: VoiceOperation): UpdateIssueRequest | null {
  switch (op.operation) {
    case 'state':
      return { state: issue.state };
    case 'sprint':
      return issue.sprintId ? { sprint: issue.sprintId } : { clear: ['sprint'] };
    case 'assignee':
      return issue.assignee ? { assignee: issue.assignee.id } : { clear: ['assignee'] };
    case 'estimate':
      return issue.effort != null ? { effort: issue.effort } : { clear: ['effort'] };
    default:
      return null;
  }
}

/**
 * Applies one proposed change through the issue endpoints, as the person, and returns what undoes it. `key` picks the
 * issue when the proposal had candidates.
 */
export async function applyOperation(workspace: string, op: VoiceOperation, key = op.issue?.key): Promise<() => Promise<void>> {
  const headers = viaVoice;
  if (op.operation === 'createIssue') {
    const { data, error } = await createIssue({ path: { workspace }, body: op.create!, headers });
    if (!data) fail(error);
    return async () => {
      await updateIssue({ path: { workspace, key: data.key }, body: { state: 'canceled' }, headers, throwOnError: true });
    };
  }
  if (!key) throw new VoiceError('issue_required', '');
  if (op.operation === 'comment') {
    const { data, error } = await addComment({ path: { workspace, key }, body: { body: op.value }, headers });
    if (!data) fail(error);
    return async () => {
      await deleteComment({ path: { workspace, id: data.id }, headers, throwOnError: true });
    };
  }
  let undo = key === op.issue?.key ? op.undo : null;
  if (!undo) {
    const { data } = await getIssue({ path: { workspace, key } });
    undo = data ? undoFrom(data, op) : null;
  }
  const { error } = await updateIssue({ path: { workspace, key }, body: op.update!, headers });
  if (error) fail(error);
  return async () => {
    if (undo) await updateIssue({ path: { workspace, key }, body: undo, headers, throwOnError: true });
  };
}

/** Lists, details and feeds show the change. */
export function refreshIssues(qc: QueryClient) {
  const op = (q: { queryKey: readonly unknown[] }) => (q.queryKey[0] as { _id?: string } | undefined)?._id ?? '';
  void qc.invalidateQueries({ predicate: (q) => isIssueList(q) || ['getIssue', 'listIssueActivity'].includes(op(q)) });
}
