import { AlertCircle, RotateCw } from 'lucide-react';
import type { CSSProperties, HTMLAttributes, ReactNode, Ref } from 'react';
import { cn } from '@/lib/cn';
import { Button } from './Button';

export function Switch({ checked, onChange, label, disabled }: { checked: boolean; onChange: (checked: boolean) => void; label: string; disabled?: boolean }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cn(
        'group relative inline-flex h-[31px] w-[51px] shrink-0 items-center rounded-full p-[2px] transition-colors duration-200 disabled:opacity-45',
        checked ? 'bg-positive' : 'bg-fill-strong',
      )}
    >
      {/* The knob travels on a slightly overshooting curve and stretches while pressed, like the iOS switch. */}
      <span
        className={cn(
          'h-[27px] w-[27px] rounded-full bg-white shadow-[0_3px_8px_rgb(0_0_0/0.15),0_1px_1px_rgb(0_0_0/0.16)] transition-[transform,width] duration-300 ease-[cubic-bezier(0.34,1.36,0.64,1)] group-active:w-[31px]',
          checked && 'translate-x-[20px] group-active:translate-x-[16px]',
        )}
      />
    </button>
  );
}

/** A placeholder block. Renders a span so it can stand in for text inside paragraphs and headings. */
export function Skeleton({ className, style }: { className?: string; style?: CSSProperties }) {
  // A default radius only when none is given; two rounded-* classes would be resolved by stylesheet order.
  return <span aria-hidden="true" className={cn('block animate-pulse bg-fill', !className?.includes('rounded') && 'rounded-lg', className)} style={style} />;
}

/** A placeholder list row: glyph, two lines of text and a trailing value, sized like the real rows it stands in for. */
export function RowSkeleton({ glyph = 38, className, trailing = true }: { glyph?: number; className?: string; trailing?: boolean }) {
  return (
    <div className={cn('flex items-center gap-3.5', className)} aria-hidden="true">
      <Skeleton className="shrink-0 rounded-full" style={{ width: glyph, height: glyph }} />
      {/* Two line boxes the height of a 15px title and a 13px caption, so rows match the real ones exactly. */}
      <span className="min-w-0 flex-1">
        <span className="flex h-[1.36rem] items-center">
          <Skeleton className="h-3.5 w-[45%] max-w-44" />
        </span>
        <span className="flex h-[1.18rem] items-center">
          <Skeleton className="h-3 w-[30%] max-w-28" />
        </span>
      </span>
      {trailing && <Skeleton className="h-3.5 w-16" />}
    </div>
  );
}

type CardProps = HTMLAttributes<HTMLElement> & { as?: 'section' | 'div' | 'article' };

/**
 * The glass content card. Cards that load data keep this shell mounted through loading, error and loaded
 * states, swapping only what is inside, so the page never jumps.
 */
export function Card({ className, children, as: Tag = 'section', ...rest }: CardProps) {
  return (
    <Tag className={cn('card', className)} {...rest}>
      {children}
    </Tag>
  );
}

export function SectionHeader({ id, title, subtitle, action }: { id?: string; title: string; subtitle?: ReactNode; action?: ReactNode }) {
  return (
    <div className="mb-4 flex items-end justify-between gap-4">
      <div className="min-w-0">
        <h2 id={id} className="title-section">
          {title}
        </h2>
        {subtitle && <p className="caption mt-0.5">{subtitle}</p>}
      </div>
      {action}
    </div>
  );
}

export function EmptyState({ icon, title, description, action, className }: { icon?: ReactNode; title: string; description?: ReactNode; action?: ReactNode; className?: string }) {
  return (
    <div className={cn('fade-in mx-auto flex max-w-sm flex-col items-center px-6 py-12 text-center', className)}>
      {icon && <div className="mb-4 flex size-14 items-center justify-center rounded-2xl bg-fill text-label-secondary">{icon}</div>}
      <h3 className="text-[1.0625rem] font-semibold tracking-[-0.01em]">{title}</h3>
      {description && <p className="mt-1.5 text-[0.9375rem] text-label-secondary">{description}</p>}
      {action && <div className="mt-5 flex flex-wrap justify-center gap-2">{action}</div>}
    </div>
  );
}

export function ErrorState({ message, onRetry, className }: { message: string; onRetry?: () => void; className?: string }) {
  return (
    <div role="alert" className={cn('fade-in flex flex-col items-center px-6 py-10 text-center', className)}>
      <AlertCircle size={28} className="mb-3 text-label-tertiary" aria-hidden="true" />
      <p className="max-w-sm text-[0.9375rem] text-label-secondary">{message}</p>
      {onRetry && (
        <Button variant="secondary" size="sm" className="mt-4" icon={<RotateCw size={14} aria-hidden="true" />} onClick={onRetry}>
          Try again
        </Button>
      )}
    </div>
  );
}

type Tone = 'neutral' | 'positive' | 'attention' | 'critical' | 'accent';

const toneClasses: Record<Tone, string> = {
  neutral: 'bg-fill text-label-secondary',
  positive: 'bg-positive-soft text-positive',
  attention: 'bg-attention-soft text-attention',
  critical: 'bg-critical-soft text-critical',
  accent: 'bg-accent-soft text-accent',
};

/** Extra attributes and a ref pass through, so a pill can carry a help tag (see Tooltip). */
export function Pill({ tone = 'neutral', children, icon, className, ...rest }: { tone?: Tone; children: ReactNode; icon?: ReactNode } & HTMLAttributes<HTMLSpanElement> & { ref?: Ref<HTMLSpanElement> }) {
  return (
    <span {...rest} className={cn('inline-flex h-6 items-center gap-1 rounded-full px-2.5 text-[0.75rem] font-medium whitespace-nowrap', toneClasses[tone], className)}>
      {icon}
      {children}
    </span>
  );
}

/** Grouped list rows in the style of iOS Settings: one surface, hairline separators between rows. */
export function GroupedList({ children, className, label }: { children: ReactNode; className?: string; label?: string }) {
  return (
    <ul aria-label={label} className={cn('card grouped overflow-hidden', className)}>
      {children}
    </ul>
  );
}
