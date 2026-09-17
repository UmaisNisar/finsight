import { formatDate } from './format';

const SAMPLE_DATE = '2026-09-12';

export const CURRENCY_OPTIONS = [
  { value: 'CAD', label: 'CAD' },
  { value: 'USD', label: 'USD' },
  { value: 'EUR', label: 'EUR' },
  { value: 'GBP', label: 'GBP' },
];

/** Date formats, each labelled with a sample date written that way. */
export const DATE_FORMAT_OPTIONS = ['MMM d, yyyy', 'd MMM yyyy', 'yyyy-MM-dd', 'MM/dd/yyyy', 'dd/MM/yyyy'].map((f) => ({ value: f, label: formatDate(SAMPLE_DATE, f) }));
