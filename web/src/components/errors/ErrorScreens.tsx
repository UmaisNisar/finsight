import { motion } from 'motion/react';
import { AlertCircle, ChevronDown, Compass, Download, RotateCw, TriangleAlert } from 'lucide-react';
import { useEffect, useId, useState, type ReactNode } from 'react';
import { isRouteErrorResponse } from 'react-router';
import { ERROR_COPY, isChunkLoadError } from '@/api/client';
import { Collapse } from '@/components/ui/AutoHeight';
import { Button, buttonStyles } from '@/components/ui/Button';
import { Card } from '@/components/ui/primitives';
import { cn } from '@/lib/cn';
import { claimAutoReload, reloadPage } from './chunkReload';

type Kind = 'crash' | 'update' | 'not-found' | 'response';

function kindOf(error: unknown): Kind {
  if (isRouteErrorResponse(error)) return error.status === 404 ? 'not-found' : 'response';
  if (isChunkLoadError(error)) return 'update';
  return 'crash';
}

/** Copy for a router error response other than 404, never its raw status text. */
function responseCopy(status: number): { title: string; description: string } {
  if (status === 401 || status === 403) return { title: 'You can’t open this page', description: 'Sign in with an account that has access, then try again.' };
  if (status >= 500) return { title: 'This page couldn’t load', description: ERROR_COPY.server };
  return { title: 'This page couldn’t load', description: 'The link may be out of date. Try again, or go back to Overview.' };
}

/** A short code to quote when reporting a problem, shown in production instead of technical detail. */
function makeReference() {
  const time = Date.now().toString(36).slice(-4);
  const random = Math.floor(Math.random() * 36 ** 3)
    .toString(36)
    .padStart(3, '0');
  return `FS-${time}${random}`.toUpperCase();
}

function describe(error: unknown): string {
  if (isRouteErrorResponse(error)) return `${error.status} ${error.statusText}`.trim();
  // V8 stacks begin with the name and message; other engines list only the frames.
  if (error instanceof Error) return error.stack?.includes(error.message) ? error.stack : `${error.name}: ${error.message}${error.stack ? `\n${error.stack}` : ''}`;
  try {
    return JSON.stringify(error, null, 2) ?? String(error);
  } catch {
    return String(error);
  }
}

/**
 * A "Details" disclosure. In development it holds the error and stack; in production only a reference code and
 * the time, so nothing technical is ever shown to people using the app.
 */
export function ErrorDetails({ error, className }: { error: unknown; className?: string }) {
  const [open, setOpen] = useState(false);
  const [{ reference, at }] = useState(() => ({ reference: makeReference(), at: new Date() }));
  const id = useId();

  return (
    <div className={cn('w-full', className)}>
      <button
        type="button"
        aria-expanded={open}
        aria-controls={id}
        onClick={() => setOpen((o) => !o)}
        className="caption mx-auto flex h-8 items-center gap-1 rounded-full px-3 transition-colors hover:bg-fill hover:text-label"
      >
        Details
        <ChevronDown size={14} className={cn('transition-transform duration-200', open && 'rotate-180')} aria-hidden="true" />
      </button>
      <Collapse open={open} id={id}>
        <div className="pt-2">
          {import.meta.env.DEV ? (
            <pre className="max-h-60 overflow-auto rounded-2xl bg-fill px-3.5 py-3 text-left font-mono text-[0.75rem] leading-relaxed break-all whitespace-pre-wrap text-label-secondary">
              {describe(error)}
            </pre>
          ) : (
            <p className="caption tabular rounded-2xl bg-fill px-3.5 py-2.5">
              Reference {reference} · {at.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}
            </p>
          )}
        </div>
      </Collapse>
    </div>
  );
}

/** Reloads once by itself when a page's code failed to load; the update screen stays as the fallback. */
function useAutoReload(enabled: boolean) {
  useEffect(() => {
    if (enabled && claimAutoReload()) reloadPage();
  }, [enabled]);
}

function ReloadButton({ variant = 'primary' }: { variant?: 'primary' | 'secondary' }) {
  return (
    <Button variant={variant} icon={<RotateCw size={15} aria-hidden="true" />} onClick={reloadPage}>
      Reload
    </Button>
  );
}

/**
 * The full-page screen for anything that breaks outside the app shell (or the shell itself). Calm and short:
 * the app mark, what happened in one line, Reload and a way home. Uses no router or query hooks, so it can render
 * whatever failed.
 */
