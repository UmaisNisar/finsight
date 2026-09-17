import { useEffect } from 'react';
import { ApiError, ERROR_COPY, isChunkLoadError } from '@/api/client';
import { useToast } from '@/app/providers/ToastProvider';

/** At most one background-error toast in this window, however many errors arrive. */
export const GLOBAL_TOAST_INTERVAL_MS = 30_000;

const EXTENSION_SOURCE = /(chrome|moz|safari(-web)?|ms-browser)-extension:\/\//i;

/**
 * Errors that aren't ours to report: browser warnings, extensions, cancelled requests, and API failures (the
 * screen that made the request, or the query client, already explains those).
 */
export function isIgnorableError(reason: unknown, source?: string): boolean {
  if (reason instanceof ApiError) return true;
  if (reason instanceof DOMException && reason.name === 'AbortError') return true;
  const message = reason instanceof Error ? reason.message : typeof reason === 'string' ? reason : '';
  if (/ResizeObserver loop/i.test(message)) return true;
  if (message === 'Script error.' || message === 'Script error') return true;
  const stack = reason instanceof Error ? (reason.stack ?? '') : '';
  return EXTENSION_SOURCE.test(source ?? '') || EXTENSION_SOURCE.test(stack);
}

/**
 * Catches what no component handled: rejected promises nobody awaited and exceptions thrown from event handlers.
 * They are logged in development; people see one calm, non-blocking toast, not a stream of them.
 */
export function GlobalErrorListener() {
  const toast = useToast();

  useEffect(() => {
    let lastToast = -Infinity;

    function report(reason: unknown, source?: string) {
      if (isIgnorableError(reason, source)) return;
      if (import.meta.env.DEV) console.error('[FinSight] Unhandled error', reason);
      const now = Date.now();
      if (now - lastToast < GLOBAL_TOAST_INTERVAL_MS) return;
      lastToast = now;
      toast(isChunkLoadError(reason) ? ERROR_COPY.updated : 'Something went wrong. If anything looks off, reload the page.', 'error');
    }

    const onRejection = (event: PromiseRejectionEvent) => report(event.reason);
    const onError = (event: ErrorEvent) => report(event.error ?? event.message, event.filename);

    window.addEventListener('unhandledrejection', onRejection);
    window.addEventListener('error', onError);
    return () => {
      window.removeEventListener('unhandledrejection', onRejection);
      window.removeEventListener('error', onError);
    };
  }, [toast]);

  return null;
}
