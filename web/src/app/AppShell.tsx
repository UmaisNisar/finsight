import { useQueryClient } from '@tanstack/react-query';
import { ChartPie, FileText, LayoutGrid, List, LogIn, Menu, Repeat, Settings, Sparkles } from 'lucide-react';
import { Suspense, useEffect } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router';
import { prefetchAppData, useSession, useSignOut } from '@/api/queries';
import { JobProgressPanel } from '@/components/JobProgressPanel';
import { Button, buttonStyles } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/primitives';
import { usePeriodLink } from '@/hooks/usePeriod';
import { useSlidingIndicator } from '@/hooks/useSlidingIndicator';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { cn } from '@/lib/cn';
import { preloadAllPages, preloadPage } from './pages';

const NAV = [
  { to: '/', label: 'Overview', icon: LayoutGrid },
  { to: '/transactions', label: 'Transactions', icon: List },
  { to: '/statements', label: 'Statements', icon: FileText },
  { to: '/insights', label: 'Insights', icon: Sparkles },
  { to: '/recurring', label: 'Recurring', icon: Repeat },
  { to: '/settings', label: 'Settings', icon: Settings },
] as const;

/** Phone tab bar. "More" also stands for the screens it lists, so a tab is always selected. */
const TABS = [
  { to: '/', label: 'Overview', icon: ChartPie, also: [] },
  { to: '/transactions', label: 'Activity', icon: List, also: [] },
  { to: '/insights', label: 'Insights', icon: Sparkles, also: [] },
  { to: '/recurring', label: 'Recurring', icon: Repeat, also: [] },
  { to: '/more', label: 'More', icon: Menu, also: ['/statements', '/settings'] },
] as const;


function DemoBanner({ canSignIn }: { canSignIn: boolean }) {
  const navigate = useNavigate();
  const signOut = useSignOut();
  const once = useSingleFlight();

  return (
    <div className="card mb-6 flex flex-wrap items-center justify-between gap-3 rounded-[22px] px-4 py-3 text-[0.9375rem]">
      <p>
        <span className="font-medium">You’re exploring sample data.</span>{' '}
        <span className="text-label-secondary">Every number is synthetic, from a fictional bank.</span>
      </p>
      <div className="flex gap-2">
        {canSignIn && (
          <a href="/api/auth/google" className={buttonStyles({ size: 'sm' })}>
            <LogIn size={14} aria-hidden="true" />
            Use my data
          </a>
        )}
        <Button variant="plain" size="sm" loading={signOut.isPending} onClick={() => void once(() => signOut.mutateAsync().then(() => navigate('/', { replace: true })))}>
          Exit demo
        </Button>
      </div>
    </div>
  );
}

/** Shown only if a page's code hasn't been preloaded yet: a title-shaped placeholder, never a spinner. */
function PageFallback() {
  return (
    <div aria-busy="true" aria-label="Loading">
      <Skeleton className="h-9 w-56" />
      <Skeleton className="mt-3 h-4 w-72 max-w-full" />
    </div>
  );
}