export function FullPageError({ error }: { error: unknown }) {
  const kind = kindOf(error);
  useAutoReload(kind === 'update');

  const copy =
    kind === 'update'
      ? { title: 'FinSight was updated', description: 'Reload to get the latest version.' }
      : kind === 'not-found'
        ? { title: 'This page doesn’t exist', description: 'The link may be out of date.' }
        : kind === 'response' && isRouteErrorResponse(error)
          ? responseCopy(error.status)
          : { title: 'Something went wrong', description: 'FinSight ran into an unexpected problem. Reloading usually fixes it.' };

  return (
    <div className="flex min-h-dvh items-center justify-center px-5 pt-[max(2.5rem,env(safe-area-inset-top))] pb-10">
      {/* Only transform animates on the glass itself; opacity would flatten its blur while it ran. */}
      <motion.main
        role="alert"
        aria-labelledby="error-title"
        initial={{ scale: 0.97 }}
        animate={{ scale: 1 }}
        transition={{ type: 'spring', stiffness: 380, damping: 32 }}
        className="card w-full max-w-[420px] rounded-[32px] px-6 pt-9 pb-5 text-center sm:px-9"
      >
        <div className="fade-in">
          <img src="/favicon.svg" alt="" width={56} height={56} className="mx-auto mb-5 size-14 rounded-[16px] shadow-float" />
          <h1 id="error-title" className="text-[1.5rem] leading-tight font-bold tracking-[-0.024em] text-balance">
            {copy.title}
          </h1>
          <p className="mx-auto mt-2 max-w-[20rem] text-[0.9375rem] leading-snug text-pretty text-label-secondary">{copy.description}</p>
          <div className="mt-7 flex flex-col-reverse gap-2.5 sm:flex-row sm:justify-center">
            {kind === 'not-found' ? (
              <a href="/" className={buttonStyles()}>
                Go to Overview
              </a>
            ) : (
              <>
                <a href="/" className={buttonStyles({ variant: 'secondary' })}>
                  Go to Overview
                </a>
                <ReloadButton />
              </>
            )}
          </div>
          {kind !== 'update' && <ErrorDetails error={error} className="mt-5" />}
        </div>
      </motion.main>
    </div>
  );
}

function IconTile({ children }: { children: ReactNode }) {
  return <div className="mb-4 flex size-14 items-center justify-center rounded-2xl bg-fill text-label-secondary">{children}</div>;
}

/**
 * The error card for one screen inside the app shell. The sidebar and tab bar stay usable around it, and it clears
 * when another screen is opened.
 */
export function PageError({ error, onRetry, homeLink }: { error: unknown; onRetry: () => void; homeLink?: ReactNode }) {
  const kind = kindOf(error);
  useAutoReload(kind === 'update');

  let icon = <TriangleAlert size={26} aria-hidden="true" />;
  let title = 'This page couldn’t load';
  let description = 'Something unexpected went wrong. Try again, or choose another page.';
  let actions: ReactNode = (
    <Button variant="secondary" icon={<RotateCw size={15} aria-hidden="true" />} onClick={onRetry}>
      Try again
    </Button>
  );

  if (kind === 'update') {
    icon = <Download size={26} aria-hidden="true" />;
    title = 'FinSight was updated';
    description = 'Reload to get the latest version.';
    actions = <ReloadButton />;
  } else if (kind === 'not-found') {
    icon = <Compass size={26} aria-hidden="true" />;
    title = 'This page doesn’t exist';
    description = 'The link may be out of date.';
    actions = homeLink;
  } else if (kind === 'response' && isRouteErrorResponse(error)) {
    ({ title, description } = responseCopy(error.status));
  }

  return (
    <Card>
      <div role="alert" className="fade-in mx-auto flex max-w-md flex-col items-center px-6 pt-12 pb-6 text-center">
        <IconTile>{icon}</IconTile>
        <h2 className="text-[1.0625rem] font-semibold tracking-[-0.01em]">{title}</h2>
        <p className="mt-1.5 text-[0.9375rem] text-pretty text-label-secondary">{description}</p>
        {actions && <div className="mt-5 flex flex-wrap justify-center gap-2">{actions}</div>}
        {kind === 'crash' && <ErrorDetails error={error} className="mt-4" />}
      </div>
    </Card>
  );
}

/**
 * Stands in for one widget (a chart, an AI card) that failed to render. Sized by the caller to the content it
 * replaces, so the card around it keeps its height.
 */
export function WidgetErrorFallback({ message, onRetry, className, minHeight }: { message: string; onRetry: () => void; className?: string; minHeight?: number | string }) {
  return (
    <div role="alert" className={cn('fade-in flex flex-col items-center justify-center px-4 py-6 text-center', className)} style={{ minHeight }}>
      <AlertCircle size={24} className="mb-2.5 text-label-tertiary" aria-hidden="true" />
      <p className="max-w-xs text-[0.9375rem] text-label-secondary">{message}</p>
      <Button variant="secondary" size="sm" className="mt-3.5" icon={<RotateCw size={14} aria-hidden="true" />} onClick={onRetry}>
        Try again
      </Button>
    </div>
  );
}
