import { afterEach, describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { ApiError, ERROR_COPY, errorMessage, isChunkLoadError, isSafeMessage, request, REQUEST_TIMEOUT_MS } from './client';
import { api } from './endpoints';

afterEach(() => vi.restoreAllMocks());

function mockFetch(status: number, body: unknown, headers: Record<string, string> = { 'Content-Type': 'application/json' }) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async () => new Response(body === undefined ? null : typeof body === 'string' ? body : JSON.stringify(body), { status, headers }));
}

describe('request', () => {
  it('sends the CSRF header on state-changing requests only', async () => {
    const fetchSpy = mockFetch(200, { ok: true });
    await request('/api/x', z.object({ ok: z.boolean() }), { method: 'POST', body: {} });
    await request('/api/x', z.object({ ok: z.boolean() }));

    const post = fetchSpy.mock.calls[0]?.[1]?.headers as Record<string, string>;
    const get = fetchSpy.mock.calls[1]?.[1]?.headers as Record<string, string>;
    expect(post['X-FinSight-Request']).toBe('1');
    expect(get['X-FinSight-Request']).toBeUndefined();
  });

  it('serialises JSON bodies but leaves FormData for the browser to encode', async () => {
    const fetchSpy = mockFetch(200, { ok: true });
    await request('/api/x', z.object({ ok: z.boolean() }), { method: 'PATCH', body: { merchant: 'Cafe' } });
    const form = new FormData();
    form.append('file', new Blob(['%PDF']), 'statement.pdf');
    await request('/api/x', z.object({ ok: z.boolean() }), { method: 'POST', body: form });

    const [, jsonInit] = fetchSpy.mock.calls[0] ?? [];
    expect((jsonInit?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(jsonInit?.body).toBe('{"merchant":"Cafe"}');
    expect(jsonInit?.credentials).toBe('same-origin');

    const [, formInit] = fetchSpy.mock.calls[1] ?? [];
    expect((formInit?.headers as Record<string, string>)['Content-Type']).toBeUndefined();
    expect(formInit?.body).toBe(form);
  });

  it('surfaces the server message and code for errors', async () => {
    mockFetch(503, { code: 'ai_unavailable', message: 'AI analysis is temporarily unavailable.' });
    await expect(request('/api/x', z.object({}))).rejects.toMatchObject({ code: 'ai_unavailable', status: 503, message: 'AI analysis is temporarily unavailable.' });
  });

  it('explains an expired session even when the 401 has no JSON body', async () => {
    mockFetch(401, '<html>Unauthorized</html>', { 'Content-Type': 'text/html' });
    await expect(request('/api/x', z.object({}))).rejects.toMatchObject({ status: 401, code: 'unauthenticated' });
  });

  it('turns a network failure into a readable error, but lets aborts through', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(new TypeError('Failed to fetch'));
    await expect(request('/api/x', z.object({}))).rejects.toMatchObject({ status: 0, code: 'network' });

    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(new DOMException('Aborted', 'AbortError'));
    await expect(request('/api/x', z.object({}))).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('resolves empty for 204 and for calls with no response schema', async () => {
    mockFetch(204, undefined);
    await expect(request('/api/x', z.object({ never: z.string() }))).resolves.toBeUndefined();
    mockFetch(200, { ignored: true });
    await expect(request('/api/x', null, { method: 'DELETE' })).resolves.toBeUndefined();
  });

  it('rejects responses that do not match the contract', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    mockFetch(200, { amount: 'not a number' });
    const error = await request('/api/x', z.object({ amount: z.number() })).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('invalid_response');
  });
});

describe('errorMessage', () => {
  it('shows API messages and hides unexpected errors behind a generic one', () => {
    expect(errorMessage(new ApiError(400, 'bad', 'Choose a PDF file.'))).toBe('Choose a PDF file.');
    expect(errorMessage(new Error('stack trace details'))).toBe('Something went wrong. Try again.');
  });
});

describe('friendly copy for every failure', () => {
  const failWith = async (status: number, body: unknown, headers?: Record<string, string>) => {
    mockFetch(status, body, headers);
    return errorMessage(await request('/api/x', z.object({})).catch((e: unknown) => e));
  };

  it('maps statuses the server did not explain', async () => {
    expect(await failWith(500, '<html>Internal Server Error</html>', { 'Content-Type': 'text/html' })).toBe(ERROR_COPY.server);
    expect(await failWith(502, undefined)).toBe(ERROR_COPY.server);
    expect(await failWith(504, undefined)).toBe(ERROR_COPY.timeout);
    expect(await failWith(429, undefined)).toBe(ERROR_COPY.rateLimited);
    expect(await failWith(403, undefined)).toBe(ERROR_COPY.forbidden);
    expect(await failWith(404, undefined)).toBe(ERROR_COPY.notFound);
    expect(await failWith(409, undefined)).toBe(ERROR_COPY.conflict);
    expect(await failWith(401, undefined)).toBe(ERROR_COPY.unauthenticated);
    expect(await failWith(400, 'nope', { 'Content-Type': 'text/plain' })).toBe(ERROR_COPY.badRequest);
  });

  it('prefers a safe server message, including for 429 and 5xx', async () => {
    expect(await failWith(429, { code: 'rate_limited', message: 'Too many uploads right now. Try again in a few minutes.' })).toBe('Too many uploads right now. Try again in a few minutes.');
    expect(await failWith(409, { code: 'conflict', message: 'That statement is already being processed.' })).toBe('That statement is already being processed.');
  });

  it('never shows technical server text', async () => {
    const stack = 'System.NullReferenceException: Object reference not set\n   at FinSight.Api.Foo() in Foo.cs:line 12';
    expect(await failWith(500, { code: 'unexpected', message: stack })).toBe(ERROR_COPY.server);
    expect(await failWith(400, { code: 'bad', message: '<b>bad</b>' })).toBe(ERROR_COPY.badRequest);
    expect(isSafeMessage('Choose a PDF file.')).toBe(true);
    expect(isSafeMessage('Exception of type X was thrown.')).toBe(false);
  });

  it('explains network failures, schema mismatches and malformed JSON', async () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(new TypeError('Failed to fetch'));
    expect(errorMessage(await request('/api/x', z.object({})).catch((e: unknown) => e))).toBe(ERROR_COPY.network);

    mockFetch(200, { amount: 'not a number' });
    expect(errorMessage(await request('/api/x', z.object({ amount: z.number() })).catch((e: unknown) => e))).toBe(ERROR_COPY.invalidResponse);
    // The zod issues are logged for developers (dev and test builds only), never shown.
    expect(consoleError).toHaveBeenCalledWith('Unexpected response from /api/x', expect.arrayContaining([expect.objectContaining({ path: ['amount'] })]));

    mockFetch(200, '{"amount": 1', { 'Content-Type': 'application/json' });
    expect(errorMessage(await request('/api/x', z.object({ amount: z.number() })).catch((e: unknown) => e))).toBe(ERROR_COPY.invalidResponse);
  });

  it('maps errors thrown outside the client', () => {
    expect(errorMessage(new TypeError('Failed to fetch'))).toBe(ERROR_COPY.network);
    expect(errorMessage(new TypeError('NetworkError when attempting to fetch resource.'))).toBe(ERROR_COPY.network);
    expect(errorMessage(new TypeError('Failed to fetch dynamically imported module: /assets/Page-abc.js'))).toBe(ERROR_COPY.updated);
    expect(errorMessage(new DOMException('signal timed out', 'TimeoutError'))).toBe(ERROR_COPY.timeout);
    expect(errorMessage(z.object({ a: z.number() }).safeParse({}).error)).toBe(ERROR_COPY.invalidResponse);
    expect(errorMessage(new TypeError("Cannot read properties of undefined (reading 'map')"))).toBe(ERROR_COPY.fallback);
    expect(errorMessage('a string')).toBe(ERROR_COPY.fallback);
  });

  it('recognises chunk load failures from each browser', () => {
    expect(isChunkLoadError(new TypeError('Failed to fetch dynamically imported module: http://x/a.js'))).toBe(true);
    expect(isChunkLoadError(new TypeError('error loading dynamically imported module: http://x/a.js'))).toBe(true);
    expect(isChunkLoadError(new TypeError('Importing a module script failed.'))).toBe(true);
    expect(isChunkLoadError(Object.assign(new Error('Loading chunk 12 failed.'), { name: 'ChunkLoadError' }))).toBe(true);
    expect(isChunkLoadError(new Error('Something else'))).toBe(false);
  });
});

describe('request timeout', () => {
  /** A fetch that never answers, but rejects like the browser when its signal aborts. */
  function hangingFetch() {
    return vi.spyOn(globalThis, 'fetch').mockImplementation(
      (_input, init) =>
        new Promise<Response>((_resolve, reject) => {
          const signal = init?.signal;
          signal?.addEventListener('abort', () => reject(signal.reason instanceof DOMException ? signal.reason : new DOMException('The operation was aborted.', 'AbortError')));
        }),
    );
  }

  afterEach(() => vi.useRealTimers());

  it('fails a request that hangs with a friendly timeout error after 30 seconds', async () => {
    vi.useFakeTimers();
    hangingFetch();
    const result = request('/api/x', z.object({})).catch((e: unknown) => e);
    await vi.advanceTimersByTimeAsync(REQUEST_TIMEOUT_MS - 1);
    let settled = false;
    void result.then(() => (settled = true));
    await Promise.resolve();
    expect(settled).toBe(false);

    await vi.advanceTimersByTimeAsync(1);
    const error = await result;
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 0, code: 'timeout', message: 'This is taking longer than usual. Try again.' });
  });

  it('honours a per-call timeout, and a caller abort still passes through as an AbortError', async () => {
    vi.useFakeTimers();
    hangingFetch();
    const quick = request('/api/x', z.object({}), { timeoutMs: 1_000 }).catch((e: unknown) => e);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(await quick).toMatchObject({ code: 'timeout' });

    const controller = new AbortController();
    const cancelled = request('/api/x', z.object({}), { signal: controller.signal }).catch((e: unknown) => e);
    controller.abort();
    expect(await cancelled).toMatchObject({ name: 'AbortError' });
  });

  it('gives uploads and analysis generation longer', async () => {
    vi.useFakeTimers();
    hangingFetch();
    const form = new FormData();
    const upload = request('/api/uploads/statements', z.object({}), { method: 'POST', body: form }).catch((e: unknown) => e);
    const generate = api.generateAnalysis({ preset: 'last-month' }).catch((e: unknown) => e);
    const states = { upload: false, generate: false };
    void upload.then(() => (states.upload = true));
    void generate.then(() => (states.generate = true));
    await vi.advanceTimersByTimeAsync(REQUEST_TIMEOUT_MS);
    expect(states).toEqual({ upload: false, generate: false });
    await vi.advanceTimersByTimeAsync(150_000);
    expect(await upload).toMatchObject({ code: 'timeout' });
    expect(await generate).toMatchObject({ code: 'timeout' });
  });

  it('times out while reading a response body that stalls', async () => {
    vi.useFakeTimers();
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (_input, init) => {
      const stream = new ReadableStream({
        start(controller) {
          init?.signal?.addEventListener('abort', () => controller.error(new DOMException('The operation was aborted.', 'AbortError')));
        },
      });
      return new Response(stream, { status: 200, headers: { 'Content-Type': 'application/json' } });
    });
    const result = request('/api/x', z.object({})).catch((e: unknown) => e);
    await vi.advanceTimersByTimeAsync(REQUEST_TIMEOUT_MS);
    expect(await result).toMatchObject({ code: 'timeout' });
  });
});
