import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { GmailConnection, Session, Settings } from '@/api/schemas';
import demoSession from '@/test/fixtures/session.json';
import settingsFixture from '@/test/fixtures/settings.json';
import { json, mockApi, renderWithApp } from '@/test/utils';
import SettingsPage from './SettingsPage';

afterEach(() => vi.restoreAllMocks());

const signedIn: Session = {
  authenticated: true,
  user: { id: 'u1', name: 'Umais', email: 'umais@example.com', isDemo: false, onboardingCompleted: true },
  capabilities: { googleSignIn: true, gmail: true, ai: true, demo: false },
};

const connected: GmailConnection = { connected: true, email: 'umais@example.com', status: 'active', connectedAt: '2026-09-01T00:00:00Z', lastSyncedAt: null };
const notConnected: GmailConnection = { connected: false, email: null, status: null, connectedAt: null, lastSyncedAt: null };

function setup({
  settings = {},
  gmail = connected,
  session = signedIn,
  putFails = false,
}: { settings?: Partial<Settings>; gmail?: GmailConnection; session?: Session; putFails?: boolean } = {}) {
  let current: Settings = { ...(settingsFixture as Settings), ...settings };
  const fetchSpy = mockApi({
    '/api/auth/session': session,
    '/api/settings': () => current,
    '/api/gmail': gmail,
    '/api/ai/key': { hasUserKey: false, hint: null, serverKeyAvailable: true, model: 'gemini-2.5-flash' },
    'PUT /api/settings': ({ init }: { init?: RequestInit }) => {
      if (putFails) return json({ code: 'gmail_auth_expired', message: 'Your Gmail connection expired. Reconnect Gmail to continue.' }, 409);
      const body = JSON.parse(String(init?.body)) as Settings;
      current = { ...body, autoImportEnabled: body.autoScanEnabled && body.autoImportEnabled, emailConfigured: current.emailConfigured };
      return current;
    },
    'POST /api/settings/digest/test': { sent: true },
  });
  renderWithApp(<SettingsPage />, { route: '/settings' });
  const calls = (method: string, path: string) => fetchSpy.mock.calls.filter(([input, init]) => String(input) === path && (init?.method ?? 'GET') === method);
  return { calls };
}

const automation = () => screen.getByRole('region', { name: 'Automation' });

describe('Settings: Automation', () => {
  it('replaces the old “coming soon” notifications row', async () => {
    setup();
    await within(await screen.findByRole('region', { name: 'Automation' })).findByRole('switch', { name: 'Monthly summary email' });
    expect(screen.queryByText('Email notifications')).not.toBeInTheDocument();
    expect(screen.queryByText(/coming soon/)).not.toBeInTheDocument();
  });

  it('offers automatic scans only when Gmail is connected', async () => {
    setup({ gmail: notConnected });
    const group = await screen.findByRole('region', { name: 'Automation' });
    await within(group).findByRole('switch', { name: 'Monthly summary email' });
    await waitFor(() => expect(within(group).queryByRole('switch', { name: 'Scan Gmail automatically' })).not.toBeInTheDocument());
  });

  it('shows the import switch only while automatic scans are on, and turning scans on reveals it', async () => {
    const { calls } = setup();
    const scan = await within(await screen.findByRole('region', { name: 'Automation' })).findByRole('switch', { name: 'Scan Gmail automatically' });
    expect(within(automation()).getByText('Checks daily for new statements. Nothing is imported without you.')).toBeInTheDocument();
    expect(within(automation()).queryByRole('switch', { name: 'Import from banks you’ve used before' })).not.toBeInTheDocument();

    act(() => fireEvent.click(scan));

    const importSwitch = await within(automation()).findByRole('switch', { name: 'Import from banks you’ve used before' });
    expect(importSwitch).toHaveAttribute('aria-checked', 'false');
    await waitFor(() => expect(calls('PUT', '/api/settings')).toHaveLength(1));
    const sent = JSON.parse(String(calls('PUT', '/api/settings')[0]?.[1]?.body)) as Settings;
    expect(sent.autoScanEnabled).toBe(true);
    expect(sent.autoImportEnabled).toBe(false);
  });

  it('hides the import switch again when scans are turned off', async () => {
    setup({ settings: { autoScanEnabled: true, autoImportEnabled: true } });
    const importSwitch = await within(await screen.findByRole('region', { name: 'Automation' })).findByRole('switch', { name: 'Import from banks you’ve used before' });
    expect(importSwitch).toHaveAttribute('aria-checked', 'true');

    act(() => fireEvent.click(within(automation()).getByRole('switch', { name: 'Scan Gmail automatically' })));

    await waitFor(() => expect(within(automation()).queryByRole('switch', { name: 'Import from banks you’ve used before' })).not.toBeInTheDocument());
  });

  it('shows where summaries go and sends a test with a toast', async () => {
    const { calls } = setup();
    const group = await screen.findByRole('region', { name: 'Automation' });
    expect(await within(group).findByText('To umais@example.com')).toBeInTheDocument();

    act(() => fireEvent.click(within(group).getByRole('button', { name: 'Send a test' })));

    expect(await screen.findByText('Test email sent')).toBeInTheDocument();
    expect(calls('POST', '/api/settings/digest/test')).toHaveLength(1);
  });

  it('disables the summary email with an inline explanation when the server has no email', async () => {
    setup({ settings: { emailConfigured: false } });
    const group = await screen.findByRole('region', { name: 'Automation' });
    const digest = await within(group).findByRole('switch', { name: 'Monthly summary email' });
    expect(digest).toBeDisabled();
    expect(within(group).getByText('Email isn’t set up on this server, so summaries can’t be sent.')).toBeInTheDocument();
    expect(within(group).queryByRole('button', { name: 'Send a test' })).not.toBeInTheDocument();
  });

  it('says scans are paused while Gmail access has expired', async () => {
    setup({ gmail: { ...connected, status: 'expired' } });
    const group = await screen.findByRole('region', { name: 'Automation' });
    expect(await within(group).findByText('Paused until you reconnect Gmail')).toBeInTheDocument();
    expect(within(group).getByRole('switch', { name: 'Scan Gmail automatically' })).toBeDisabled();
  });

  it('shows a refused change inline and puts the switch back', async () => {
    setup({ putFails: true });
    const group = await screen.findByRole('region', { name: 'Automation' });
    const scan = await within(group).findByRole('switch', { name: 'Scan Gmail automatically' });

    act(() => fireEvent.click(scan));

    expect(await screen.findByRole('alert')).toHaveTextContent('Your Gmail connection expired.');
    await waitFor(() => expect(within(group).getByRole('switch', { name: 'Scan Gmail automatically' })).toHaveAttribute('aria-checked', 'false'));
  });

  it('is absent for demo accounts', async () => {
    setup({ session: demoSession as Session });
    await screen.findByRole('switch', { name: 'AI insights' });
    expect(screen.queryByRole('region', { name: 'Automation' })).not.toBeInTheDocument();
    expect(screen.queryByRole('switch', { name: 'Monthly summary email' })).not.toBeInTheDocument();
  });
});
