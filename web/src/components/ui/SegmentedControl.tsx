import { useId, type KeyboardEvent } from 'react';
import { useSlidingIndicator } from '@/hooks/useSlidingIndicator';
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

/**
 * An iOS-style segmented control, implemented as an accessible radio group with arrow-key navigation.
 * A single glass thumb slides between segments, measured within the control so it can't fly in from elsewhere.
 */
export function SegmentedControl<T extends string>({ label, options, value, onChange, className, size = 'md' }: Props<T>) {
  const id = useId();
  const { container, lens } = useSlidingIndicator<HTMLDivElement, HTMLSpanElement>('[aria-checked="true"]', value);

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
    <div ref={container} role="radiogroup" aria-label={label} className={cn('glass-control inline-flex rounded-full p-1', className)}>
      <span ref={lens} aria-hidden="true" className="sliding-lens glass-lens rounded-full opacity-0" />
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
              'relative flex-1 rounded-full font-medium whitespace-nowrap transition-colors duration-200',
              size === 'sm' ? 'h-7 px-2.5 text-[0.8125rem]' : 'h-8 px-3.5 text-[0.875rem]',
              selected ? 'text-label' : 'text-label-secondary hover:text-label',
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}
