import { useQueryClient } from '@tanstack/react-query';
import { Link, Outlet, useLocation, useNavigate, useRouteError } from 'react-router';
import { buttonStyles } from '@/components/ui/Button';
import { devCrash } from './devCrash';
import { FullPageError, PageError } from './ErrorScreens';

/** The router's error element outside the app shell: a full-page screen, never React Router's developer default. */
export function RootRouteError() {
  return <FullPageError error={useRouteError()} />;
}

/**
 * The router's error element for screens inside the shell. React Router clears it on every navigation; Try again
 * refetches failed queries and re-enters the current URL, which renders the screen afresh.
 */
export function ShellRouteError() {
  const error = useRouteError();
  const location = useLocation();
  const navigate = useNavigate();
  const client = useQueryClient();

  function retry() {
    void client.refetchQueries({ predicate: (query) => query.state.status === 'error' });
    void navigate({ pathname: location.pathname, search: location.search, hash: location.hash }, { replace: true, state: location.state, preventScrollReset: true });
  }

  return (
    <PageError
      error={error}
      onRetry={retry}
      homeLink={
        <Link to="/" className={buttonStyles({ variant: 'secondary' })}>
          Go to Overview
        </Link>
      }
    />
  );
}

/** The screens' outlet, which in development can be made to throw with `?__crash=1` (see devCrash). */
export function PageOutlet() {
  const { search } = useLocation();
  devCrash('page', search);
  return <Outlet />;
}
