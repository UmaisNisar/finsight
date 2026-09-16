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

const FALLBACK_MESSAGE = 'Something went wrong. Try again.';

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  signal?: AbortSignal;
}

/**
 * All API traffic goes through here: same-origin cookies, the CSRF header the server requires on
 * state-changing requests, human-readable errors, and runtime validation of every response.
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

  let response: Response;
  try {
    response = await fetch(path, { method, headers, body, credentials: 'same-origin', signal: options.signal });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error;
    }
    throw new ApiError(0, 'network', "Can't reach FinSight. Check your connection and try again.");
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  if (schema === null || response.status === 204) {
    return;
  }

  const json: unknown = await response.json();
  const parsed = schema.safeParse(json);
  if (!parsed.success) {
    if (import.meta.env.DEV) {
      console.error(`Unexpected response shape from ${path}`, parsed.error.issues);
    }
    throw new ApiError(response.status, 'invalid_response', FALLBACK_MESSAGE);
  }

  return parsed.data;
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const parsed = apiErrorSchema.safeParse(await response.json());
    if (parsed.success) {
      return new ApiError(response.status, parsed.data.code, parsed.data.message);
    }
  } catch {
    // Non-JSON error body: fall through to a generic message.
  }

  if (response.status === 401) {
    return new ApiError(401, 'unauthenticated', 'Your session has ended. Sign in again.');
  }

  return new ApiError(response.status, 'unexpected', FALLBACK_MESSAGE);
}

export function errorMessage(error: unknown): string {
  return error instanceof ApiError ? error.message : FALLBACK_MESSAGE;
}
