import { describe, expect, it } from 'vitest';
import type { Anomaly } from '@/api/schemas';
import { accountLabel, formatDate, formatMoney, formatMoneyCompact, formatPercent, formatRelativeTime, formatShortDate, greeting } from './format';
import { anomalyReason, FREQUENCY_LABEL } from './labels';

describe('formatMoney', () => {
  it('formats with a narrow currency symbol and whole units for headlines', () => {
    expect(formatMoney(6200, 'CAD', { whole: true })).toMatch(/^\$6,200$/);
    expect(formatMoney(-22.99, 'USD')).toMatch(/^−\$22\.99$/);
    expect(formatMoney(1540.5, 'AUD')).toMatch(/^\$1,540\.50$/);
    expect(formatMoney(85_000, 'PKR', { whole: true })).toMatch(/^Rs\s?85,000$/);
  });

  it('shows an explicit plus sign for money in when asked, and no sign for zero', () => {
    expect(formatMoney(2650, 'GBP', { signed: true })).toMatch(/^\+£2,650\.00$/);
    expect(formatMoney(0, 'GBP', { signed: true })).toMatch(/^£0\.00$/);
  });

  it('abbreviates axis values', () => {
    expect(formatMoneyCompact(12_900, 'CAD')).toMatch(/^\$12\.9K$/);
    expect(formatMoneyCompact(0, 'CAD')).toMatch(/^\$0$/);
  });
});

describe('formatPercent', () => {
  it('trims trailing zero decimals and keeps one decimal otherwise', () => {
    expect(formatPercent(32.6)).toBe('32.6%');
    expect(formatPercent(34.0)).toBe('34%');
    expect(formatPercent(-8, { signed: true })).toBe('−8%');
    expect(formatPercent(12.4, { signed: true, digits: 0 })).toBe('+12%');
  });
});

describe('dates', () => {
  it('honours the date format chosen in settings', () => {
    expect(formatDate('2026-09-12', 'yyyy-MM-dd')).toBe('2026-09-12');
    expect(formatDate('2026-09-12', 'dd/MM/yyyy')).toBe('12/09/2026');
    expect(formatDate('2026-09-12', 'MM/dd/yyyy')).toBe('09/12/2026');
    expect(formatDate('2026-09-12T18:00:00Z', 'yyyy-MM-dd')).toBe('2026-09-12');
  });

  it('keeps day-first order for day-first formats in compact dates', () => {
    expect(formatShortDate('2026-09-02', 'dd/MM/yyyy')).toMatch(/^2 \S+$/);
    expect(formatShortDate('2026-09-02', 'MM/dd/yyyy')).toMatch(/^\S+ 2$/);
  });

  it('describes times relative to now', () => {
    const now = Date.parse('2026-09-16T12:00:00Z');
    expect(formatRelativeTime('2026-09-16T11:55:00Z', now)).toBe('5 minutes ago');
    expect(formatRelativeTime('2026-09-14T12:00:00Z', now)).toBe('2 days ago');
  });

  it('greets by time of day', () => {
    expect(greeting(new Date(2026, 8, 16, 9))).toBe('Good morning');
    expect(greeting(new Date(2026, 8, 16, 14))).toBe('Good afternoon');
    expect(greeting(new Date(2026, 8, 16, 2))).toBe('Good evening');
  });
});

describe('accountLabel', () => {
  it('only ever shows the last four digits', () => {
    expect(accountLabel('TD Bank', '4821')).toBe('TD Bank ••4821');
    expect(accountLabel(null, '4821')).toBe('Account ••4821');
    expect(accountLabel(null, null)).toBe('Account');
  });
});

describe('labels', () => {
  const anomaly: Anomaly = { transactionId: 't', date: '2026-08-02', merchant: 'Apple', categoryId: 'shopping.electronics', categoryName: 'Electronics', amount: 1299, kind: 'unusuallyLarge', typicalAmount: 120 };

  it('explains why a transaction was flagged', () => {
    expect(anomalyReason(anomaly, 'CAD')).toMatch(/^Usually about \$120$/);
    expect(anomalyReason({ ...anomaly, typicalAmount: null }, 'CAD')).toBe('Larger than usual');
    expect(anomalyReason({ ...anomaly, kind: 'possibleDuplicate' }, 'CAD')).toBe('Possible duplicate charge');
  });

  it('names every recurring frequency', () => {
    expect(FREQUENCY_LABEL.biweekly).toBe('Every 2 weeks');
    expect(Object.keys(FREQUENCY_LABEL)).toHaveLength(5);
  });
});
