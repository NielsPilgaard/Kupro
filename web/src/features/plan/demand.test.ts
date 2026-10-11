import { describe, expect, it } from 'vitest';
import { lanes, monthsBetween, monthsFor, timeline } from './demand';

// Same cases as PlanTests on the server: the band must agree with GET /plan.
describe('plan demand', () => {
  const months = ['2027-01-01', '2027-02-01'];

  it('spreads effort evenly over the card days', () => {
    const result = monthsFor(months, [{ countedEffort: 59, startDate: '2027-01-01', dueDate: '2027-02-28' }], [], 37);
    expect(result.map((m) => m.demandHours)).toEqual([31, 28]);
  });

  it('turns hours into people and follows the capacity steps', () => {
    const result = monthsFor(
      months,
      [{ countedEffort: 740, startDate: '2027-01-01', dueDate: '2027-01-31' }],
      [{ fromMonth: '2027-01-01', people: 2 }, { fromMonth: '2027-02-01', people: 3 }],
      37,
    );
    expect(result[0]).toMatchObject({ supplyHours: 327.71, demandPeople: 4.52, supplyPeople: 2, gapPeople: 2.52 });
    expect(result[1]!.supplyPeople).toBe(3);
  });

  it('lists months inclusively across a year end', () => {
    expect(monthsBetween('2026-11-01', '2027-02-01')).toEqual(['2026-11-01', '2026-12-01', '2027-01-01', '2027-02-01']);
  });

  it('maps dates to the month grid and back', () => {
    const t = timeline(months, 120);
    expect(t.x('2027-02-01')).toBe(120);
    expect(t.dateAt(t.x('2027-01-16') + 1)).toBe('2027-01-16');
  });

  it('packs overlapping cards into separate lanes', () => {
    const { lane, count } = lanes([
      { key: 'A', startDate: '2027-01-01', dueDate: '2027-01-31' },
      { key: 'B', startDate: '2027-01-10', dueDate: '2027-02-10' },
      { key: 'C', startDate: '2027-02-05', dueDate: '2027-02-20' },
    ]);
    expect([lane.get('A'), lane.get('B'), lane.get('C')]).toEqual([0, 1, 0]);
    expect(count).toBe(2);
  });
});
