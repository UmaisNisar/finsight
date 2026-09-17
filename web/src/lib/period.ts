export type PeriodPreset = 'this-month' | 'last-month' | 'last-3-months' | 'last-6-months' | 'last-12-months' | 'custom';

export interface PeriodSelection {
  preset: PeriodPreset;
  from?: string;
  to?: string;
}

export const PERIOD_PRESETS: { value: Exclude<PeriodPreset, 'custom'>; label: string }[] = [
  { value: 'this-month', label: 'This month' },
  { value: 'last-month', label: 'Last month' },
  { value: 'last-3-months', label: 'Last 3 months' },
  { value: 'last-6-months', label: 'Last 6 months' },
  { value: 'last-12-months', label: 'Last 12 months' },
];

export const DEFAULT_PERIOD: PeriodSelection = { preset: 'last-month' };

/** URL parameters that describe the selected period, carried across navigation. */
export const PERIOD_PARAMS = ['period', 'from', 'to'] as const;

export function isPreset(value: string | null): value is PeriodPreset {
  return value !== null && (value === 'custom' || PERIOD_PRESETS.some((p) => p.value === value));
}

/** The query string for a period; also its cache key, so equal selections share cached data. */
export function periodQuery(period: PeriodSelection): string {
  const params = new URLSearchParams({ period: period.preset });
  if (period.preset === 'custom' && period.from && period.to) {
    params.set('from', period.from);
    params.set('to', period.to);
  }
  return params.toString();
}

/** The button label for a selection. Custom ranges use the label the server resolved, when known. */
export function periodLabel(period: PeriodSelection, resolvedLabel?: string): string {
  if (period.preset === 'custom') {
    return resolvedLabel ?? 'Custom range';
  }
  return PERIOD_PRESETS.find((p) => p.value === period.preset)?.label ?? 'Last month';
}

/** Six complete months ending with the month that contains `end`: the cash-flow trend window. */
export function trailingMonths(end: string, months = 6): PeriodSelection {
  const [year, month] = end.split('-').map(Number) as [number, number];
  const endDate = new Date(Date.UTC(year, month, 0));
  const startDate = new Date(Date.UTC(year, month - months, 1));
  return { preset: 'custom', from: utcIsoDate(startDate), to: utcIsoDate(endDate) };
}

function utcIsoDate(date: Date): string {
  return date.toISOString().slice(0, 10);
}

/** Local calendar date as yyyy-MM-dd (not UTC, so "today" is the user's today). */
export function localIsoDate(date: Date): string {
  const mm = String(date.getMonth() + 1).padStart(2, '0');
  const dd = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${mm}-${dd}`;
}

/** True when a period covers exactly one calendar month. */
export function isSingleMonth(start: string, end: string): boolean {
  return start.slice(0, 7) === end.slice(0, 7) && start.endsWith('-01');
}
