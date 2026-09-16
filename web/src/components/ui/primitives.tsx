import { AlertCircle, RotateCw } from 'lucide-react';
import type { ReactNode } from 'react';
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
        'relative inline-flex h-[31px] w-[51px] shrink-0 items-center rounded-full p-[2px] transition-colors duration-200 disabled:opacity-45',
        checked ? 'bg-positive' : 'bg-fill-strong',
      )}
    >
      <span
        className={cn(
          'size-[27px] rounded-full bg-white shadow-[0_3px_8px_rgb(0_0_0/0.15),0_1px_1px_rgb(0_0_0/0.16)] transition-transform duration-200',
          checked && 'translate-x-[20px]',
        )}
      />
    </button>
  );
}

export function Skeleton({ className }: { className?: string }) {
  return <div aria-hidden="true" className={cn('animate-pulse rounded-lg bg-fill', className)} />;
}

export function Card({ className, children, as: Tag = 'section', ...rest }: { className?: string; children: ReactNode; as?: 'section' | 'div' | 'article'; 'aria-labelledby'?: string }) {
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
    <div className={cn('mx-auto flex max-w-sm flex-col items-center px-6 py-12 text-center', className)}>
      {icon && <div className="mb-4 flex size-14 items-center justify-center rounded-2xl bg-fill text-label-secondary">{icon}</div>}
      <h3 className="text-[1.0625rem] font-semibold tracking-[-0.01em]">{title}</h3>
      {description && <p className="mt-1.5 text-[0.9375rem] text-label-secondary">{description}</p>}
      {action && <div className="mt-5 flex flex-wrap justify-center gap-2">{action}</div>}
    </div>
  );
}

export function ErrorState({ message, onRetry, className }: { message: string; onRetry?: () => void; className?: string }) {
  return (
    <div role="alert" className={cn('flex flex-col items-center px-6 py-10 text-center', className)}>
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

export function Pill({ tone = 'neutral', children, icon }: { tone?: Tone; children: ReactNode; icon?: ReactNode }) {
  return (
    <span className={cn('inline-flex h-6 items-center gap-1 rounded-full px-2.5 text-[0.75rem] font-medium whitespace-nowrap', toneClasses[tone])}>
      {icon}
      {children}
    </span>
  );
}

/** Grouped list rows in the style of iOS Settings: one surface, hairline separators inset from the left. */
export function GroupedList({ children, className, label }: { children: ReactNode; className?: string; label?: string }) {
  return (
    <ul aria-label={label} className={cn('card overflow-hidden [&>li+li]:shadow-[inset_0_0.5px_0_var(--separator)]', className)}>
      {children}
    </ul>
  );
}

export function VisuallyHidden({ children }: { children: ReactNode }) {
  return <span className="sr-only">{children}</span>;
}
