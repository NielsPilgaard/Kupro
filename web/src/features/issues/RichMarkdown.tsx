import { Paperclip } from 'lucide-react';
import Markdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { ATTACHMENT_SCHEME, useAttachment } from '../../api/attachments';

/** Markdown for descriptions and comments; `attachment:ID` images and links resolve through the API (members only). */
export function RichMarkdown({ workspace, children }: { workspace: string; children: string }) {
  return (
    <div className="prose-flowstate text-md">
      <Markdown
        remarkPlugins={[remarkGfm]}
        urlTransform={(url) => (url.startsWith(ATTACHMENT_SCHEME) ? url : defaultUrlTransform(url))}
        components={{
          img: ({ src, alt }) =>
            typeof src === 'string' && src.startsWith(ATTACHMENT_SCHEME) ? (
              <AttachmentImage workspace={workspace} id={src.slice(ATTACHMENT_SCHEME.length)} alt={alt ?? ''} />
            ) : (
              <img src={src} alt={alt ?? ''} />
            ),
          a: ({ href, children: label }) =>
            href?.startsWith(ATTACHMENT_SCHEME) ? (
              <AttachmentLink workspace={workspace} id={href.slice(ATTACHMENT_SCHEME.length)}>
                {label}
              </AttachmentLink>
            ) : (
              <a href={href} target="_blank" rel="noreferrer">
                {label}
              </a>
            ),
        }}
      >
        {children}
      </Markdown>
    </div>
  );
}

function AttachmentImage({ workspace, id, alt }: { workspace: string; id: string; alt: string }) {
  const { data } = useAttachment(workspace, id);
  if (!data?.downloadUrl) return <span className="inline-block h-24 w-40 rounded-r1 bg-sunk" aria-label={alt} />;
  return (
    <a href={data.downloadUrl} target="_blank" rel="noreferrer">
      <img src={data.downloadUrl} alt={alt} className="max-h-96 rounded-r1 border border-line" />
    </a>
  );
}

function AttachmentLink({ workspace, id, children }: { workspace: string; id: string; children: React.ReactNode }) {
  const { data } = useAttachment(workspace, id);
  return (
    <a href={data?.downloadUrl ?? undefined} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1">
      <Paperclip size={14} strokeWidth={1.5} aria-hidden="true" />
      {children}
    </a>
  );
}
