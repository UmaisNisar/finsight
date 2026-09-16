import { describe, expect, it } from 'vitest';
import { accountLabel, formatDate, formatMoney, formatPercent } from './format';
import { isSingleMonth, periodQuery, trailingMonths } from './period';

describe('formatMoney', () => {
  it('formats with a narrow currency symbol and whole units for headlines', () => {
    expect(formatMoney(6200, 'CAD', { whole: true })).toMatch(/^\$6,200$/);
    expect(formatMoney(-22.99, 'USD')).toMatch(/^−\$22\.99$/);
  });

  it('shows an explicit plus sign for money in when asked', () => {
    expect(formatMoney(2650, 'GBP', { signed: true })).toMatch(/^\+£2,650\.00$/);
  });
});

describe('formatPercent', () => {
  it('trims trailing zero decimals and keeps one decimal otherwise', () => {
    expect(formatPercent(32.6)).toBe('32.6%');
    expect(formatPercent(34.0)).toBe('34%');
    expect(formatPercent(-8, { signed: true })).toBe('−8%');
  });
});

describe('formatDate', () => {
  it('honours the date format chosen in settings', () => {
    expect(formatDate('2026-09-12', 'yyyy-MM-dd')).toBe('2026-09-12');
    expect(formatDate('2026-09-12', 'dd/MM/yyyy')).toBe('12/09/2026');
    expect(formatDate('2026-09-12', 'MM/dd/yyyy')).toBe('09/12/2026');
  });
});

describe('accountLabel', () => {
  it('only ever shows the last four digits', () => {
    expect(accountLabel('TD Bank', '4821')).toBe('TD Bank ••4821');
    expect(accountLabel(null, null)).toBe('Account');
  });
});

describe('periods', () => {
  it('builds query strings for presets and custom ranges', () => {
    expect(periodQuery({ preset: 'last-3-months' })).toBe('period=last-3-months');
    expect(periodQuery({ preset: 'custom', from: '2026-01-01', to: '2026-03-31' })).toBe('period=custom&from=2026-01-01&to=2026-03-31');
  });

  it('computes the trailing six complete months ending with a period', () => {
    expect(trailingMonths('2026-08-31')).toEqual({ preset: 'custom', from: '2026-03-01', to: '2026-08-31' });
    expect(trailingMonths('2026-02-28', 3)).toEqual({ preset: 'custom', from: '2025-12-01', to: '2026-02-28' });
  });

  it('recognises single-month periods', () => {
    expect(isSingleMonth('2026-08-01', '2026-08-31')).toBe(true);
    expect(isSingleMonth('2026-06-01', '2026-08-31')).toBe(false);
  });
});
