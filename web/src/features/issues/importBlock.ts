const IMPORT_BLOCK = /\n*<details>\n<summary>([^\n<]*)<\/summary>\n\n([\s\S]*)<\/details>\s*$/;

/**
 * An import's collapsed block at the end of a description (ADR-019). Only this exact shape: raw HTML stays off, and the
 * inside goes back through Markdown, so imported text can't bring any markup of its own.
 */
export function splitImportBlock(markdown: string): { body: string; block?: { summary: string; content: string } } {
  const match = IMPORT_BLOCK.exec(markdown);
  if (!match) return { body: markdown };
  return { body: markdown.slice(0, match.index), block: { summary: match[1] ?? '', content: match[2] ?? '' } };
}
