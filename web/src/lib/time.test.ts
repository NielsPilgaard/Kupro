import { describe, expect, it } from 'vitest';
import { addDays, isoWeek, mondayOf, parseDuration, today } from './time';

describe('parseDuration', () => {
  it.each([
    ['1h30m', 90],
    ['1h30', 90],
    ['1t30m', 90],
    ['90m', 90],
    ['1.5h', 90],
    ['1,5', 90],
    ['2', 120],
    ['1:30', 90],
    ['45min', 45],
    [' 2h ', 120],
    ['', 0],
  ])('%s is %i minutes', (text, minutes) => expect(parseDuration(text)).toBe(minutes));

  it.each(['abc', '1:75', 'h', '-1h'])('%s is not a duration', (text) => expect(parseDuration(text)).toBeNaN());
});

describe('dates', () => {
  it('finds the Monday of a week', () => {
    expect(mondayOf('2026-11-26')).toBe('2026-11-23');
    expect(mondayOf('2026-11-23')).toBe('2026-11-23');
    expect(mondayOf('2026-11-29')).toBe('2026-11-23');
  });

  it('adds days across months', () => expect(addDays('2026-11-30', 1)).toBe('2026-12-01'));

  it('numbers ISO weeks', () => {
    expect(isoWeek('2026-11-26')).toBe(48);
    expect(isoWeek('2027-01-01')).toBe(53);
    expect(isoWeek('2027-01-04')).toBe(1);
  });

  it('takes today in Copenhagen', () => expect(today(new Date('2026-11-26T23:30:00Z'))).toBe('2026-11-27'));
});
