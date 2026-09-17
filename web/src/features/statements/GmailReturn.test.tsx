import { screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Session } from '@/api/schemas';
import settings from '@/test/fixtures/settings.json';
import { mockApi, renderWithApp } from '@/test/utils';
import StatementsPage from './StatementsPage';

afterEach(() => vi.restoreAllMocks());

const session: Session = {
  authenticated: true,
  user: { id: 'u1', name: 'Umais', email: 'umais@example.com', isDemo: false, onboardingCompleted: true },
  capabilities: { googleSignIn: true, gmail: true, ai: true, demo: true },
};

describe('Statements: returning from Google consent', () => {
  it('announces the connection once and starts a scan', async () => {
    const fetchSpy = mockApi({
      '/api/auth/session': session,
      '/api/settings': settings,
      '/api/gmail': { connected: true, email: 'sam.rivera@gmail.com', status: 'active', connectedAt: null, lastSyncedAt: null },
      '/api/statements': [],
      'POST /api/statements/sync': { jobId: 'job-1' },
    });
    renderWithApp(<StatementsPage />, { route: '/statements?gmail=connected' });

    expect(await screen.findByText('Gmail connected. Looking for statements…')).toBeInTheDocument();
    const syncs = () => fetchSpy.mock.calls.filter(([input, init]) => String(input) === '/api/statements/sync' && init?.method === 'POST');
    await waitFor(() => expect(syncs()).toHaveLength(1));
  });

  it('explains a denied consent', async () => {
    mockApi({ '/api/auth/session': session, '/api/settings': settings, '/api/gmail': { connected: false, email: null, status: null, connectedAt: null, lastSyncedAt: null }, '/api/statements': [] });
    renderWithApp(<StatementsPage />, { route: '/statements?gmail=denied' });
    expect(await screen.findByRole('alert')).toHaveTextContent('needs permission to read Gmail');
    expect(await screen.findByRole('link', { name: 'Connect Gmail' })).toHaveAttribute('href', '/api/gmail/connect?returnTo=%2Fstatements');
  });
});
