import { describe, expect, it } from 'vitest';
import { cliFor, compareRank, projectKeyOf } from './issues';

describe('issues helpers', () => {
  it('orders by rank, then by key number', () => {
    const items = [
      { key: 'FLOW-10', rank: 2 },
      { key: 'FLOW-2', rank: 1 },
      { key: 'FLOW-9', rank: 2 },
    ];
    expect(items.sort(compareRank).map((i) => i.key)).toEqual(['FLOW-2', 'FLOW-9', 'FLOW-10']);
  });

  it('shows the CLI command with dashed enum values', () => {
    expect(cliFor('state', 'FLOW-3', 'inProgress')).toBe('kupro issue update FLOW-3 --state in-progress');
    expect(cliFor('close', 'FLOW-3')).toBe('kupro issue close FLOW-3');
  });

  it('extracts the project key from multi-dash keys', () => {
    expect(projectKeyOf('FLOW-12')).toBe('FLOW');
  });
});
