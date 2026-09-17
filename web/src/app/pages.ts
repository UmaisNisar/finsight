import { lazy } from 'react';

/** Code for each screen, loaded on demand. Shared by the router (lazy pages) and the navigation (preloading). */
const loaders = {
  '/': () => import('@/features/overview/OverviewPage'),
  '/transactions': () => import('@/features/transactions/TransactionsPage'),
  '/statements': () => import('@/features/statements/StatementsPage'),
  '/insights': () => import('@/features/insights/InsightsPage'),
  '/recurring': () => import('@/features/recurring/RecurringPage'),
  '/settings': () => import('@/features/settings/SettingsPage'),
  '/more': () => import('@/features/settings/MorePage'),
} as const;

export const Pages = {
  Overview: lazy(loaders['/']),
  Transactions: lazy(loaders['/transactions']),
  Statements: lazy(loaders['/statements']),
  Insights: lazy(loaders['/insights']),
  Recurring: lazy(loaders['/recurring']),
  Settings: lazy(loaders['/settings']),
  More: lazy(loaders['/more']),
};

/** Starts loading a screen's code (for example when its nav item is hovered), so the navigation never waits. */
export function preloadPage(path: string) {
  const loader = loaders[path.split('?')[0] as keyof typeof loaders];
  if (loader) void loader().catch(() => undefined);
}

/** Loads every screen soon after the app shell appears. */
export function preloadAllPages() {
  const load = () => Object.values(loaders).forEach((loader) => void loader().catch(() => undefined));
  if ('requestIdleCallback' in window) window.requestIdleCallback(load, { timeout: 500 });
  else setTimeout(load, 200);
}
