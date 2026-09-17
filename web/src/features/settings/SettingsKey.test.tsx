import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AiKey, Session } from '@/api/schemas';
import demoSession from '@/test/fixtures/session.json';
import settings from '@/test/fixtures/settings.json';
import { deferred, json, mockApi, renderWithApp } from '@/test/utils';
import SettingsPage from './SettingsPage';

afterEach(() => vi.restoreAllMocks());

const signedIn: Session = {
  authenticated: true,
  user: { id: 'u1', name: 'Umais', email: 'umais@example.com', isDemo: false, onboardingCompleted: true },
  capabilities: { googleSignIn: true, gmail: false, ai: true, demo: true },
};

const userKey: AiKey = { hasUserKey: true, hint: '…Ab3x', serverKeyAvailable: false, model: 'gemini-2.5-flash' };
const serverKey: AiKey = { hasUserKey: false, hint: null, serverKeyAvailable: true, model: 'gemini-2.5-flash' };
const noKey: AiKey = { hasUserKey: false, hint: null, serverKeyAvailable: false, model: 'gemini-2.5-flash' };

function setup(initial: AiKey, { session = signedIn, deleteFails = false }: { session?: Session; deleteFails?: boolean } = {}) {
  let key = initial;
  const fetchSpy = mockApi({
    '/api/auth/session': session,
    '/api/settings': settings,
    '/api/ai/key': () => key,
    'PUT /api/ai/key': ({ init }: { init?: RequestInit }) => {
      const { apiKey } = JSON.parse(String(init?.body)) as { apiKey: string };
      key = { ...key, hasUserKey: true, hint: `…${apiKey.slice(-4)}` };
      return key;
    },
    'DELETE /api/ai/key': () => {
      if (deleteFails) return json({ code: 'server_error', message: 'Couldn’t remove the key. Try again.' }, 500);
      key = { ...key, hasUserKey: false, hint: null };
      return new Response(null, { status: 204 });
    },
  });
  renderWithApp(<SettingsPage />, { route: '/settings' });
  const calls = (method: string) => fetchSpy.mock.calls.filter(([input, init]) => String(input) === '/api/ai/key' && (init?.method ?? 'GET') === method);
  return { calls };
}

const aiGroup = () => screen.getByRole('region', { name: 'AI' });

