import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MotionConfig } from 'motion/react';
import { lazy, Suspense } from 'react';
import { createBrowserRouter, Outlet, RouterProvider } from 'react-router';
import { ApiError } from '@/api/client';
import { keys, useSession } from '@/api/queries';
import { ErrorState, Skeleton } from '@/components/ui/primitives';
import { WelcomePage } from '@/features/auth/WelcomePage';
import { AppShell } from './AppShell';
import { JobsProvider } from './providers/JobsProvider';
import { ThemeController } from './providers/ThemeController';
import { ToastProvider } from './providers/ToastProvider';

const OverviewPage = lazy(() => import('@/features/overview/OverviewPage'));
const TransactionsPage = lazy(() => import('@/features/transactions/TransactionsPage'));
const StatementsPage = lazy(() => import('@/features/statements/StatementsPage'));
const InsightsPage = lazy(() => import('@/features/insights/InsightsPage'));
const RecurringPage = lazy(() => import('@/features/recurring/RecurringPage'));
const SettingsPage = lazy(() => import('@/features/settings/SettingsPage'));
const MorePage = lazy(() => import('@/features/settings/MorePage'));

const queryClient: QueryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
      retry: (count, error) => !(error instanceof ApiError && error.status > 0 && error.status < 500) && count < 2,
    },
  },
  // An expired session anywhere sends the user back to the welcome screen.
  queryCache: new QueryCache({ onError: (error) => handleUnauthenticated(error) }),
  mutationCache: new MutationCache({ onError: (error) => handleUnauthenticated(error) }),
});

function handleUnauthenticated(error: unknown) {
  if (error instanceof ApiError && error.status === 401) {
    void queryClient.invalidateQueries({ queryKey: keys.session });
  }
}

function SessionGate() {
  const session = useSession();

  if (session.isPending) {
    return (
      <div className="flex min-h-dvh items-center justify-center" aria-busy="true" aria-label="Loading FinSight">
        <Skeleton className="size-14 rounded-2xl" />
      </div>
    );
  }

  if (session.isError) {
    return <ErrorState className="min-h-dvh justify-center" message="FinSight couldn't start. Check your connection." onRetry={() => void session.refetch()} />;
  }

  if (!session.data.authenticated) {
    return <WelcomePage capabilities={session.data.capabilities} />;
  }

  return (
    <JobsProvider enabled>
      <Outlet />
    </JobsProvider>
  );
}

const router = createBrowserRouter([
  {
    element: <SessionGate />,
    children: [
      {
        element: <AppShell />,
        children: [
          { index: true, element: <OverviewPage /> },
          { path: 'transactions', element: <TransactionsPage /> },
          { path: 'statements', element: <StatementsPage /> },
          { path: 'insights', element: <InsightsPage /> },
          { path: 'recurring', element: <RecurringPage /> },
          { path: 'settings', element: <SettingsPage /> },
          { path: 'more', element: <MorePage /> },
          { path: '*', element: <ErrorState message="This page doesn't exist." /> },
        ],
      },
    ],
  },
]);

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <MotionConfig reducedMotion="user">
        <ToastProvider>
          <ThemeController />
          <Suspense>
            <RouterProvider router={router} />
          </Suspense>
        </ToastProvider>
      </MotionConfig>
    </QueryClientProvider>
  );
}
