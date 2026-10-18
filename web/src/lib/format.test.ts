import { describe, expect, it } from 'vitest';
import { keyOf, slugOf } from './format';

describe('names to ids', () => {
  it('keeps Danish letters readable in workspace slugs', () => {
    expect(slugOf('Æblegården ApS')).toBe('aeblegaarden-aps');
    expect(slugOf('  Café Øst!  ')).toBe('cafe-oest');
  });

  it('keeps Danish letters readable in project keys', () => {
    expect(keyOf('Ærø Webshop')).toBe('AERO');
    expect(keyOf('2026 Roadmap')).toBe('ROAD');
  });
});
