/** Formatting follows the user's currency and date-format settings; locale comes from the browser. */

export interface FormatPreferences {
  currency: string;
  dateFormat: string;
}

const currencyFormatters = new Map<string, Intl.NumberFormat>();

function currencyFormatter(currency: string, fractionDigits: number): Intl.NumberFormat {
  const key = `${currency}:${fractionDigits}`;
  let formatter = currencyFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency,
      currencyDisplay: 'narrowSymbol',
      minimumFractionDigits: fractionDigits,
      maximumFractionDigits: fractionDigits,
    });
    currencyFormatters.set(key, formatter);
  }
  return formatter;
}

/** $1,234.56. `whole` drops cents for headline figures. */
export function formatMoney(amount: number, currency: string, options: { whole?: boolean; signed?: boolean } = {}): string {
  const digits = options.whole ? 0 : 2;
  const formatted = currencyFormatter(currency, digits).format(Math.abs(amount));
  if (options.signed) {
    return amount > 0 ? `+${formatted}` : amount < 0 ? `−${formatted}` : formatted;
  }
  return amount < 0 ? `−${formatted}` : formatted;
}

const compactFormatters = new Map<string, Intl.NumberFormat>();

/** $12.9K for axis ticks and compact labels. */
export function formatMoneyCompact(amount: number, currency: string): string {
  let formatter = compactFormatters.get(currency);
  if (!formatter) {
    formatter = new Intl.NumberFormat(undefined, { style: 'currency', currency, currencyDisplay: 'narrowSymbol', notation: 'compact', maximumFractionDigits: 1 });
    compactFormatters.set(currency, formatter);
  }
  return formatter.format(amount);
}

export function formatPercent(value: number, options: { signed?: boolean; digits?: number } = {}): string {
  const digits = options.digits ?? 1;
  const text = `${Math.abs(value).toFixed(digits).replace(/\.0$/, '')}%`;
  if (!options.signed) {
    return value < 0 ? `−${text}` : text;
  }
  return value > 0 ? `+${text}` : value < 0 ? `−${text}` : text;
}

function parseIsoDate(iso: string): Date {
  const [year, month, day] = iso.slice(0, 10).split('-').map(Number) as [number, number, number];
  return new Date(year, month - 1, day);
}

const MONTH_SHORT = new Intl.DateTimeFormat(undefined, { month: 'short' });
const MONTH_LONG = new Intl.DateTimeFormat(undefined, { month: 'long' });

/** Honors the date format chosen in Settings. */
export function formatDate(iso: string, dateFormat: string): string {
  const date = parseIsoDate(iso);
  const dd = String(date.getDate()).padStart(2, '0');
  const mm = String(date.getMonth() + 1).padStart(2, '0');
  const yyyy = date.getFullYear();

  switch (dateFormat) {
    case 'yyyy-MM-dd':
      return `${yyyy}-${mm}-${dd}`;
    case 'MM/dd/yyyy':
      return `${mm}/${dd}/${yyyy}`;
    case 'dd/MM/yyyy':
      return `${dd}/${mm}/${yyyy}`;
    case 'd MMM yyyy':
      return `${date.getDate()} ${MONTH_SHORT.format(date)} ${yyyy}`;
    default:
      return `${MONTH_SHORT.format(date)} ${date.getDate()}, ${yyyy}`;
  }
}

/** "Sep 12" — compact dates inside lists, where the year is implied by grouping. */
export function formatShortDate(iso: string, dateFormat: string): string {
  const date = parseIsoDate(iso);
  // Day-first formats keep the day first; everything else reads month first.
  return dateFormat === 'd MMM yyyy' || dateFormat === 'dd/MM/yyyy'
    ? `${date.getDate()} ${MONTH_SHORT.format(date)}`
    : `${MONTH_SHORT.format(date)} ${date.getDate()}`;
}

export function formatMonth(iso: string, style: 'short' | 'long' = 'short'): string {
  return (style === 'short' ? MONTH_SHORT : MONTH_LONG).format(parseIsoDate(iso));
}

const MONTH_YEAR = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' });

export function formatMonthYear(iso: string): string {
  return MONTH_YEAR.format(parseIsoDate(iso));
}

const RELATIVE = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

export function formatRelativeTime(isoDateTime: string, now = Date.now()): string {
  const then = new Date(isoDateTime).getTime();
  const seconds = Math.round((then - now) / 1000);
  const rtf = RELATIVE;
  const abs = Math.abs(seconds);
  if (abs < 60) return rtf.format(seconds, 'second');
  if (abs < 3600) return rtf.format(Math.round(seconds / 60), 'minute');
  if (abs < 86400) return rtf.format(Math.round(seconds / 3600), 'hour');
  return rtf.format(Math.round(seconds / 86400), 'day');
}

export function greeting(now = new Date()): string {
  const hour = now.getHours();
  if (hour < 5) return 'Good evening';
  if (hour < 12) return 'Good morning';
  if (hour < 18) return 'Good afternoon';
  return 'Good evening';
}

export function accountLabel(institution: string | null, mask: string | null): string {
  if (institution && mask) return `${institution} ••${mask}`;
  if (mask) return `Account ••${mask}`;
  return institution ?? 'Account';
}