describe('Settings: AI section', () => {
  it('shows the user’s key by its hint, with the model', async () => {
    setup(userKey);
    expect(await screen.findByText('Your key …Ab3x')).toBeInTheDocument();
    expect(within(aiGroup()).getByText('gemini-2.5-flash')).toBeInTheDocument();
    expect(within(aiGroup()).getByRole('button', { name: 'Replace key' })).toBeInTheDocument();
    expect(within(aiGroup()).getByRole('button', { name: 'Remove key' })).toBeInTheDocument();
    // The existing AI toggles live in the same section.
    expect(within(aiGroup()).getByRole('switch', { name: 'AI categorization' })).toBeInTheDocument();
    expect(within(aiGroup()).getByRole('switch', { name: 'AI insights' })).toBeInTheDocument();
  });

  it('says FinSight’s built-in AI is used when only the server has a key', async () => {
    setup(serverKey);
    expect(await screen.findByText('Using FinSight’s built-in AI')).toBeInTheDocument();
    expect(within(aiGroup()).getByRole('button', { name: 'Add key' })).toBeInTheDocument();
    expect(within(aiGroup()).queryByRole('button', { name: 'Remove key' })).not.toBeInTheDocument();
  });

  it('says AI is off when there is no key at all', async () => {
    setup(noKey);
    expect(await screen.findByText('Not set up — AI categorization and insights are off')).toBeInTheDocument();
    expect(within(aiGroup()).getByRole('button', { name: 'Add key' })).toBeInTheDocument();
  });

  it('is hidden for demo accounts, which keep the AI toggles', async () => {
    const { calls } = setup(serverKey, { session: demoSession as Session });
    expect(await screen.findByRole('switch', { name: 'AI categorization' })).toBeInTheDocument();
    expect(screen.queryByText('Gemini API key')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add key' })).not.toBeInTheDocument();
    expect(calls('GET')).toHaveLength(0);
  });

  it('removes the key after a destructive confirmation and shows a toast', async () => {
    const { calls } = setup(userKey);
    await screen.findByText('Your key …Ab3x');

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Remove key' })));
    const dialog = await screen.findByRole('alertdialog', { hidden: true });
    expect(dialog).toHaveAccessibleName('Remove your API key?');
    act(() => fireEvent.click(within(dialog).getByRole('button', { name: 'Remove', hidden: true })));

    await waitFor(() => expect(calls('DELETE')).toHaveLength(1));
    expect(await screen.findByText('Not set up — AI categorization and insights are off')).toBeInTheDocument();
    expect(await screen.findByText('API key removed')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove key' })).not.toBeInTheDocument();
  });

  it('keeps the key when the confirmation is cancelled', async () => {
    const { calls } = setup(userKey);
    await screen.findByText('Your key …Ab3x');
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Remove key' })));
    const dialog = await screen.findByRole('alertdialog', { hidden: true });
    act(() => fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel', hidden: true })));
    await waitFor(() => expect(dialog).not.toHaveAttribute('open'));
    expect(calls('DELETE')).toHaveLength(0);
    expect(screen.getByText('Your key …Ab3x')).toBeInTheDocument();
  });

  it('shows a failed removal inline', async () => {
    setup(userKey, { deleteFails: true });
    await screen.findByText('Your key …Ab3x');
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Remove key' })));
    const dialog = await screen.findByRole('alertdialog', { hidden: true });
    act(() => fireEvent.click(within(dialog).getByRole('button', { name: 'Remove', hidden: true })));

    const alert = await within(aiGroup()).findByRole('alert');
    expect(alert).toHaveTextContent('Couldn’t remove the key. Try again.');
    expect(screen.getByText('Your key …Ab3x')).toBeInTheDocument();
  });

  it('adds a key from a sheet with the guided form, then closes it with a toast', async () => {
    const { calls } = setup(serverKey);
    await screen.findByText('Using FinSight’s built-in AI');

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Add key' })));
    const sheet = screen.getByRole('dialog', { name: 'Add a Gemini API key' });
    expect(sheet).toHaveAttribute('open');
    expect(within(sheet).getByRole('link', { name: /Google AI Studio/ })).toBeInTheDocument();
    act(() => fireEvent.change(within(sheet).getByLabelText('Gemini API key'), { target: { value: 'AIza-new-Q7wz' } }));
    act(() => fireEvent.click(within(sheet).getByRole('button', { name: 'Verify & save' })));

    expect(await screen.findByText('Your key …Q7wz')).toBeInTheDocument();
    expect(calls('PUT')).toHaveLength(1);
    await waitFor(() => expect(sheet).not.toHaveAttribute('open'));
    expect(await screen.findByText('API key saved')).toBeInTheDocument();
  });
});

describe('Settings: loading', () => {
  it('renders every section shell while settings load, and keeps the same shells once loaded', async () => {
    const settingsResponse = deferred<unknown>();
    const keyResponse = deferred<unknown>();
    mockApi({
      '/api/auth/session': signedIn,
      '/api/settings': () => settingsResponse.promise,
      '/api/ai/key': () => keyResponse.promise,
    });
    renderWithApp(<SettingsPage />, { route: '/settings' });

    const names = ['Account', 'AI', 'Preferences', 'Your data'];
    const shells = await waitFor(() => names.map((name) => screen.getByRole('region', { name })));
    // Labels are real from the start; only the controls are placeholders.
    expect(within(shells[2] as HTMLElement).getByText('Currency')).toBeInTheDocument();
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
    expect(screen.getAllByText('Gemini API key')[0]).toBeInTheDocument();

    await act(async () => {
      settingsResponse.resolve(settings);
      keyResponse.resolve(serverKey);
    });

    expect(await screen.findByRole('switch', { name: 'AI insights' })).toBeInTheDocument();
    expect(await screen.findByText('Using FinSight’s built-in AI')).toBeInTheDocument();
    names.forEach((name, i) => expect(screen.getByRole('region', { name })).toBe(shells[i]));
  });
});
