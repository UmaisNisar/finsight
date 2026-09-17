import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { MemoryRouter } from 'react-router';
import { vi } from 'vitest';
import { ConfirmProvider } from '@/app/providers/ConfirmProvider';
import { ToastProvider } from '@/app/providers/ToastProvider';

type Handler = unknown | ((request: { url: URL; init?: RequestInit }) => unknown);

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** A promise with its resolver exposed, for holding a response open to observe loading states. */
export function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

/**
 * Stubs fetch with handlers keyed by "METHOD /path" or "/path" (GET). The longest matching key wins, so
 * "/api/statements/1" can override "/api/statements". A handler may return a body, a Response, or a promise.
 * Unmatched requests fail loudly.
 */
export function mockApi(handlers: Record<string, Handler>) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, 'http://localhost');
    const method = init?.method ?? 'GET';
    const key = Object.keys(handlers)
      .filter((k) => {
        const [m, p] = k.includes(' ') ? k.split(' ') : ['GET', k];
        return m === method && url.pathname.startsWith(p ?? '');
      })
      .sort((a, b) => b.length - a.length)[0];
    if (!key) throw new Error(`Unmocked request: ${method} ${url.pathname}${url.search}`);
    const handler = handlers[key];
    const result = await (typeof handler === 'function' ? handler({ url, init }) : handler);
    return result instanceof Response ? result : json(result);
  });
}

export function testQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity }, mutations: { retry: false } } });
}

/** Renders with the providers pages expect: query client, router, toasts and alerts. */
export function renderWithApp(ui: ReactElement, { route = '/', client = testQueryClient() }: { route?: string; client?: QueryClient } = {}) {
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[route]}>
          <ToastProvider>
            <ConfirmProvider>
              {children}
            </ConfirmProvider>
          </ToastProvider>
        </MemoryRouter>
      </QueryClientProvider>
    );
  }
  return { client, ...render(ui, { wrapper: Wrapper }) };
}
