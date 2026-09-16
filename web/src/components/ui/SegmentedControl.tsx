import { motion } from 'motion/react';
import { useId, type KeyboardEvent } from 'react';
import { cn } from '@/lib/cn';

interface Option<T extends string> {
  value: T;
  label: string;
}

interface Props<T extends string> {
  label: string;
  options: Option<T>[];
  value: T;
  onChange: (value: T) => void;
  className?: string;
  size?: 'sm' | 'md';
}

/** An iOS-style segmented control, implemented as an accessible radio group with arrow-key navigation. */
export function SegmentedControl<T extends string>({ label, options, value, onChange, className, size = 'md' }: Props<T>) {
  const id = useId();

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    const index = options.findIndex((o) => o.value === value);
    const delta = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0;
    if (delta === 0) return;
    event.preventDefault();
    const next = options[(index + delta + options.length) % options.length];
    if (next) {
      onChange(next.value);
      document.getElementById(`${id}-${next.value}`)?.focus();
    }
  }

  return (
    <div
      role="radiogroup"
      aria-label={label}
      className={cn('relative inline-flex rounded-[10px] bg-fill p-0.5', className)}
    >
      {options.map((option) => {
        const selected = option.value === value;
        return (
          <button
            key={option.value}
            id={`${id}-${option.value}`}
            type="button"
            role="radio"
            aria-checked={selected}
            tabIndex={selected ? 0 : -1}
            onClick={() => onChange(option.value)}
            onKeyDown={onKeyDown}
            className={cn(
              'relative flex-1 rounded-[8px] font-medium whitespace-nowrap transition-colors',
              size === 'sm' ? 'h-7 px-2.5 text-[0.8125rem]' : 'h-8 px-3.5 text-[0.875rem]',
              selected ? 'text-label' : 'text-label-secondary hover:text-label',
            )}
          >
            {selected && (
              <motion.span
                layoutId={`${id}-thumb`}
                className="absolute inset-0 rounded-[8px] bg-surface-raised shadow-[0_1px_3px_rgb(0_0_0/0.12),0_0_0_0.5px_rgb(0_0_0/0.04)]"
                transition={{ type: 'spring', stiffness: 500, damping: 38 }}
              />
            )}
            <span className="relative">{option.label}</span>
          </button>
        );
      })}
    </div>
  );
}
