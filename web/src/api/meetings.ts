import {
  useMutation,
  useQuery,
  useQueryClient,
  type Query,
} from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useToast } from "../components/Toasts";
import {
  addMeetingAudioMutation,
  addMeetingTranscriptMutation,
  getMeetingOptions,
  getMeetingSettingsOptions,
  listMeetingProposalsOptions,
  reviewMeetingProposalsMutation,
  startMeetingMutation,
  undoMeetingMutation,
  undoMeetingProposalMutation,
  updateMeetingMutation,
  updateMeetingSettingsMutation,
} from "./gen/@tanstack/react-query.gen";
import type { ProposalReview } from "./gen/types.gen";
import { isIssueList } from "./issues";

const meetingOps = [
  "listMeetings",
  "getMeeting",
  "getMeetingTranscript",
  "listMeetingProposals",
  "getMeetingSettings",
];
const opOf = (q: Query) =>
  (q.queryKey[0] as { _id?: string } | undefined)?._id ?? "";

/** Meetings, proposals and settings; also what realtime `meetings` events refetch. */
export const isMeetingQuery = (q: Query) => meetingOps.includes(opOf(q));

/** Applying or undoing changes issues too, so lists, details and feeds refetch with the meeting (task 015). */
function useRefresh() {
  const qc = useQueryClient();
  return () => {
    void qc.invalidateQueries({
      predicate: (q) =>
        isMeetingQuery(q) ||
        isIssueList(q) ||
        ["getIssue", "listIssueActivity"].includes(opOf(q)),
    });
  };
}

function useFailed() {
  const toast = useToast();
  const { t } = useTranslation();
  return (error: unknown) =>
    toast.error(
      (error as { detail?: string } | null)?.detail ?? t("meeting.failed"),
    );
}

export const useMeeting = (workspace: string, id: string) =>
  useQuery(getMeetingOptions({ path: { workspace, id } }));

export const useProposals = (workspace: string, id: string) =>
  useQuery(listMeetingProposalsOptions({ path: { workspace, id } }));

export const useMeetingSettings = (workspace: string) =>
  useQuery(getMeetingSettingsOptions({ path: { workspace } }));

export function useStartMeeting() {
  return useMutation({ ...startMeetingMutation(), onError: useFailed() });
}

export function useUpdateMeeting(workspace: string, id: string) {
  const refresh = useRefresh();
  const m = useMutation({
    ...updateMeetingMutation(),
    onSuccess: refresh,
    onError: useFailed(),
  });
  return (body: Parameters<typeof m.mutate>[0]["body"]) =>
    m.mutate({ path: { workspace, id }, body });
}

export function useSay() {
  const refresh = useRefresh();
  return useMutation({
    ...addMeetingTranscriptMutation(),
    onSuccess: refresh,
    onError: useFailed(),
  });
}

/** One utterance of browser audio (WAV); the server transcribes it and proposes like for text. */
export function useSendAudio(workspace: string, id: string) {
  const refresh = useRefresh();
  const m = useMutation({
    ...addMeetingAudioMutation(),
    onSuccess: refresh,
    onError: useFailed(),
  });
  // The endpoint reads the raw request body (browser-only, not in the generated types), so the Blob goes as-is.
  return (wav: Blob) =>
    m.mutate({
      path: { workspace, id },
      body: wav,
      headers: { "Content-Type": "audio/wav" },
      bodySerializer: null,
    } as unknown as Parameters<typeof m.mutate>[0]);
}

export function useReview(workspace: string, id: string) {
  const refresh = useRefresh();
  const failed = useFailed();
  const m = useMutation({
    ...reviewMeetingProposalsMutation(),
    onSuccess: (results) => {
      refresh();
      const error = results.find((r) => r.error)?.error;
      if (error) failed(error);
    },
    onError: failed,
  });
  return {
    review: (items: ProposalReview[]) =>
      m.mutate({ path: { workspace, id }, body: { items } }),
    pending: m.isPending,
  };
}

export function useUndo(workspace: string, id: string) {
  const refresh = useRefresh();
  const failed = useFailed();
  const one = useMutation({
    ...undoMeetingProposalMutation(),
    onSuccess: refresh,
    onError: failed,
  });
  const all = useMutation({
    ...undoMeetingMutation(),
    onSuccess: refresh,
    onError: failed,
  });
  return {
    undo: (proposalId: string) =>
      one.mutate({ path: { workspace, id, proposalId } }),
    undoAll: () => all.mutate({ path: { workspace, id } }),
  };
}

export function useUpdateMeetingSettings(workspace: string) {
  const refresh = useRefresh();
  const m = useMutation({
    ...updateMeetingSettingsMutation(),
    onSuccess: refresh,
    onError: useFailed(),
  });
  return (body: Parameters<typeof m.mutate>[0]["body"]) =>
    m.mutate({ path: { workspace }, body });
}
