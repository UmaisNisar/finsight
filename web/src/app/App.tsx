import { QueryClientProvider } from '@tanstack/react-query';
import { Compass, RotateCw } from 'lucide-react';
import { MotionConfig } from 'motion/react';
import { lazy, Suspense, useEffect } from 'react';
import { createBrowserRouter, Link, Navigate, Outlet, RouterProvider, ScrollRestoration, useLocation, type RouteObject } from 'react-router';
import { errorMessage } from '@/api/client';
import { useSession } from '@/api/queries';
import { devCrash } from '@/components/errors/devCrash';
import { ErrorBoundary } from '@/components/errors/ErrorBoundary';
import { FullPageError } from '@/components/errors/ErrorScreens';
import { GlobalErrorListener } from '@/components/errors/GlobalErrorListener';
import { OfflineIndicator } from '@/components/errors/OfflineIndicator';
import { PageOutlet, RootRouteError, ShellRouteError } from '@/components/errors/RouteErrors';
import { sessionNotice } from '@/components/errors/sessionNotice';
import { Button, buttonStyles } from '@/components/ui/Button';
import { Card, EmptyState } from '@/components/ui/primitives';
import { WelcomePage } from '@/features/auth/WelcomePage';
import { ONBOARDING_PATH } from '@/features/onboarding/paths';
import { AppShell } from './AppShell';
import { Pages } from './pages';
import { JobsProvider } from './providers/JobsProvider';
import { ConfirmProvider } from './providers/ConfirmProvider';
import { ThemeController } from './providers/ThemeController';
import { ToastProvider } from './providers/ToastProvider';
import { createQueryClient } from './queryClient';

const queryClient = createQueryClient();

// Most sessions never need onboarding, so its code loads only for accounts that do.
const OnboardingPage = lazy(() => import('@/features/onboarding/OnboardingPage'));

/** A launch screen: just the app mark, fading in only if loading takes a moment. */
function LaunchScreen() {
  return (
    <div className="flex min-h-dvh items-center justify-center" aria-busy="true" aria-label="Loading FinSight">
      <img src="/favicon.svg" alt="" width={56} height={56} className="launch-mark size-14 rounded-2xl" />
    </div>
  );
}

/** FinSight couldn't check the session, usually because the server is unreachable. */
function StartupError({ message, retrying, onRetry }: { message: string; retrying: boolean; onRetry: () => void }) {
  return (
    <div className="flex min-h-dvh items-center justify-center px-5 py-10">
      <main role="alert" aria-labelledby="startup-title" className="card w-full max-w-[420px] rounded-[32px] px-6 pt-9 pb-8 text-center sm:px-9">
        <div className="fade-in">
          <img src="/favicon.svg" alt="" width={56} height={56} className="mx-auto mb-5 size-14 rounded-[16px] shadow-float" />
          <h1 id="startup-title" className="text-[1.5rem] leading-tight font-bold tracking-[-0.024em]">
            FinSight couldn’t start
          </h1>
          <p className="mx-auto mt-2 max-w-[20rem] text-[0.9375rem] leading-snug text-pretty text-label-secondary">{message}</p>
          <Button className="mt-7" loading={retrying} icon={<RotateCw size={15} aria-hidden="true" />} onClick={onRetry}>
            Try again
          </Button>
        </div>
      </main>
    </div>
  );
}

/**
 * Chooses between the welcome screen, first-run onboarding and the app. A signed-in account that hasn't finished
 * onboarding sees it whatever the URL (the address becomes /onboarding, keeping any query such as ?gmail=);
 * demo accounts never do. Once finished, /onboarding leads to Overview.
 */
export function SessionGate() {
  const session = useSession();
  const location = useLocation();
  if (import.meta.env.DEV) devCrash('root', location.search);

  const authenticated = session.data?.authenticated === true;
  const confirming = session.isFetching;
  // A signed-in session confirmed by the server retires the "you were signed out" notice.
  useEffect(() => {
    if (authenticated && !confirming) sessionNotice.clear();
  }, [authenticated, confirming]);

  if (session.isPending) {
    return <LaunchScreen />;
  }

  // Only with no session at all: a failed background refresh keeps the app on screen.
  if (!session.data) {
    return <StartupError message={errorMessage(session.error)} retrying={session.isFetching} onRetry={() => void session.refetch()} />;
  }

  if (!session.data.authenticated) {
    return <WelcomePage capabilities={session.data.capabilities} />;
  }

  const user = session.data.user;
  const needsOnboarding = user !== null && !user.isDemo && !user.onboardingCompleted;
  const onOnboardingPath = location.pathname === ONBOARDING_PATH;

  if (needsOnboarding && !onOnboardingPath) {
    return <Navigate to={{ pathname: ONBOARDING_PATH, search: location.search }} replace />;
  }
  if (!needsOnboarding && onOnboardingPath) {
    return <Navigate to="/" replace />;
  }

  return (
    // Onboarding shows progress inline, so the provider stays quiet there (no toasts or floating panel timing).
    <JobsProvider enabled quiet={needsOnboarding}>
      {needsOnboarding ? (
        <Suspense fallback={<LaunchScreen />}>
          <OnboardingPage />
        </Suspense>
      ) : (
        <>
          {/* Each screen keeps its own scroll position, like tabs; period and filter changes don't scroll. */}
          <ScrollRestoration getKey={(location) => location.pathname} />
          <Outlet />
        </>
      )}
    </JobsProvider>
  );
}

function NotFound() {
  return (
    <Card className="mt-6">
      <EmptyState
        icon={<Compass size={26} aria-hidden="true" />}
        title="This page doesn’t exist"
        description="The link may be out of date."
        action={
          <Link to="/" className={buttonStyles({ variant: 'secondary' })}>
            Go to Overview
          </Link>
        }
      />
    </Card>
  );
}

/**
 * Every failure has a screen of its own. Anything outside the shell (or in the shell itself) gets the full-page
 * screen; a failing screen inside the shell gets an error card while the sidebar and tab bar keep working.
 */
export const routes: RouteObject[] = [
  {
    element: <SessionGate />,
    errorElement: <RootRouteError />,
    children: [
      {
        element: <AppShell />,
        children: [
          {
            element: import.meta.env.DEV ? <PageOutlet /> : <Outlet />,
            errorElement: <ShellRouteError />,
            children: [
              { index: true, element: <Pages.Overview /> },
              { path: 'transactions', element: <Pages.Transactions /> },
              { path: 'statements', element: <Pages.Statements /> },
              { path: 'insights', element: <Pages.Insights /> },
              { path: 'recurring', element: <Pages.Recurring /> },
              { path: 'settings', element: <Pages.Settings /> },
              { path: 'more', element: <Pages.More /> },
              { path: '*', element: <NotFound /> },
            ],
          },
        ],
      },
    ],
  },
];

const router = createBrowserRouter(routes);

export function App() {
  return (
    // The last line of defence, for failures above the router (the providers, the theme controller).
    <ErrorBoundary fallback={({ error }) => <FullPageError error={error} />}>
      <QueryClientProvider client={queryClient}>
        <MotionConfig reducedMotion="user">
          <ToastProvider banner={<OfflineIndicator />}>
            <ConfirmProvider>
              <GlobalErrorListener />
              <ThemeController />
              <Suspense>
                <RouterProvider router={router} />
              </Suspense>
            </ConfirmProvider>
          </ToastProvider>
        </MotionConfig>
      </QueryClientProvider>
    </ErrorBoundary>
  );
}
