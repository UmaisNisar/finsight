import { AnimatePresence, motion } from 'motion/react';
import { Calendar as CalendarIcon, Check, ChevronDown, ChevronRight } from 'lucide-react';
import { useEffect, useId, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { usePeriod } from '@/hooks/usePeriod';
import { cn } from '@/lib/cn';
import { formatDate } from '@/lib/format';
import { localIsoDate, PERIOD_PRESETS, periodLabel, type PeriodSelection } from '@/lib/period';
import { Button } from './ui/Button';
import { Calendar } from './ui/Calendar';

/** Height-and-fade reveal for sections that expand inside the menu. */
const EXPAND = {
  initial: { height: 0, opacity: 0 },
  animate: { height: 'auto', opacity: 1 },
  exit: { height: 0, opacity: 0 },
  transition: { type: 'spring', stiffness: 520, damping: 42, opacity: { duration: 0.16 } },
} as const;

/**
 * Time period menu: presets plus a custom range. Every dashboard component reads the same selection.
 * Presets follow the ARIA menu pattern (arrow keys move, Enter chooses); the custom range is a disclosure.
 */
export function PeriodPicker({ resolvedLabel, className }: { resolvedLabel?: string; className?: string }) {
  const { period, setPeriod } = usePeriod();
  const [open, setOpen] = useState(false);
  const [customOpen, setCustomOpen] = useState(false);
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [editingField, setEditingField] = useState<'from' | 'to' | null>(null);
  const [today, setToday] = useState(() => localIsoDate(new Date()));
  const containerRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const panelId = useId();

  useEffect(() => {
    if (!open) return;
    function onPointer(event: PointerEvent) {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    }
    document.addEventListener('pointerdown', onPointer);
    return () => document.removeEventListener('pointerdown', onPointer);
  }, [open]);

  function openMenu() {
    // Start from the current selection each time, so an abandoned custom range never lingers.
    const isCustom = period.preset === 'custom';
    setToday(localIsoDate(new Date()));
    setFrom(period.from ?? '');
    setTo(period.to ?? localIsoDate(new Date()));
    setCustomOpen(isCustom);
    setEditingField(null);
    setOpen(true);
    requestAnimationFrame(() => {
      const target = menuRef.current?.querySelector<HTMLElement>('[aria-checked="true"]') ?? menuRef.current?.querySelector<HTMLElement>('[role="menuitemradio"]');
      target?.focus({ preventScroll: true });
    });
  }

  function close(refocus = true) {
    setOpen(false);
    if (refocus) buttonRef.current?.focus({ preventScroll: true });
  }

  function choose(next: PeriodSelection) {
    setPeriod(next);
    close();
  }

  function applyCustom(event: FormEvent) {
    event.preventDefault();
    if (from && to && from <= to) {
      choose({ preset: 'custom', from, to });
    }
  }

  function onPanelKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Escape') {
      event.stopPropagation();
      close();
    }
  }

  function onMenuKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const items = [...(menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitemradio"]') ?? [])];
    const index = items.indexOf(document.activeElement as HTMLElement);
    const next = { ArrowDown: index + 1, ArrowUp: index - 1, Home: 0, End: items.length - 1 }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    items[(next + items.length) % items.length]?.focus();
  }

  const label = periodLabel(period, resolvedLabel);

  return (
    <div ref={containerRef} className={cn('relative', className)}>
      <button
        ref={buttonRef}
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        aria-label={`Time period: ${label}`}
        onClick={() => (open ? close(false) : openMenu())}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown' && !open) {
            event.preventDefault();
            openMenu();
          }
        }}
        className="glass-control inline-flex h-9 items-center gap-2 rounded-full pr-3 pl-3.5 text-[0.9375rem] font-medium transition-[background-color,transform] duration-200 hover:bg-fill active:scale-[0.97]"
      >
        <CalendarIcon size={16} className="shrink-0 text-label-secondary" aria-hidden="true" />
        <span className="max-w-[14rem] truncate">{label}</span>
        <ChevronDown size={15} className={cn('shrink-0 text-label-secondary transition-transform duration-200', open && 'rotate-180')} aria-hidden="true" />
      </button>

      <AnimatePresence>
        {open && (
          <motion.div
            id={panelId}
            role="dialog"
            aria-label="Time period"
            onKeyDown={onPanelKeyDown}
            initial={{ opacity: 0, y: -4, scale: 0.97 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -4, scale: 0.97, transition: { duration: 0.12, ease: 'easeIn' } }}
            transition={{ duration: 0.18, ease: [0.2, 0.8, 0.2, 1] }}
            // Phones: the button sits at the start of the header, so the menu opens rightwards from it.
            className="glass absolute top-full left-0 z-40 mt-2 w-[min(19rem,calc(100vw-2rem))] origin-top-left overflow-hidden rounded-[22px] p-1.5 sm:right-0 sm:left-auto sm:origin-top-right"
          >
            <p className="eyebrow px-3 pt-1.5 pb-1" aria-hidden="true">
              Time period
            </p>
            <div ref={menuRef} role="menu" aria-label="Presets" tabIndex={-1} onKeyDown={onMenuKeyDown}>
              {PERIOD_PRESETS.map((preset) => {
                const selected = period.preset === preset.value;
                return (
                  <button
                    key={preset.value}
                    type="button"
                    role="menuitemradio"
                    aria-checked={selected}
                    tabIndex={-1}
                    onClick={() => choose({ preset: preset.value })}
                    className="flex h-10 w-full items-center gap-2 rounded-[10px] px-3 text-left text-[0.9375rem] transition-colors outline-none hover:bg-fill focus-visible:bg-fill"
                  >
                    <span className="w-4">{selected && <Check size={16} strokeWidth={2.5} className="text-accent" aria-hidden="true" />}</span>
                    {preset.label}
                  </button>
                );
              })}
            </div>
            <div className="my-1.5 h-px bg-separator" aria-hidden="true" />
            <button
              type="button"
              aria-expanded={customOpen}
              onClick={() => setCustomOpen((o) => !o)}
              className="flex h-10 w-full items-center gap-2 rounded-[10px] px-3 text-left text-[0.9375rem] transition-colors hover:bg-fill"
            >
              <span className="w-4">{period.preset === 'custom' && <Check size={16} strokeWidth={2.5} className="text-accent" aria-hidden="true" />}</span>
              <span className="flex-1">Custom range</span>
              <ChevronRight size={15} className={cn('text-label-tertiary transition-transform duration-200', customOpen && 'rotate-90')} aria-hidden="true" />
            </button>
            <AnimatePresence initial={false}>
              {customOpen && (
                <motion.form key="custom" onSubmit={applyCustom} className="overflow-hidden" {...EXPAND}>
                  <div className="space-y-2 px-1.5 pt-1 pb-1.5">
                    <div className="grid grid-cols-2 gap-2">
                      {(['from', 'to'] as const).map((field) => {
                        const iso = field === 'from' ? from : to;
                        const editing = editingField === field;
                        return (
                          <button
                            key={field}
                            type="button"
                            aria-expanded={editing}
                            onClick={() => setEditingField(editing ? null : field)}
                            className={cn(
                              'flex flex-col items-start rounded-[14px] px-3 py-2 text-left transition-colors',
                              editing ? 'bg-accent-soft' : 'bg-fill hover:bg-fill-strong',
                            )}
                          >
                            <span className="caption">{field === 'from' ? 'Start' : 'End'}</span>
                            <span className={cn('tabular text-[0.9375rem] font-medium', editing ? 'text-accent' : iso ? 'text-label' : 'text-label-tertiary')}>
                              {iso ? formatDate(iso, 'MMM d, yyyy') : 'Choose'}
                            </span>
                          </button>
                        );
                      })}
                    </div>
                    <AnimatePresence initial={false}>
                      {editingField && (
                        <motion.div key={editingField} className="overflow-hidden" {...EXPAND}>
                          <Calendar
                            label={editingField === 'from' ? 'Start date' : 'End date'}
                            value={editingField === 'from' ? from : to}
                            min={editingField === 'to' ? from || undefined : undefined}
                            max={editingField === 'from' ? to || today : today}
                            today={today}
                            onChange={(iso) => {
                              if (editingField === 'from') {
                                setFrom(iso);
                                setEditingField(to ? null : 'to');
                              } else {
                                setTo(iso);
                                setEditingField(from ? null : 'from');
                              }
                            }}
                          />
                        </motion.div>
                      )}
                    </AnimatePresence>
                    <Button type="submit" size="sm" className="w-full" disabled={!from || !to || from > to}>
                      Apply range
                    </Button>
                  </div>
                </motion.form>
              )}
            </AnimatePresence>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
