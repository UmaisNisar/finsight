import { QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider, type RouteObject } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ERROR_COPY } from '@/api/client';
import { claimAutoReload, reloadPage, AUTO_RELOAD_WINDOW_MS } from '@/components/errors/chunkReload';
import { PageOutlet, RootRouteError, ShellRouteError } from '@/components/errors/RouteErrors';
import analysis from '@/test/fixtures/analysis.json';
import recurring from '@/test/fixtures/recurring.json';
import session from '@/test/fixtures/session.json';
import settings from '@/test/fixtures/settings.json';
import statements from '@/test/fixtures/statements.json';
import summary from '@/test/fixtures/summary.json';
import { mockApi, testQueryClient } from '@/test/utils';
import { routes } from './App';
import { ConfirmProvider } from './providers/ConfirmProvider';
import { ToastProvider } from './providers/ToastProvider';

vi.mock('@/components/errors/chunkReload', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/components/errors/chunkReload')>()),
  reloadPage: vi.fn(),
}));

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(600);
  vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(256);
  // Render errors are logged by React and the boundaries in development; keep test output readable.
  vi.spyOn(console, 'error').mockImplementation(() => undefined);
  vi.spyOn(console, 'warn').mockImplementation(() => undefined);
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.mocked(reloadPage).mockClear();
});

function renderRoutes(routeObjects: RouteObject[], initialEntry: string) {
  const router = createMemoryRouter(routeObjects, { initialEntries: [initialEntry] });
  const client = testQueryClient();
  render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <ConfirmProvider>
          <RouterProvider router={router} />
        </ConfirmProvider>
      </ToastProvider>
    </QueryClientProvider>,
  );
  return { router, client };
}

function mockApp() {
  return mockApi({
    '/api/auth/session': session,
    '/api/settings': settings,
    '/api/summary': summary,
    '/api/analysis': analysis,
    '/api/recurring': recurring,
    '/api/statements': statements,
  });
}

const noDeveloperScreen = () => expect(document.body.textContent).not.toMatch(/Hey developer|errorElement|ErrorBoundary/);

describe('root error screen', () => {
  it('replaces a render error outside the shell with a calm full-page screen', async () => {
    mockApp();
    renderRoutes(routes, '/?__crash=root');

    expect(await screen.findByRole('heading', { name: 'Something went wrong' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to Overview' })).toHaveAttribute('href', '/');
    noDeveloperScreen();

    // Details are collapsed, and in development hold the error itself.
    expect(screen.queryByText(/Test crash/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Details' }));
    expect(await screen.findByText(/Test crash \(\?__crash=root\)/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Reload' }));
    expect(reloadPage).toHaveBeenCalledTimes(1);
  });

  it('shows a 404 route error response as a missing page, not a crash', async () => {
    renderRoutes([{ path: '/', element: <p>Home</p>, errorElement: <RootRouteError /> }], '/nowhere');
    expect(await screen.findByRole('heading', { name: 'This page doesn’t exist' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reload' })).not.toBeInTheDocument();
    noDeveloperScreen();
  });
});

describe('page errors inside the shell', () => {
  it('keeps the navigation usable and clears the error when another screen is opened', async () => {
    mockApp();
    const { router } = renderRoutes(routes, '/?__crash=1');

    expect(await screen.findByRole('heading', { name: 'This page couldn’t load' })).toBeInTheDocument();
    noDeveloperScreen();
    // The shell is still there around the error card.
    const navs = screen.getAllByRole('navigation', { name: 'Main' });
    expect(navs.length).toBe(2);

    fireEvent.click(within(navs[0] as HTMLElement).getByRole('link', { name: 'Recurring' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/recurring'));
    expect(await screen.findByRole('heading', { level: 1, name: 'Recurring' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'This page couldn’t load' })).not.toBeInTheDocument();
  });

  it('Try again renders the screen afresh once the problem is gone', async () => {
    let broken = true;
    function Flaky() {
      if (broken) throw new Error('Flaky page');
      return <h1>Recovered</h1>;
    }
    const { client } = renderRoutes([{ element: <PageOutlet />, errorElement: <ShellRouteError />, children: [{ path: '/', element: <Flaky /> }] }], '/');
    const refetch = vi.spyOn(client, 'refetchQueries');

    expect(await screen.findByRole('heading', { name: 'This page couldn’t load' })).toBeInTheDocument();
    broken = false;
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'Recovered' })).toBeInTheDocument();
    expect(refetch).toHaveBeenCalled();
  });

  it('shows a route 404 thrown by a screen as a missing page with a way home', async () => {
    renderRoutes(
      [
        {
          errorElement: <ShellRouteError />,
          children: [
            {
              path: '/',
              loader: () => {
                throw new Response('', { status: 404, statusText: 'Not Found' });
              },
              element: <p>Never</p>,
            },
          ],
        },
      ],
      '/',
    );
    expect(await screen.findByRole('heading', { name: 'This page doesn’t exist' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to Overview' })).toBeInTheDocument();
    expect(screen.queryByText(/404|Not Found/)).not.toBeInTheDocument();
  });
});

describe('code that failed to load after an update', () => {
  it('shows the update message, reloads once by itself, and not again straight away', async () => {
    mockApp();
    renderRoutes(routes, '/?__crash=chunk');

    expect(await screen.findByRole('heading', { name: 'FinSight was updated' })).toBeInTheDocument();
    expect(screen.getByText('Reload to get the latest version.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
    // The sidebar stays, since only the screen's code failed.
    expect(screen.getAllByRole('navigation', { name: 'Main' }).length).toBe(2);
    await waitFor(() => expect(reloadPage).toHaveBeenCalledTimes(1));

    // The reload didn't help (same session, moments later): the screen stays, with no second automatic reload.
    act(() => screen.getByRole('button', { name: 'Reload' }).blur());
    vi.mocked(reloadPage).mockClear();
    renderRoutes([{ path: '/', element: <ThrowChunk />, errorElement: <RootRouteError /> }], '/');
    expect(await screen.findAllByRole('heading', { name: 'FinSight was updated' })).toHaveLength(2);
    expect(reloadPage).not.toHaveBeenCalled();
    expect(ERROR_COPY.updated).toBe('FinSight was updated. Reload to get the latest version.');
  });

  it('allows one automatic reload per minute', () => {
    const start = 1_000_000;
    expect(claimAutoReload(start)).toBe(true);
    expect(claimAutoReload(start + 5_000)).toBe(false);
    expect(claimAutoReload(start + AUTO_RELOAD_WINDOW_MS + 1)).toBe(true);
  });
});

function ThrowChunk(): never {
  throw new TypeError('Failed to fetch dynamically imported module: http://localhost/assets/InsightsPage-abc.js');
}
