import { afterEach, describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { ApiError, request } from './client';

afterEach(() => vi.restoreAllMocks());

function mockFetch(status: number, body: unknown) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async () => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));
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

  it('surfaces the server message and code for errors', async () => {
    mockFetch(503, { code: 'ai_unavailable', message: 'AI analysis is temporarily unavailable.' });
    await expect(request('/api/x', z.object({}))).rejects.toMatchObject({ code: 'ai_unavailable', status: 503, message: 'AI analysis is temporarily unavailable.' });
  });

  it('rejects responses that do not match the contract', async () => {
    mockFetch(200, { amount: 'not a number' });
    const error = await request('/api/x', z.object({ amount: z.number() })).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('invalid_response');
  });
});