export function AppShell() {
  const session = useSession();
  const periodLink = usePeriodLink();
  const { pathname } = useLocation();
  const user = session.data?.user;
  // Links are positioned inside their nav, so each lens measures and moves relative to it.
  const client = useQueryClient();
  const gmailAvailable = (session.data?.capabilities.gmail ?? false) && !user?.isDemo;
  useEffect(() => preloadAllPages(), []);
  useEffect(() => prefetchAppData(client, { gmail: gmailAvailable }), [client, gmailAvailable]);

  const { container: sidebarNav, lens: sidebarLens } = useSlidingIndicator<HTMLElement, HTMLSpanElement>('a[aria-current="page"]', pathname);
  const { container: tabNav, lens: tabLens } = useSlidingIndicator<HTMLElement, HTMLSpanElement>('a[aria-current="page"]', pathname);

  return (
    <div className="min-h-dvh">
      <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:top-3 focus:left-3 focus:z-50 focus:rounded-full focus:bg-surface focus:px-4 focus:py-2 focus:shadow-float">
        Skip to content
      </a>

      {/* Sidebar: a floating glass source list on wide screens. */}
      <aside className="glass-strong fixed top-3 bottom-3 left-3 z-30 hidden w-[240px] flex-col rounded-[28px] px-3 pt-6 pb-3 lg:flex">
        <div className="mb-7 flex items-center gap-2.5 px-3">
          <img src="/favicon.svg" alt="" width={28} height={28} className="size-7 rounded-[8px]" />
          <span className="text-[1.0625rem] font-semibold tracking-[-0.015em]">FinSight</span>
        </div>
        <nav ref={sidebarNav} aria-label="Main" className="relative flex-1">
          <span ref={sidebarLens} aria-hidden="true" className="sliding-lens glass-lens rounded-full opacity-0" />
          <ul className="space-y-0.5">
            {NAV.map(({ to, label, icon: Icon }) => (
              <li key={to}>
                <NavLink
                  to={periodLink(to)}
                  end={to === '/'}
                  onPointerEnter={() => preloadPage(to)}
                  onFocus={() => preloadPage(to)}
                  className={({ isActive }) =>
                    cn(
                      'relative flex h-10 items-center gap-3 rounded-full px-3.5 text-[0.9375rem] transition-[color]',
                      // No hover fill: with the sliding lens it read as a second highlight while the lens travelled.
                      isActive ? 'font-medium text-label' : 'text-label-secondary hover:text-label',
                    )
                  }
                >
                  {({ isActive }) => (
                    <>
                      <Icon size={18} strokeWidth={isActive ? 2.25 : 1.9} className={cn('relative', isActive && 'text-accent')} aria-hidden="true" />
                      <span className="relative">{label}</span>
                    </>
                  )}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
        {user && (
          <div className="glass-control flex items-center gap-3 rounded-[20px] px-3 py-2">
            <span aria-hidden="true" className="flex size-8 shrink-0 items-center justify-center rounded-full bg-fill-strong text-[0.8125rem] font-semibold">
              {user.name.slice(0, 1).toUpperCase()}
            </span>
            <div className="min-w-0">
              <p className="truncate text-[0.875rem] font-medium">{user.name}</p>
              <p className="caption truncate">{user.isDemo ? 'Demo account' : user.email}</p>
            </div>
          </div>
        )}
      </aside>

      <main id="main" className="px-4 pt-[max(1.25rem,env(safe-area-inset-top))] pb-[calc(6.5rem+env(safe-area-inset-bottom))] sm:px-6 lg:ml-[256px] lg:px-10 lg:pt-10 lg:pb-16">
        <div className="mx-auto max-w-[1080px]">
          {/* Settings has its own Exit demo in the account card, so the banner would repeat it. */}
          {user?.isDemo && pathname !== '/settings' && <DemoBanner canSignIn={session.data?.capabilities.googleSignIn ?? false} />}
          {/*
            Suspense stays outside the per-screen key: navigations run in a transition, so an already-shown
            boundary keeps the current screen until the next one's code is ready, never flashing a fallback.
            Screens change with a quick fade of their content only; card glass appears at once (see .page-enter).
          */}
          <Suspense fallback={<PageFallback />}>
            <div key={pathname} className="page-enter">
              <Outlet />
            </div>
          </Suspense>
        </div>
      </main>

      {/* Tab bar: a floating glass capsule on phones and tablets. */}
      <nav
        ref={tabNav}
        aria-label="Main"
        className="glass-strong fixed inset-x-3 bottom-[max(0.75rem,env(safe-area-inset-bottom))] z-30 mx-auto max-w-md rounded-full p-1.5 lg:hidden"
      >
        <span ref={tabLens} aria-hidden="true" className="sliding-lens glass-lens rounded-full opacity-0" />
        <ul className="grid grid-cols-5">
          {TABS.map(({ to, label, icon: Icon, also }) => {
            const alsoActive = (also as readonly string[]).includes(pathname);
            return (
              <li key={to}>
                <NavLink
                  to={periodLink(to)}
                  end={to === '/'}
                  onPointerDown={() => preloadPage(to)}
                  onFocus={() => preloadPage(to)}
                  aria-current={alsoActive ? 'page' : undefined}
                  className={({ isActive }) =>
                    cn(
                      'relative flex h-[52px] flex-col items-center justify-center gap-0.5 rounded-full text-[0.6875rem] font-medium transition-colors',
                      isActive || alsoActive ? 'text-accent' : 'text-label-secondary',
                    )
                  }
                >
                  {({ isActive }) => (
                    <>
                      <Icon size={21} strokeWidth={isActive || alsoActive ? 2.2 : 1.9} className="relative" aria-hidden="true" />
                      <span className="relative">{label}</span>
                    </>
                  )}
                </NavLink>
              </li>
            );
          })}
        </ul>
      </nav>

      <JobProgressPanel />
    </div>
  );
}
