import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { keys, useSession } from '@/api/queries';
import type { Session } from '@/api/schemas';
import { AiInsightCard } from '@/features/insights/AiComponents';
import analysis from '@/test/fixtures/analysis.json';
import session from '@/test/fixtures/session.json';
import { deferred, mockApi, renderWithApp, testQueryClient } from '@/test/utils';
import { WelcomePage } from './WelcomePage';

afterEach(() => vi.restoreAllMocks());

const signedOut: Session = { ...(session as Session), authenticated: false, user: null };

/** The same switch SessionGate makes: welcome screen when signed out, the app when signed in. */
function Gate() {
  const current = useSession();
  if (!current.data) return null;
  return current.data.authenticated ? <p>App shell</p> : <WelcomePage capabilities={current.data.capabilities} />;
}

const countCalls = (spy: ReturnType<typeof mockApi>, method: string, path: string) =>
  spy.mock.calls.filter(([input, init]) => String(input) === path && (init?.method ?? 'GET') === method).length;

describe('Explore with sample data', () => {
  it('starts one demo however fast it is clicked, and stays busy until the app replaces the welcome screen', async () => {
    const demoResponse = deferred<unknown>();
    const fetchSpy = mockApi({ '/api/auth/session': session, 'POST /api/auth/demo': demoResponse.promise });
    const client = testQueryClient();
    client.setQueryData(keys.session, signedOut);
    renderWithApp(<Gate />, { client });

    const button = screen.getByRole('button', { name: 'Explore with sample data' });
    // Several clicks inside one act: all dispatch before React can re-render the button as disabled.
    act(() => {
      fireEvent.click(button);
      fireEvent.click(button);
      fireEvent.click(button);
    });
    act(() => fireEvent.click(button));
    await act(async () => undefined); // mutations start their request on a microtask
    expect(countCalls(fetchSpy, 'POST', '/api/auth/demo')).toBe(1);
    // React Query notifies on the next task.
    await waitFor(() => expect(button).toBeDisabled());
    expect(button).toHaveAttribute('aria-busy', 'true');
    expect(countCalls(fetchSpy, 'POST', '/api/auth/demo')).toBe(1);

    await act(async () => demoResponse.resolve(session.user));
    // Signed in straight from the response: the welcome screen is gone in the same update, never re-enabled.
    expect(await screen.findByText('App shell')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Explore with sample data' })).not.toBeInTheDocument();
    expect(countCalls(fetchSpy, 'POST', '/api/auth/demo')).toBe(1);
  });

  it('re-enables with the server’s message when the demo can’t start', async () => {
    mockApi({ 'POST /api/auth/demo': new Response(JSON.stringify({ code: 'rate_limited', message: 'You’re doing that too often. Wait a moment and try again.' }), { status: 429 }) });
    const client = testQueryClient();
    client.setQueryData(keys.session, signedOut);
    renderWithApp(<Gate />, { client });

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Explore with sample data' })));
    expect(await screen.findByRole('alert')).toHaveTextContent('too often');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Explore with sample data' })).toBeEnabled());
  });
});

describe('Analyze spending', () => {
  it('sends a single analysis request for a double click', async () => {
    const generated = deferred<unknown>();
    const fetchSpy = mockApi({ '/api/analysis': analysis, 'POST /api/analysis/generate': generated.promise });
    renderWithApp(<AiInsightCard period={{ preset: 'last-month' }} hasData />);

    const button = await screen.findByRole('button', { name: 'Analyze spending' });
    act(() => {
      fireEvent.click(button);
      fireEvent.click(button);
    });
    await act(async () => undefined);
    const generateCalls = fetchSpy.mock.calls.filter(([input]) => String(input).startsWith('/api/analysis/generate'));
    expect(generateCalls).toHaveLength(1);
    expect(await screen.findByText(/This takes about 20 seconds/)).toBeInTheDocument();
    expect(fetchSpy.mock.calls.filter(([input]) => String(input).startsWith('/api/analysis/generate'))).toHaveLength(1);
  });
});
