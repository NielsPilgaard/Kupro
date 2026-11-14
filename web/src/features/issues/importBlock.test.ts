import { describe, expect, it } from 'vitest';
import { splitImportBlock } from './importBlock';

describe('splitImportBlock', () => {
  it('takes the import block off the end of a description', () => {
    const { body, block } = splitImportBlock('Hello\n\n<details>\n<summary>Imported from Jira</summary>\n\n| Field | Value |\n\n</details>');
    expect(body).toBe('Hello');
    expect(block).toEqual({ summary: 'Imported from Jira', content: '| Field | Value |\n\n' });
  });

  it('handles a description that is only the block', () => {
    const { body, block } = splitImportBlock('<details>\n<summary>Imported from CSV</summary>\n\n[Open the original](https://x)\n\n</details>');
    expect(body).toBe('');
    expect(block?.summary).toBe('Imported from CSV');
  });

  it('leaves any other text alone, including details written by hand', () => {
    for (const text of ['Plain', '<details>not ours</details>', 'a\n<details>\n<summary>x</summary>\n\nb\n\n</details>\nafter']) {
      expect(splitImportBlock(text)).toEqual({ body: text });
    }
  });
});
