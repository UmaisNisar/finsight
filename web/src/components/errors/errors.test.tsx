import { QueryClientProvider, useMutation, type QueryClient } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { createMemoryRouter, MemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, ERROR_COPY } from '@/api/client';
import { keys, useSignOut } from '@/api/queries';
import type { Session } from '@/api/schemas';
import { SessionGate } from '@/app/App';
import { ToastProvider } from '@/app/providers/ToastProvider';
import { createQueryClient } from '@/app/queryClient';
import { GeminiKeyForm } from '@/features/ai/GeminiKeyForm';
import session from '@/test/fixtures/session.json';
import { json, mockApi, renderWithApp } from '@/test/utils';
import { GlobalErrorListener } from './GlobalErrorListener';
import { OfflineIndicator } from './OfflineIndicator';
import { sessionNotice } from './sessionNotice';
import { WidgetBoundary } from './WidgetBoundary';

beforeEach(() => {
  vi.spyOn(console, 'error').mockImplementation(() => undefined);
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  sessionNotice.clear();
});

function withProviders(ui: ReactNode, client: QueryClient, router = true) {
  const content = <ToastProvider banner={<OfflineIndicator />}>{ui}</ToastProvider>;
  return <QueryClientProvider client={client}>{router ? <MemoryRouter>{content}</MemoryRouter> : content}</QueryClientProvider>;
}

describe('WidgetBoundary', () => {
  it('keeps the card, shows a short message, and Try again re-renders and refetches', async () => {
    let broken = true;
    function Chart() {
      if (broken) throw new Error('Recharts exploded');
      return <p>Chart drawn</p>;
    }
    const client = createQueryClient();
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    render(
      withProviders(
        <>
          <section className="card" aria-label="Cash flow">
            <h2>Cash flow</h2>
            <WidgetBoundary name="test-chart" message="The chart couldn’t be shown." queryKeys={[['summary']]} minHeight={288}>
              <Chart />
            </WidgetBoundary>
          </section>
          <p>Rest of the page</p>
        </>,
        client,
      ),
    );

    const card = screen.getByRole('region', { name: 'Cash flow' });
    const alert = screen.getByRole('alert');
    expect(card).toContainElement(alert);
    expect(alert).toHaveTextContent('The chart couldn’t be shown.');
    expect(alert).toHaveStyle({ minHeight: '288px' });
    expect(screen.getByText('Rest of the page')).toBeInTheDocument();
    expect(screen.queryByText(/Recharts exploded/)).not.toBeInTheDocument();

    broken = false;
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Chart drawn')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Cash flow' })).toBe(card);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['summary'] });
  });

  it('wraps its fallback in the widget’s own shell when given one', () => {
    function Broken(): never {
      throw new Error('boom');
    }
    render(
      withProviders(
        <WidgetBoundary name="ai" message="This insight couldn’t be shown." shell={(fallback) => <section aria-label="Insight" className="card">{fallback}</section>}>
          <Broken />
        </WidgetBoundary>,
        createQueryClient(),
      ),
    );
    expect(screen.getByRole('region', { name: 'Insight' })).toHaveTextContent('This insight couldn’t be shown.');
  });
});

