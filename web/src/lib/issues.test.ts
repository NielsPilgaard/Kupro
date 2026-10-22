import { describe, expect, it } from 'vitest';
import { cliFor, compareRank, projectKeyOf } from './issues';

describe('issues helpers', () => {
  it('orders by rank, then by key number', () => {
    const items = [
      { key: 'KUP-10', rank: 2 },
      { key: 'KUP-2', rank: 1 },
      { key: 'KUP-9', rank: 2 },
    ];
    expect(items.sort(compareRank).map((i) => i.key)).toEqual(['KUP-2', 'KUP-9', 'KUP-10']);
  });

  it('shows the CLI command with dashed enum values', () => {
    expect(cliFor('state', 'KUP-3', 'inProgress')).toBe('kupro issue update KUP-3 --state in-progress');
    expect(cliFor('close', 'KUP-3')).toBe('kupro issue close KUP-3');
  });

  it('extracts the project key from multi-dash keys', () => {
    expect(projectKeyOf('KUP-12')).toBe('KUP');
  });
});
