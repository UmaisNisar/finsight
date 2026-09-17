import { Check } from 'lucide-react';
import { cn } from '@/lib/cn';

/**
 * The iOS selection circle: a native checkbox (visually hidden, so it keeps keyboard and screen reader support)
 * drawn as a ring that fills with the accent and a check. Place it inside a <label> that names it.
 */
export function RoundCheckbox({ checked, onChange, disabled }: { checked: boolean; onChange: () => void; disabled?: boolean }) {
  return (
    <>
      <input type="checkbox" checked={checked} onChange={onChange} disabled={disabled} className="peer sr-only" />
      <span
        aria-hidden="true"
        className={cn(
          'flex size-[22px] shrink-0 items-center justify-center rounded-full border-[1.5px] transition-[background-color,border-color,transform] duration-200 peer-focus-visible:outline-3 peer-focus-visible:outline-accent/50 peer-active:scale-90 peer-disabled:opacity-45',
          checked ? 'border-accent bg-accent text-white' : 'border-label-tertiary/60',
        )}
      >
        <Check size={13} strokeWidth={3} className={cn('transition-[opacity,transform] duration-200', checked ? 'scale-100 opacity-100' : 'scale-50 opacity-0')} />
      </span>
    </>
  );
}
