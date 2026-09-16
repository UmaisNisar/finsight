import { Loader2 } from 'lucide-react';
import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { cn } from '@/lib/cn';

type Variant = 'primary' | 'secondary' | 'plain' | 'destructive' | 'destructive-plain';
type Size = 'sm' | 'md' | 'lg';

const variants: Record<Variant, string> = {
  primary: 'bg-accent text-accent-contrast hover:brightness-110 active:brightness-95',
  secondary: 'bg-fill text-label hover:bg-fill-strong',
  plain: 'text-accent hover:bg-accent-soft',
  destructive: 'bg-critical-soft text-critical hover:brightness-95',
  'destructive-plain': 'text-critical hover:bg-critical-soft',
};

const sizes: Record<Size, string> = {
  sm: 'h-8 px-3 text-[0.8125rem] gap-1.5',
  md: 'h-10 px-4 text-[0.9375rem] gap-2',
  lg: 'h-12 px-6 text-base gap-2',
};

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
  loading?: boolean;
  icon?: ReactNode;
}

export function Button({ variant = 'primary', size = 'md', loading, icon, className, children, disabled, type = 'button', ...props }: ButtonProps) {
  return (
    <button
      type={type}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={cn(
        'inline-flex shrink-0 select-none items-center justify-center rounded-full font-medium tracking-[-0.01em] whitespace-nowrap',
        'transition-[filter,background-color,opacity,transform] duration-150 active:scale-[0.98] disabled:opacity-45 disabled:active:scale-100',
        variants[variant],
        sizes[size],
        className,
      )}
      {...props}
    >
      {loading ? <Loader2 size={16} className="animate-spin" aria-hidden="true" /> : icon}
      {children}
    </button>
  );
}

export function IconButton({ label, className, children, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { label: string }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      className={cn(
        'inline-flex size-9 items-center justify-center rounded-full text-label-secondary transition-colors hover:bg-fill hover:text-label',
        className,
      )}
      {...props}
    >
      {children}
    </button>
  );
}
