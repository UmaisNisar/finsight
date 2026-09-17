import { act, screen, waitFor, within } from '@testing-library/react';
import { keys } from '@/api/queries';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import InsightsPage from '@/features/insights/InsightsPage';
import OverviewPage from '@/features/overview/OverviewPage';
import RecurringPage from '@/features/recurring/RecurringPage';
import TransactionsPage from '@/features/transactions/TransactionsPage';
import analysis from '@/test/fixtures/analysis.json';
import categories from '@/test/fixtures/categories.json';
import recurring from '@/test/fixtures/recurring.json';
import session from '@/test/fixtures/session.json';
import settings from '@/test/fixtures/settings.json';
import summary from '@/test/fixtures/summary.json';
import transactions from '@/test/fixtures/transactions.json';
import { deferred, mockApi, renderWithApp, testQueryClient } from '@/test/utils';

/*
  Loading behaviour that keeps pages still: cards render their final shell immediately, show placeholders inside
  it, and swap in content without the shell being replaced (so nothing below moves or flashes).
*/

beforeEach(() => {
  // Recharts measures its container; give it a size in jsdom.
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(600);
  vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(256);
});
afterEach(() => vi.restoreAllMocks());

const card = (titleId: string) => document.querySelector(`[aria-labelledby="${titleId}"]`) as HTMLElement | null;

describe('Overview', () => {
  it('shows every card shell while loading, then fills the same shells', async () => {
    const summaryResponse = deferred<unknown>();
    mockApi({
      '/api/auth/session': session,
      '/api/settings': settings,
      '/api/analysis': analysis,
      // The first summary request is the selected period; later ones are the cash-flow trend.
      '/api/summary': ({ url }: { url: URL }) => (url.searchParams.get('period') === 'last-month' ? summaryResponse.promise : summary),
    });
    renderWithApp(<OverviewPage />, { route: '/?period=last-month' });

    const hero = await waitFor(() => {
      const el = card('summary-title');
      expect(el).not.toBeNull();
      return el as HTMLElement;
    });
    const cashFlow = card('cashflow-title');
    const breakdown = card('breakdown-title');
    const largest = card('largest-title');
    expect(hero).toHaveAttribute('aria-busy', 'true');
    expect(largest).toHaveAttribute('aria-busy', 'true');
    // Labels are real text from the start; only values are placeholders.
    expect(within(hero).getByText('Income')).toBeInTheDocument();
    expect(screen.queryByText(/\$6,017/)).not.toBeInTheDocument();

    await act(async () => summaryResponse.resolve(summary));

    await waitFor(() => expect(card('summary-title')).toHaveAttribute('aria-busy', 'false'));
    expect(screen.getByText(/\$6,017/)).toBeInTheDocument();
    // Same DOM nodes: the shells were filled, not replaced.
    expect(card('summary-title')).toBe(hero);
    expect(card('cashflow-title')).toBe(cashFlow);
    expect(card('breakdown-title')).toBe(breakdown);
    expect(card('largest-title')).toBe(largest);
    expect(card('largest-title')).toHaveAttribute('aria-busy', 'false');
  });
});

describe('Recurring', () => {
  it('keeps the totals card in place from loading to loaded', async () => {
    const response = deferred<unknown>();
    mockApi({ '/api/auth/session': session, '/api/settings': settings, '/api/recurring': response.promise });
    renderWithApp(<RecurringPage />, { route: '/recurring' });

    const totals = screen.getByRole('region', { name: 'Recurring totals' });
    expect(totals).toHaveAttribute('aria-busy', 'true');

    await act(async () => response.resolve(recurring));
    await waitFor(() => expect(totals).toHaveAttribute('aria-busy', 'false'));
    expect(screen.getByRole('region', { name: 'Recurring totals' })).toBe(totals);
    expect(screen.getByRole('heading', { name: 'Subscriptions', level: 2 })).toBeInTheDocument();
  });
});

describe('Transactions', () => {
  it('waits for the period’s dates instead of fetching an unfiltered list, and reserves the totals line', async () => {
    const summaryResponse = deferred<unknown>();
    const fetchSpy = mockApi({
      '/api/auth/session': session,
      '/api/settings': settings,
      '/api/categories': categories,
      '/api/summary': summaryResponse.promise,
      '/api/transactions': transactions,
    });
    renderWithApp(<TransactionsPage />, { route: '/transactions?period=last-month' });

    const requested = () => fetchSpy.mock.calls.map(([input]) => String(input)).filter((url) => url.startsWith('/api/transactions'));
    await act(async () => undefined);
    expect(requested()).toEqual([]);
    const totalsLine = document.querySelector('p[aria-live="polite"].caption') as HTMLElement;
    expect(totalsLine).toBeInTheDocument();

    await act(async () => summaryResponse.resolve(summary));
    await waitFor(() => expect(requested()).toHaveLength(1));
    expect(requested()[0]).toContain('from=2026-08-01');
    expect(requested()[0]).toContain('to=2026-08-31');

    await waitFor(() => expect(totalsLine).toHaveTextContent(`${transactions.total} transactions`));
    expect(document.querySelector('p[aria-live="polite"].caption')).toBe(totalsLine);
  });
});

/*
  An account with nothing imported: once any summary says so, pages show their empty state at once. A skeleton
  shaped like a full list would otherwise collapse into a short empty card (a visible jump).
*/
describe('Empty account', () => {
  const emptySummary = {
    ...summary,
    hasAnyData: false,
    latestTransactionDate: null,
    summary: { ...summary.summary, transactionCount: 0 },
  };
  const hasSkeleton = () => document.querySelector('.animate-pulse') !== null;

  it('Transactions shows its empty state while the list request is still open', async () => {
    const list = deferred<unknown>();
    mockApi({
      '/api/auth/session': session,
      '/api/settings': settings,
      '/api/categories': categories,
      '/api/summary': emptySummary,
      '/api/transactions': list.promise,
    });
    renderWithApp(<TransactionsPage />, { route: '/transactions?period=last-month' });

    expect(await screen.findByRole('heading', { name: 'No transactions in this period' })).toBeInTheDocument();
    expect(hasSkeleton()).toBe(false);
  });

  it('Recurring shows its empty state while its request is still open', async () => {
    const response = deferred<unknown>();
    const client = testQueryClient();
    client.setQueryData(keys.summary({ preset: 'last-month' }), emptySummary);
    mockApi({ '/api/auth/session': session, '/api/settings': settings, '/api/recurring': response.promise });
    renderWithApp(<RecurringPage />, { route: '/recurring', client });

    expect(screen.getByRole('heading', { name: 'No recurring payments yet' })).toBeInTheDocument();
    expect(hasSkeleton()).toBe(false);
  });

  it('Insights shows an empty period without waiting for the AI state', async () => {
    const analysisResponse = deferred<unknown>();
    mockApi({ '/api/auth/session': session, '/api/settings': settings, '/api/summary': emptySummary, '/api/analysis': analysisResponse.promise });
    renderWithApp(<InsightsPage />, { route: '/insights?period=last-month' });

    expect(await screen.findByRole('heading', { name: /Nothing to analyze/ })).toBeInTheDocument();
  });
});
