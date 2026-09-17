import type { z } from 'zod';
import { apiErrorSchema } from './schemas';

/** An API failure with a stable code and a message that is safe to show. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

/**
 * The words people see when something fails. Every failure maps to one of these (or to a safe message from the
 * server), so no raw exception text, status line or stack trace ever reaches the screen.
 */
export const ERROR_COPY = {
  fallback: 'Something went wrong. Try again.',
  network: 'Can’t reach FinSight. Check your connection.',
  timeout: 'This is taking longer than usual. Try again.',
  server: 'FinSight is having trouble right now. Try again in a moment.',
  rateLimited: 'That’s a lot of requests. Wait a moment, then try again.',
  unauthenticated: 'Your session has ended. Sign in again.',
  forbidden: 'You don’t have access to that.',
  notFound: 'That couldn’t be found. It may have been deleted.',
  conflict: 'That changed while you were working. Reload and try again.',
  badRequest: 'That didn’t work. Check the details and try again.',
  invalidResponse: 'FinSight received an unexpected response. Reload and try again.',
  updated: 'FinSight was updated. Reload to get the latest version.',
} as const;

/** How long a request may take (including reading its response) before it fails with a timeout error. */
export const REQUEST_TIMEOUT_MS = 30_000;
/** Uploads send whole PDFs, so they get longer on slow connections. */
export const UPLOAD_TIMEOUT_MS = 120_000;

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  signal?: AbortSignal;
  /** Overrides the default timeout for this call. `null` waits indefinitely. */
  timeoutMs?: number | null;
}

/** Endpoints known to run long (such as generating an analysis) pass `timeoutMs` themselves. */
function timeoutFor(options: RequestOptions): number | null {
  if (options.timeoutMs !== undefined) return options.timeoutMs;
  if (options.body instanceof FormData) return UPLOAD_TIMEOUT_MS;
  return REQUEST_TIMEOUT_MS;
}

/**
 * All API traffic goes through here: same-origin cookies, the CSRF header the server requires on
 * state-changing requests, human-readable errors, and runtime validation of every response.
 * Every request has a timeout, so a request that hangs (an API restart, a stalled proxy) always settles as an
 * error: mutations leave their pending state, buttons re-enable and the error shows, instead of waiting forever.
 */
export async function request<T>(path: string, schema: z.ZodType<T>, options?: RequestOptions): Promise<T>;
export async function request(path: string, schema: null, options?: RequestOptions): Promise<void>;
export async function request<T>(path: string, schema: z.ZodType<T> | null, options: RequestOptions = {}): Promise<T | void> {
  const method = options.method ?? 'GET';
  const headers: Record<string, string> = { Accept: 'application/json' };
  let body: BodyInit | undefined;

  if (method !== 'GET') {
    headers['X-FinSight-Request'] = '1';
  }

  if (options.body instanceof FormData) {
    body = options.body;
  } else if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(options.body);
  }

  // One controller for the whole exchange, aborted by the timeout or by the caller's own signal.
  const controller = new AbortController();
  let timedOut = false;
  const timeoutMs = timeoutFor(options);
  const timer =
    timeoutMs === null
      ? undefined
      : setTimeout(() => {
          timedOut = true;
          controller.abort();
        }, timeoutMs);
  const forwardAbort = () => controller.abort(options.signal?.reason);
  if (options.signal?.aborted) controller.abort(options.signal.reason);
  else options.signal?.addEventListener('abort', forwardAbort, { once: true });

  // A failure caused by aborting: a timeout becomes a friendly error; the caller's own cancellation passes through.
  const aborted = (error: unknown): unknown => {
    if (timedOut) return new ApiError(0, 'timeout', ERROR_COPY.timeout);
    if (error instanceof DOMException && error.name === 'AbortError') return error;
    return options.signal?.aborted ? new DOMException('Aborted', 'AbortError') : null;
  };

  try {
    let response: Response;
    try {
      response = await fetch(path, { method, headers, body, credentials: 'same-origin', signal: controller.signal });
    } catch (error) {
      throw aborted(error) ?? new ApiError(0, 'network', ERROR_COPY.network);
    }

    if (!response.ok) {
      const apiError = await toApiError(response);
      throw aborted(apiError) ?? apiError;
    }

    if (schema === null || response.status === 204) {
      return;
    }

    let json: unknown;
    try {
      json = await response.json();
    } catch (error) {
      const abort = aborted(error);
      if (abort) throw abort;
      logInvalidResponse(path, error);
      throw new ApiError(response.status, 'invalid_response', ERROR_COPY.invalidResponse);
    }

    const parsed = schema.safeParse(json);
    if (!parsed.success) {
      logInvalidResponse(path, parsed.error.issues);
      throw new ApiError(response.status, 'invalid_response', ERROR_COPY.invalidResponse);
    }

    return parsed.data;
  } finally {
    clearTimeout(timer);
    options.signal?.removeEventListener('abort', forwardAbort);
  }
}

