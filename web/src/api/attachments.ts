import { useQuery } from '@tanstack/react-query';
import { useCallback, type ClipboardEvent, type DragEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useToast } from '../components/Toasts';
import { getAttachmentOptions } from './gen/@tanstack/react-query.gen';
import { completeAttachment, createAttachment } from './gen/sdk.gen';

/** `attachment:ID` in markdown (task 008); the API turns it into a short-lived download URL. */
export const ATTACHMENT_SCHEME = 'attachment:';

/**
 * Upload one file: create (presigned URL), PUT straight to storage, complete. Returns the markdown reference.
 * The PUT uses plain fetch: the presigned URL is the credential, and the API's bearer token must not leak to storage.
 */
export async function uploadAttachment(workspace: string, file: File, issue?: string): Promise<string> {
  const contentType = file.type || 'application/octet-stream';
  const { data: upload } = await createAttachment({
    path: { workspace },
    body: { fileName: file.name || 'pasted.png', contentType, size: file.size, issue },
    throwOnError: true,
  });
  const put = await fetch(upload.uploadUrl, { method: 'PUT', body: file, headers: { 'Content-Type': contentType } });
  if (!put.ok) throw new Error(`storage ${put.status}`);
  const { data: done } = await completeAttachment({ path: { workspace, id: upload.attachment.id }, throwOnError: true });
  return done.markdown;
}

/** Download URL for an `attachment:ID`; refreshed before the 5-minute presign runs out. */
export function useAttachment(workspace: string, id: string | null) {
  return useQuery({
    ...getAttachmentOptions({ path: { workspace, id: id ?? '' } }),
    enabled: !!id,
    staleTime: 4 * 60_000,
    refetchInterval: 4 * 60_000,
  });
}

/**
 * Paste or drop files into a markdown textarea: each file uploads and its markdown replaces a placeholder at the
 * cursor. `setValue` receives an updater so concurrent uploads don't overwrite each other.
 */
export function useMarkdownUpload(workspace: string, issue: string | undefined, setValue: (update: (v: string) => string) => void) {
  const { t } = useTranslation();
  const toast = useToast();

  const insert = useCallback(
    (target: HTMLTextAreaElement, files: File[]) => {
      for (const file of files) {
        const placeholder = `![${t('upload.uploading', { name: file.name || 'image' })}]()`;
        const at = target.selectionStart;
        setValue((v) => `${v.slice(0, at)}${placeholder}\n${v.slice(at)}`);
        uploadAttachment(workspace, file, issue)
          .then((markdown) => setValue((v) => v.replace(placeholder, markdown)))
          .catch(() => {
            setValue((v) => v.replace(`${placeholder}\n`, ''));
            toast.error(t('upload.failed', { name: file.name }));
          });
      }
    },
    [workspace, issue, setValue, t, toast],
  );

  return {
    onPaste: (e: ClipboardEvent<HTMLTextAreaElement>) => {
      const files = Array.from(e.clipboardData.files);
      if (files.length === 0) return;
      e.preventDefault();
      insert(e.currentTarget, files);
    },
    onDrop: (e: DragEvent<HTMLTextAreaElement>) => {
      const files = Array.from(e.dataTransfer.files);
      if (files.length === 0) return;
      e.preventDefault();
      insert(e.currentTarget, files);
    },
  };
}
