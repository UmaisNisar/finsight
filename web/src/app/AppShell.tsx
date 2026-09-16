import { useQueryClient } from '@tanstack/react-query';
import { ChartPie, FileText, LayoutGrid, List, LogIn, Menu, Repeat, Settings, Sparkles } from 'lucide-react';
import { Suspense } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router';
import { api } from '@/api/endpoints';
import { useSession } from '@/api/queries';
import { JobProgressPanel } from '@/components/JobProgressPanel';
import { Skeleton } from '@/components/ui/primitives';
import { cn } from '@/lib/cn';

const NAV = [
  { to: '/', label: 'Overview', icon: LayoutGrid },
  { to: '/transactions', label: 'Transactions', icon: List },
  { to: '/statements', label: 'Statements', icon: FileText },
  { to: '/insights', label: 'Insights', icon: Sparkles },
  { to: '/recurring', label: 'Recurring', icon: Repeat },
  { to: '/settings', label: 'Settings', icon: Settings },
] as const;

const TABS = [
  { to: '/', label: 'Overview', icon: ChartPie },
  { to: '/transactions', label: 'Activity', icon: List },
  { to: '/insights', label: 'Insights', icon: Sparkles },
  { to: '/recurring', label: 'Recurring', icon: Repeat },
  { to: '/more', label: 'More', icon: Menu },
] as const;

/** Keeps ?period across navigation so every screen shows the same time range. */
function useLinkWithPeriod() {
  const location = useLocation();
  const params = new URLSearchParams(location.search);
  const keep = new URLSearchParams();
  for (const key of ['period', 'from', 'to']) {
    const value = params.get(key);
    if (value) keep.set(key, value);
  }
  const suffix = keep.toString();
  return (to: string) => (suffix ? `${to}?${suffix}` : to);
}

function DemoBanner({ canSignIn }: { canSignIn: boolean }) {
  const client = useQueryClient();
  const navigate = useNavigate();

  async function exit() {
    await api.logout();
    client.clear();
    navigate('/', { replace: true });
  }

  return (
    <div className="mb-6 flex flex-wrap items-center justify-between gap-3 rounded-2xl bg-accent-soft px-4 py-3 text-[0.9375rem]">
      <p>
        <span className="font-medium">You’re exploring sample data.</span>{' '}
        <span className="text-label-secondary">Every number is synthetic, from a fictional bank.</span>
      </p>
      <div className="flex gap-2">
        {canSignIn && (
          <a href="/api/auth/google" className="inline-flex h-8 items-center gap-1.5 rounded-full bg-accent px-3 text-[0.8125rem] font-medium text-accent-contrast">
            <LogIn size={14} aria-hidden="true" />
            Use my data
          </a>
        )}
        <button type="button" onClick={exit} className="h-8 rounded-full px-3 text-[0.8125rem] font-medium text-accent hover:bg-accent-soft">
          Exit demo
        </button>
      </div>
    </div>
  );
}

function PageFallback() {
  return (
    <div aria-busy="true" aria-label="Loading" className="space-y-6">
      <Skeleton className="h-10 w-64" />
      <Skeleton className="h-40 w-full rounded-[20px]" />
      <Skeleton className="h-72 w-full rounded-[20px]" />
    </div>
  );
}

export function AppShell() {
  const session = useSession();
  const withPeriod = useLinkWithPeriod();
  const user = session.data?.user;

  return (
    <div className="min-h-dvh">
      <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:top-3 focus:left-3 focus:z-50 focus:rounded-full focus:bg-surface focus:px-4 focus:py-2 focus:shadow-float">
        Skip to content
      </a>

      {/* Sidebar: macOS-style source list on wide screens. */}
      <aside className="glass fixed inset-y-0 left-0 z-30 hidden w-[248px] flex-col border-r border-separator px-3 pt-6 pb-4 lg:flex">
        <div className="mb-7 flex items-center gap-2.5 px-3">
          <img src="/favicon.svg" alt="" className="size-7 rounded-[8px]" />
          <span className="text-[1.0625rem] font-semibold tracking-[-0.015em]">FinSight</span>
        </div>
        <nav aria-label="Main" className="flex-1">
          <ul className="space-y-0.5">
            {NAV.map(({ to, label, icon: Icon }) => (
              <li key={to}>
                <NavLink
                  to={withPeriod(to)}
                  end={to === '/'}
                  className={({ isActive }) =>
                    cn(
                      'flex h-9 items-center gap-3 rounded-[10px] px-3 text-[0.9375rem] transition-colors',
                      isActive ? 'bg-fill-strong font-medium text-label' : 'text-label-secondary hover:bg-fill hover:text-label',
                    )
                  }
                >
                  {({ isActive }) => (
                    <>
                      <Icon size={18} strokeWidth={isActive ? 2.25 : 1.9} className={isActive ? 'text-accent' : ''} aria-hidden="true" />
                      {label}
                    </>
                  )}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
        {user && (
          <div className="flex items-center gap-3 rounded-xl px-3 py-2">
            <span aria-hidden="true" className="flex size-8 items-center justify-center rounded-full bg-fill-strong text-[0.8125rem] font-semibold">
              {user.name.slice(0, 1).toUpperCase()}
            </span>
            <div className="min-w-0">
              <p className="truncate text-[0.875rem] font-medium">{user.name}</p>
              <p className="caption truncate">{user.isDemo ? 'Demo account' : user.email}</p>
            </div>
          </div>
        )}
      </aside>

      <main id="main" className="px-4 pt-[max(1.25rem,env(safe-area-inset-top))] pb-[calc(6.5rem+env(safe-area-inset-bottom))] sm:px-6 lg:ml-[248px] lg:px-10 lg:pt-10 lg:pb-16">
        <div className="mx-auto max-w-[1080px]">
          {user?.isDemo && <DemoBanner canSignIn={session.data?.capabilities.googleSignIn ?? false} />}
          <Suspense fallback={<PageFallback />}>
            <Outlet />
          </Suspense>
        </div>
      </main>

      {/* Tab bar: phones and tablets. */}
      <nav aria-label="Main" className="glass-strong fixed inset-x-0 bottom-0 z-30 border-t border-separator pb-[env(safe-area-inset-bottom)] lg:hidden">
        <ul className="mx-auto grid max-w-lg grid-cols-5">
          {TABS.map(({ to, label, icon: Icon }) => (
            <li key={to}>
              <NavLink
                to={withPeriod(to)}
                end={to === '/'}
                className={({ isActive }) =>
                  cn('flex h-[56px] flex-col items-center justify-center gap-0.5 text-[0.6875rem] font-medium', isActive ? 'text-accent' : 'text-label-tertiary')
                }
              >
                <Icon size={22} strokeWidth={1.9} aria-hidden="true" />
                {label}
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>

      <JobProgressPanel />
    </div>
  );
}
