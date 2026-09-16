import { AnimatePresence, motion } from 'motion/react';
import { Calendar, Check, ChevronDown } from 'lucide-react';
import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { usePeriod } from '@/hooks/usePeriod';
import { cn } from '@/lib/cn';
import { PERIOD_PRESETS, toIsoDate, type PeriodSelection } from '@/lib/period';
import { Button } from './ui/Button';

export function periodLabel(period: PeriodSelection, resolvedLabel?: string): string {
  if (period.preset === 'custom') {
    return resolvedLabel ?? 'Custom range';
  }
  return PERIOD_PRESETS.find((p) => p.value === period.preset)?.label ?? 'Last month';
}

/** Time period menu: presets plus a custom range. Every dashboard component reads the same selection. */
export function PeriodPicker({ resolvedLabel, className }: { resolvedLabel?: string; className?: string }) {
  const { period, setPeriod } = usePeriod();
  const [open, setOpen] = useState(false);
  const [customOpen, setCustomOpen] = useState(period.preset === 'custom');
  const containerRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuId = useId();

  const today = toIsoDate(new Date());
  const [from, setFrom] = useState(period.from ?? '');
  const [to, setTo] = useState(period.to ?? today);

  useEffect(() => {
    if (!open) return;
    function onPointer(event: PointerEvent) {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    }
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        setOpen(false);
        buttonRef.current?.focus();
      }
    }
    document.addEventListener('pointerdown', onPointer);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('pointerdown', onPointer);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  function choose(next: PeriodSelection) {
    setPeriod(next);
    setOpen(false);
    buttonRef.current?.focus();
  }

  function applyCustom(event: FormEvent) {
    event.preventDefault();
    if (from && to && from <= to) {
      choose({ preset: 'custom', from, to });
    }
  }

  const label = periodLabel(period, resolvedLabel);

  return (
    <div ref={containerRef} className={cn('relative', className)}>
      <button
        ref={buttonRef}
        type="button"
        aria-haspopup="true"
        aria-expanded={open}
        aria-controls={menuId}
        onClick={() => setOpen((o) => !o)}
        className="inline-flex h-9 items-center gap-2 rounded-full bg-surface pr-3 pl-3.5 text-[0.9375rem] font-medium shadow-soft transition-colors hover:bg-surface-raised"
      >
        <Calendar size={16} className="text-label-secondary" aria-hidden="true" />
        <span className="max-w-[14rem] truncate">{label}</span>
        <ChevronDown size={15} className={cn('text-label-secondary transition-transform', open && 'rotate-180')} aria-hidden="true" />
      </button>

      <AnimatePresence>
        {open && (
          <motion.div
            id={menuId}
            initial={{ opacity: 0, y: -4, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -4, scale: 0.98 }}
            transition={{ duration: 0.16, ease: [0.2, 0.8, 0.2, 1] }}
            className="glass absolute right-0 z-40 mt-2 w-72 origin-top-right rounded-2xl p-1.5 shadow-float"
          >
            <fieldset>
              <legend className="eyebrow px-3 pt-1.5 pb-1">Time period</legend>
              {PERIOD_PRESETS.map((preset) => {
                const selected = period.preset === preset.value;
                return (
                  <button
                    key={preset.value}
                    type="button"
                    role="menuitemradio"
                    aria-checked={selected}
                    onClick={() => choose({ preset: preset.value })}
                    className="flex h-10 w-full items-center gap-2 rounded-[10px] px-3 text-left text-[0.9375rem] hover:bg-fill"
                  >
                    <span className="w-4">{selected && <Check size={16} strokeWidth={2.5} className="text-accent" aria-hidden="true" />}</span>
                    {preset.label}
                  </button>
                );
              })}
            </fieldset>
            <div className="my-1.5 h-px bg-separator" />
            <button
              type="button"
              aria-expanded={customOpen}
              onClick={() => setCustomOpen((o) => !o)}
              className="flex h-10 w-full items-center gap-2 rounded-[10px] px-3 text-left text-[0.9375rem] hover:bg-fill"
            >
              <span className="w-4">{period.preset === 'custom' && <Check size={16} strokeWidth={2.5} className="text-accent" aria-hidden="true" />}</span>
              Custom range…
            </button>
            {customOpen && (
              <form onSubmit={applyCustom} className="space-y-2 px-3 pt-1 pb-2">
                <div className="grid grid-cols-2 gap-2">
                  <label className="caption flex flex-col gap-1">
                    From
                    <input
                      type="date"
                      required
                      max={to || today}
                      value={from}
                      onChange={(e) => setFrom(e.target.value)}
                      className="h-9 rounded-lg bg-fill px-2 text-[0.875rem] text-label"
                    />
                  </label>
                  <label className="caption flex flex-col gap-1">
                    To
                    <input
                      type="date"
                      required
                      min={from}
                      value={to}
                      onChange={(e) => setTo(e.target.value)}
                      className="h-9 rounded-lg bg-fill px-2 text-[0.875rem] text-label"
                    />
                  </label>
                </div>
                <Button type="submit" size="sm" className="w-full" disabled={!from || !to || from > to}>
                  Apply range
                </Button>
              </form>
            )}
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
