import { type QueryKey, useQueryClient } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { DevCrash } from './devCrash';
import { ErrorBoundary } from './ErrorBoundary';
import { WidgetErrorFallback } from './ErrorScreens';

export interface WidgetBoundaryProps {
  /** Identifies the widget in development (`?__crash=<name>`, or `?__crash=card` for all). */
  name: string;
  children: ReactNode;
  /** One short sentence, e.g. "This chart couldn’t be shown." */
  message?: string;
  /** Data the widget shows; Try again refetches it along with re-rendering. */
  queryKeys?: readonly QueryKey[];
  /** Clears a shown error when any of these change (for example the selected period). */
  resetKeys?: readonly unknown[];
  /** The height of the content being replaced, so the card doesn't shrink. */
  minHeight?: number | string;
  className?: string;
  /**
   * Wraps the fallback in the widget's own card shell, for widgets that render their card themselves (such as the
   * AI insight card). Omit when the boundary already sits inside a card.
   */
  shell?: (fallback: ReactNode) => ReactNode;
}

/**
 * A card-sized error boundary for an isolated widget: a chart, an AI card, a step body. If it fails to render, only
 * its content is replaced by a short message with Try again, and the rest of the page keeps working.
 */
export function WidgetBoundary({ name, children, message = 'This couldn’t be shown right now.', queryKeys, resetKeys, minHeight, className, shell }: WidgetBoundaryProps) {
  const client = useQueryClient();

  return (
    <ErrorBoundary
      resetKeys={resetKeys}
      onReset={() => queryKeys?.forEach((queryKey) => void client.invalidateQueries({ queryKey }))}
      fallback={({ reset }) => {
        const fallback = <WidgetErrorFallback message={message} onRetry={reset} minHeight={minHeight} className={className} />;
        return shell ? shell(fallback) : fallback;
      }}
    >
      {import.meta.env.DEV ? <DevCrash target={`card:${name}`}>{children}</DevCrash> : children}
    </ErrorBoundary>
  );
}