describe('signed-out notice', () => {
  const signedIn = session as Session;
  const signedOut: Session = { ...signedIn, authenticated: false, user: null };

  it('says the session ended once, only after it ended mid-use', async () => {
    let current: Session = signedOut;
    mockApi({
      '/api/auth/session': () => current,
      '/api/x': () => json({ code: 'unauthenticated', message: 'Sign in.' }, 401),
    });
    const client = createQueryClient();
    const notice = () => screen.queryAllByText('You were signed out. Sign in again to continue.');

    // A first visit while signed out: no notice.
    // SessionGate renders ScrollRestoration, which needs a data router.
    const router = createMemoryRouter([{ element: <SessionGate />, children: [{ path: '*', element: <p>App</p> }] }]);
    render(withProviders(<RouterProvider router={router} />, client, false));
    expect(await screen.findByRole('button', { name: 'Explore with sample data' })).toBeInTheDocument();
    expect(notice()).toHaveLength(0);

    // Signed in, then any request gets a 401: the welcome screen returns and explains why.
    current = signedIn;
    await act(() => client.refetchQueries({ queryKey: keys.session }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Explore with sample data' })).not.toBeInTheDocument());
    current = signedOut;
    const { request } = await import('@/api/client');
    const { z } = await import('zod');
    await act(async () => {
      await Promise.all([1, 2, 3].map((n) => client.fetchQuery({ queryKey: ['x', n], queryFn: () => request('/api/x', z.object({})) }).catch(() => undefined)));
    });
    await waitFor(() => expect(notice()).toHaveLength(1));

    // Signing in again retires it; choosing Sign out later shows the welcome screen without it.
    current = signedIn;
    await act(() => client.refetchQueries({ queryKey: keys.session }));
    await waitFor(() => expect(notice()).toHaveLength(0));
    expect(sessionNotice.isExpired()).toBe(false);
    current = signedOut;
    await act(() => client.refetchQueries({ queryKey: keys.session }));
    expect(await screen.findByRole('button', { name: 'Explore with sample data' })).toBeInTheDocument();
    expect(notice()).toHaveLength(0);
  });
});

describe('offline capsule', () => {
  it('appears while offline, leaves when back online, and refetches what is on screen', async () => {
    let online = true;
    vi.spyOn(navigator, 'onLine', 'get').mockImplementation(() => online);
    const client = createQueryClient();
    const refetch = vi.spyOn(client, 'refetchQueries').mockResolvedValue();
    render(withProviders(<p>App</p>, client));

    expect(screen.queryByText('You’re offline')).not.toBeInTheDocument();
    act(() => {
      online = false;
      window.dispatchEvent(new Event('offline'));
    });
    expect(await screen.findByRole('status')).toHaveTextContent('You’re offline');
    expect(refetch).not.toHaveBeenCalled();

    act(() => {
      online = true;
      window.dispatchEvent(new Event('online'));
    });
    await waitFor(() => expect(screen.queryByText('You’re offline')).not.toBeInTheDocument());
    expect(refetch).toHaveBeenCalledTimes(1);
    expect(refetch).toHaveBeenCalledWith({ type: 'active' }, { cancelRefetch: false });
  });
});

describe('mutation errors', () => {
  const fail = () => Promise.reject(new ApiError(500, 'server_error', ERROR_COPY.server));

  function Inline() {
    const save = useMutation({ mutationFn: fail });
    return (
      <div>
        <button type="button" onClick={() => save.mutate()}>
          Save inline
        </button>
        {save.isError && <p>{(save.error as Error).message}</p>}
      </div>
    );
  }

  function Silent() {
    const save = useMutation({ mutationFn: fail, meta: { errorToast: true } });
    return (
      <button type="button" onClick={() => save.mutate()}>
        Save silently
      </button>
    );
  }

  function SignOut() {
    const signOut = useSignOut();
    return (
      <button type="button" onClick={() => signOut.mutate()}>
        Sign out
      </button>
    );
  }

  it('toasts failures that have no inline error UI, and only those', async () => {
    mockApi({ 'POST /api/auth/logout': () => json({ code: 'x', message: 'Couldn’t sign out. Try again.' }, 500) });
    render(
      withProviders(
        <>
          <Inline />
          <Silent />
          <SignOut />
        </>,
        createQueryClient(),
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Save inline' }));
    expect(await screen.findByText(ERROR_COPY.server)).toBeInTheDocument();
    // Shown inline only: no toast repeating it.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getAllByText(ERROR_COPY.server)).toHaveLength(1);

    fireEvent.click(screen.getByRole('button', { name: 'Save silently' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(ERROR_COPY.server);

    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await waitFor(() => expect(screen.getAllByRole('alert').map((a) => a.textContent)).toContain('Couldn’t sign out. Try again.'));
  });

  it('does not toast a 401, which returns to the welcome screen instead', async () => {
    function Expired() {
      const save = useMutation({ mutationFn: () => Promise.reject(new ApiError(401, 'unauthenticated', ERROR_COPY.unauthenticated)), meta: { errorToast: true } });
      return (
        <button type="button" onClick={() => save.mutate()}>
          Save
        </button>
      );
    }
    mockApi({ '/api/auth/session': session });
    render(withProviders(<Expired />, createQueryClient()));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('re-enables a form whose request hung, and explains the timeout', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.spyOn(globalThis, 'fetch').mockImplementation(
      (_input, init) =>
        new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () => reject(new DOMException('The operation was aborted.', 'AbortError')));
        }),
    );
    renderWithApp(<GeminiKeyForm />, { client: createQueryClient() });

    fireEvent.change(screen.getByLabelText('Gemini API key'), { target: { value: 'AIzaSyExampleKey1234' } });
    fireEvent.click(screen.getByRole('button', { name: 'Verify & save' }));
    expect(await screen.findByRole('button', { name: 'Verifying…' })).toBeDisabled();

    await act(() => vi.advanceTimersByTimeAsync(30_000));
    const button = await screen.findByRole('button', { name: 'Verify & save' });
    expect(button).toBeEnabled();
    expect(screen.getByRole('alert')).toHaveTextContent('This is taking longer than usual. Try again.');

    // A second attempt isn't swallowed by the single-flight guard.
    fireEvent.click(button);
    expect(await screen.findByRole('button', { name: 'Verifying…' })).toBeDisabled();
  });
});

describe('global error listener', () => {
  it('shows one calm toast for unexpected failures and ignores noise', async () => {
    render(withProviders(<GlobalErrorListener />, createQueryClient()));
    const reject = (reason: unknown) => act(() => void window.dispatchEvent(Object.assign(new Event('unhandledrejection'), { reason })));

    reject(new ApiError(500, 'server_error', ERROR_COPY.server));
    reject(new DOMException('Aborted', 'AbortError'));
    act(() => void window.dispatchEvent(new ErrorEvent('error', { message: 'ResizeObserver loop completed with undelivered notifications.' })));
    const fromExtension = new Error('boom');
    fromExtension.stack = 'Error: boom\n    at chrome-extension://abc/content.js:1:1';
    reject(fromExtension);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    reject(new TypeError("Cannot read properties of undefined (reading 'x')"));
    act(() => void window.dispatchEvent(new ErrorEvent('error', { error: new Error('Handler threw'), message: 'Handler threw' })));
    reject(new Error('Another one'));
    await waitFor(() => expect(screen.getAllByRole('alert')).toHaveLength(1));
    expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong. If anything looks off, reload the page.');
    expect(screen.queryByText(/Cannot read properties/)).not.toBeInTheDocument();
  });
});