function logInvalidResponse(path: string, detail: unknown) {
  if (import.meta.env.DEV) {
    console.error(`Unexpected response from ${path}`, detail);
  }
}

function isTimeout(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'TimeoutError';
}

/** Friendly copy for a status the server didn't explain. */
function statusCopy(status: number): { code: string; message: string } {
  if (status === 401) return { code: 'unauthenticated', message: ERROR_COPY.unauthenticated };
  if (status === 403) return { code: 'forbidden', message: ERROR_COPY.forbidden };
  if (status === 404) return { code: 'not_found', message: ERROR_COPY.notFound };
  if (status === 408 || status === 504) return { code: 'timeout', message: ERROR_COPY.timeout };
  if (status === 409) return { code: 'conflict', message: ERROR_COPY.conflict };
  if (status === 429) return { code: 'rate_limited', message: ERROR_COPY.rateLimited };
  if (status >= 500) return { code: 'server_error', message: ERROR_COPY.server };
  if (status >= 400) return { code: 'bad_request', message: ERROR_COPY.badRequest };
  return { code: 'unexpected', message: ERROR_COPY.fallback };
}

/**
 * Whether a server message reads as a sentence meant for people: short, one line, and free of markup, stack frames
 * and exception names. Anything else falls back to the friendly copy for its status.
 */
export function isSafeMessage(message: string): boolean {
  const text = message.trim();
  if (text.length < 3 || text.length > 240) return false;
  if (/[\r\n<>{}]/.test(text)) return false;
  if (/\bat\s+[\w.$<>]+\s*\(|Exception\b|Traceback|\bstack\b|System\.|Microsoft\.|\bnull reference\b/i.test(text)) return false;
  return true;
}

async function toApiError(response: Response): Promise<ApiError> {
  const fallback = statusCopy(response.status);
  try {
    const parsed = apiErrorSchema.safeParse(await response.json());
    if (parsed.success) {
      return new ApiError(response.status, parsed.data.code, isSafeMessage(parsed.data.message) ? parsed.data.message : fallback.message);
    }
  } catch {
    // Non-JSON error body (a proxy page, an empty 502): use the copy for its status.
  }
  return new ApiError(response.status, fallback.code, fallback.message);
}

/** A lazy page or component whose code failed to download, typically after a redeploy or a dev server restart. */
export function isChunkLoadError(error: unknown): boolean {
  if (!(error instanceof Error)) return false;
  if (error.name === 'ChunkLoadError') return true;
  return /Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed|Unable to preload CSS|Loading (CSS )?chunk [\w-]+ failed|'text\/html' is not a valid JavaScript MIME type/i.test(
    error.message,
  );
}

/** Copy that is always safe to show for any thrown value. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  if (isChunkLoadError(error)) return ERROR_COPY.updated;
  if (isTimeout(error)) return ERROR_COPY.timeout;
  // fetch rejects with a TypeError when the network is down or the request is blocked.
  if (error instanceof TypeError && /fetch|network|load failed/i.test(error.message)) return ERROR_COPY.network;
  if (error instanceof Error && error.name === 'ZodError') return ERROR_COPY.invalidResponse;
  return ERROR_COPY.fallback;
}
