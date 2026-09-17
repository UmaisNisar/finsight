import { ChevronLeft, ChevronRight } from 'lucide-react';
import { useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import { cn } from '@/lib/cn';
import { localIsoDate } from '@/lib/period';

function parse(iso: string): Date {
  const [y, m, d] = iso.split('-').map(Number) as [number, number, number];
  return new Date(y, m - 1, d);
}

const MONTH_TITLE = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' });
const DAY_LABEL = new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' });
const WEEKDAYS = Array.from({ length: 7 }, (_, i) => {
  const date = new Date(2026, 1, 1 + i); // 1 Feb 2026 is a Sunday.
  return { short: new Intl.DateTimeFormat(undefined, { weekday: 'narrow' }).format(date), long: new Intl.DateTimeFormat(undefined, { weekday: 'long' }).format(date) };
});

interface Props {
  value: string;
  onChange: (iso: string) => void;
  min?: string;
  max?: string;
  label: string;
  /** Today's date, injectable for tests. */
  today?: string;
}

function clamp(iso: string, min?: string, max?: string): string {
  if (min && iso < min) return min;
  if (max && iso > max) return max;
  return iso;
}

/**
 * An inline month calendar in the style of the iOS and macOS date pickers: month title with chevrons, narrow
 * weekday letters, and round day cells with the selection filled in the accent colour. Implements the ARIA
 * grid pattern: arrow keys move by day and week, Page Up/Down by month, Home/End to the week's ends.
 * Days outside min/max stay focusable (aria-disabled) so keyboard focus never falls off the grid.
 */
export function Calendar({ value, onChange, min, max, label, today = localIsoDate(new Date()) }: Props) {
  const [focusDate, setFocusDate] = useState(() => clamp(value || today, min, max));
  const gridRef = useRef<HTMLTableElement>(null);
  const moveFocus = useRef(false);

  // Keyboard moves focus after the render that shows the new day, which may be in another month.
  useLayoutEffect(() => {
    if (!moveFocus.current) return;
    moveFocus.current = false;
    gridRef.current?.querySelector<HTMLButtonElement>(`[data-date="${focusDate}"]`)?.focus();
  }, [focusDate]);

  const focus = parse(focusDate);
  const year = focus.getFullYear();
  const month = focus.getMonth();
  const first = new Date(year, month, 1);
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const cells: (string | null)[] = [...Array<null>(first.getDay()).fill(null), ...Array.from({ length: daysInMonth }, (_, i) => localIsoDate(new Date(year, month, i + 1)))];
  while (cells.length % 7) cells.push(null);
  const weeks = Array.from({ length: cells.length / 7 }, (_, i) => cells.slice(i * 7, i * 7 + 7));

  const outOfRange = (iso: string) => (!!min && iso < min) || (!!max && iso > max);
  const monthStart = localIsoDate(first);
  const monthEnd = localIsoDate(new Date(year, month, daysInMonth));
  const canGoBack = !min || monthStart > min;
  const canGoForward = !max || monthEnd < max;

  function moveTo(date: Date, keepFocus = true) {
    moveFocus.current = keepFocus;
    setFocusDate(localIsoDate(date));
  }

  function shiftMonth(delta: number, keepFocus = false) {
    const day = Math.min(focus.getDate(), new Date(year, month + delta + 1, 0).getDate());
    moveTo(new Date(year, month + delta, day), keepFocus);
  }

  function select(iso: string) {
    if (outOfRange(iso)) return;
    setFocusDate(iso);
    onChange(iso);
  }

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    const d = focus.getDate();
    const keys: Record<string, () => void> = {
      ArrowLeft: () => moveTo(new Date(year, month, d - 1)),
      ArrowRight: () => moveTo(new Date(year, month, d + 1)),
      ArrowUp: () => moveTo(new Date(year, month, d - 7)),
      ArrowDown: () => moveTo(new Date(year, month, d + 7)),
      Home: () => moveTo(new Date(year, month, d - focus.getDay())),
      End: () => moveTo(new Date(year, month, d + (6 - focus.getDay()))),
      PageUp: () => shiftMonth(-1, true),
      PageDown: () => shiftMonth(1, true),
    };
    const action = keys[event.key];
    if (action) {
      event.preventDefault();
      action();
    }
  }

  const monthTitle = MONTH_TITLE.format(first);
  const navButton = 'flex size-8 items-center justify-center rounded-full text-accent transition-[background-color,opacity] hover:bg-fill disabled:opacity-30 disabled:hover:bg-transparent';

  return (
    <div className="select-none">
      <div className="mb-1 flex items-center justify-between pl-2">
        <span className="text-[0.9375rem] font-semibold" aria-live="polite">
          {monthTitle}
        </span>
        <div className="flex">
          <button type="button" aria-label="Previous month" disabled={!canGoBack} onClick={() => shiftMonth(-1)} className={navButton}>
            <ChevronLeft size={18} strokeWidth={2.4} aria-hidden="true" />
          </button>
          <button type="button" aria-label="Next month" disabled={!canGoForward} onClick={() => shiftMonth(1)} className={navButton}>
            <ChevronRight size={18} strokeWidth={2.4} aria-hidden="true" />
          </button>
        </div>
      </div>
      <table ref={gridRef} role="grid" aria-label={`${label}, ${monthTitle}`} className="w-full table-fixed border-collapse">
        <thead>
          <tr>
            {WEEKDAYS.map((w) => (
              <th key={w.long} scope="col" abbr={w.long} className="h-7 text-[0.6875rem] font-semibold text-label-tertiary uppercase">
                {w.short}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {weeks.map((week, i) => (
            <tr key={i}>
              {week.map((iso, j) => {
                if (!iso) return <td key={j} />;
                const selected = iso === value;
                const disabled = outOfRange(iso);
                return (
                  <td key={iso} className="p-0 text-center" aria-selected={selected}>
                    <button
                      type="button"
                      data-date={iso}
                      tabIndex={iso === focusDate ? 0 : -1}
                      aria-disabled={disabled || undefined}
                      aria-label={DAY_LABEL.format(parse(iso))}
                      aria-current={iso === today ? 'date' : undefined}
                      onClick={() => select(iso)}
                      onFocus={() => setFocusDate(iso)}
                      onKeyDown={onKeyDown}
                      className={cn(
                        'tabular mx-auto flex size-9 items-center justify-center rounded-full text-[0.9375rem] transition-colors',
                        disabled && 'cursor-default opacity-30',
                        selected ? 'bg-accent font-semibold text-accent-contrast' : iso === today ? 'font-semibold text-accent' : '',
                        !selected && !disabled && 'hover:bg-fill',
                      )}
                    >
                      {Number(iso.slice(8))}
                    </button>
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
