import { Loader2 } from 'lucide-react';
import type { ButtonHTMLAttributes, ReactNode, Ref } from 'react';
import { cn } from '@/lib/cn';
import { Tooltip } from './Tooltip';

type Variant = 'primary' | 'secondary' | 'plain' | 'tinted' | 'destructive' | 'destructive-plain';
type Size = 'sm' | 'md' | 'lg';

const variants: Record<Variant, string> = {
  // Hover and press states change colour, never apply a CSS filter: filters force GPU layers that halo on glass.
  primary: 'glass-tint text-accent-contrast disabled:text-label-tertiary disabled:opacity-100',
  secondary: 'glass-control text-label hover:bg-fill-strong',
  plain: 'text-accent hover:bg-accent-soft',
  tinted: 'bg-accent-soft text-accent hover:bg-[color-mix(in_srgb,var(--accent)_20%,transparent)]',
  destructive: 'bg-critical-soft text-critical hover:bg-[color-mix(in_srgb,var(--critical)_20%,transparent)]',
  'destructive-plain': 'text-critical hover:bg-critical-soft',
};

const sizes: Record<Size, string> = {
  sm: 'h-8 px-3 text-[0.8125rem] gap-1.5',
  md: 'h-10 px-4 text-[0.9375rem] gap-2',
  lg: 'h-12 px-6 text-base gap-2',
};

/** Button classes, for links that should look like buttons (router links, OAuth redirects). */
export function buttonStyles({ variant = 'primary', size = 'md', className }: { variant?: Variant; size?: Size; className?: string } = {}): string {
  return cn(
    'button inline-flex shrink-0 select-none items-center justify-center rounded-full font-medium tracking-[-0.01em] whitespace-nowrap disabled:opacity-45',
    variants[variant],
    sizes[size],
    className,
  );
}

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
  loading?: boolean;
  icon?: ReactNode;
}

export function Button({ variant = 'primary', size = 'md', loading, icon, className, children, disabled, type = 'button', ...props }: ButtonProps) {
  return (
    <button type={type} disabled={disabled || loading} aria-busy={loading || undefined} className={buttonStyles({ variant, size, className })} {...props}>
      {loading ? <Loader2 size={16} className="animate-spin" aria-hidden="true" /> : icon}
      {children}
    </button>
  );
}

/** An icon-only button. Its label is its accessible name and, on hover or keyboard focus, a help tag. */
export function IconButton({ label, className, children, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { label: string; ref?: Ref<HTMLButtonElement> }) {
  return (
    <Tooltip content={label} describe={false}>
    <button
      type="button"
      aria-label={label}
      className={cn(
        'inline-flex size-9 items-center justify-center rounded-full text-label-secondary transition-[background-color,color,transform] duration-200 hover:bg-fill hover:text-label active:scale-[0.94]',
        className,
      )}
      {...props}
    >
      {children}
    </button>
    </Tooltip>
  );
}
