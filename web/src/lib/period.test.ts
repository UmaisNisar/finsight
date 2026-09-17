import { describe, expect, it } from 'vitest';
import { isPreset, isSingleMonth, localIsoDate, periodLabel, periodQuery, trailingMonths } from './period';

describe('periods', () => {
  it('builds query strings for presets and custom ranges', () => {
    expect(periodQuery({ preset: 'last-3-months' })).toBe('period=last-3-months');
    expect(periodQuery({ preset: 'custom', from: '2026-01-01', to: '2026-03-31' })).toBe('period=custom&from=2026-01-01&to=2026-03-31');
    // A custom preset missing its range can't send partial dates.
    expect(periodQuery({ preset: 'custom', from: '2026-01-01' })).toBe('period=custom');
  });

  it('recognises only known presets', () => {
    expect(isPreset('last-12-months')).toBe(true);
    expect(isPreset('custom')).toBe(true);
    expect(isPreset('last-2-weeks')).toBe(false);
    expect(isPreset(null)).toBe(false);
  });

  it('labels presets, and custom ranges with the resolved label when known', () => {
    expect(periodLabel({ preset: 'this-month' })).toBe('This month');
    expect(periodLabel({ preset: 'custom', from: '2026-03-01', to: '2026-08-31' })).toBe('Custom range');
    expect(periodLabel({ preset: 'custom' }, 'Mar 1 – Aug 31, 2026')).toBe('Mar 1 – Aug 31, 2026');
  });

  it('computes the trailing complete months ending with a period, across year boundaries', () => {
    expect(trailingMonths('2026-08-31')).toEqual({ preset: 'custom', from: '2026-03-01', to: '2026-08-31' });
    expect(trailingMonths('2026-02-28', 3)).toEqual({ preset: 'custom', from: '2025-12-01', to: '2026-02-28' });
    expect(trailingMonths('2024-02-10', 12)).toEqual({ preset: 'custom', from: '2023-03-01', to: '2024-02-29' });
  });

  it('recognises single-month periods', () => {
    expect(isSingleMonth('2026-08-01', '2026-08-31')).toBe(true);
    expect(isSingleMonth('2026-06-01', '2026-08-31')).toBe(false);
    expect(isSingleMonth('2026-08-05', '2026-08-31')).toBe(false);
  });

  it('formats local dates without shifting to UTC', () => {
    // 11:30pm local on Sep 16 is still Sep 16, whatever the time zone.
    expect(localIsoDate(new Date(2026, 8, 16, 23, 30))).toBe('2026-09-16');
    expect(localIsoDate(new Date(2026, 0, 1, 0, 5))).toBe('2026-01-01');
  });
});
