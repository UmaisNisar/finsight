import type { ReactNode } from 'react';
import { Skeleton } from './ui/primitives';

/**
 * Large page title with an optional subtitle and actions. Pass `subtitle="loading"` while the subtitle depends
 * on data: its line is reserved with a placeholder so the page below doesn't move when it arrives.
 */
export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: ReactNode | 'loading'; actions?: ReactNode }) {
  return (
    <header className="mb-7 flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between md:mb-9">
      <div className="min-w-0">
        <h1 className="title-large">{title}</h1>
        {subtitle === 'loading' ? (
          <p className="mt-1.5 flex h-[1.45em] items-center text-[1.0625rem]" aria-hidden="true">
            <Skeleton className="h-4 w-56 max-w-full" />
          </p>
        ) : (
          subtitle && <p className="fade-in mt-1.5 text-[1.0625rem] text-label-secondary">{subtitle}</p>
        )}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}
