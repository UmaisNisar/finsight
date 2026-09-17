import { MutationCache, QueryCache, QueryClient, type Mutation } from '@tanstack/react-query';
import { ApiError, errorMessage } from '@/api/client';
import { keys } from '@/api/queries';
import type { Session } from '@/api/schemas';
import { sessionNotice } from '@/components/errors/sessionNotice';
import { announce } from './providers/ToastProvider';

declare module '@tanstack/react-query' {
  interface Register {
    mutationMeta: {
      /**
       * Show a toast when this mutation fails. Off by default, because most mutations explain failures inline
       * next to the control that started them; turn it on for mutations with no error UI of their own.
       */
      errorToast?: boolean;
    };
  }
}

export function shouldToastMutationError(error: unknown, mutation: Pick<Mutation<unknown, unknown, unknown, unknown>, 'meta' | 'options'>): boolean {
  if (error instanceof ApiError && error.status === 401) return false; // The welcome screen explains it.
  if (error instanceof DOMException && error.name === 'AbortError') return false;
  return mutation.meta?.errorToast ?? false;
}

export function createQueryClient(): QueryClient {
  const client: QueryClient = new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        refetchOnWindowFocus: false,
        // Network failures and 5xx retry twice; client errors (4xx, unexpected responses) and timeouts don't.
        retry: (count, error) => {
          if (error instanceof ApiError && (error.code === 'timeout' || (error.status > 0 && error.status < 500))) return false;
          return count < 2;
        },
      },
    },
    queryCache: new QueryCache({ onError: (error) => handleUnauthenticated(error) }),
    mutationCache: new MutationCache({
      onError: (error, _variables, _context, mutation) => {
        handleUnauthenticated(error);
        if (shouldToastMutationError(error, mutation)) announce(errorMessage(error), 'error');
      },
    }),
  });

  /**
   * An expired session anywhere sends the user back to the welcome screen. When it ends mid-use (the cached session
   * was signed in), the welcome screen says so, once.
   */
  function handleUnauthenticated(error: unknown) {
    if (!(error instanceof ApiError && error.status === 401)) return;
    if (client.getQueryData<Session>(keys.session)?.authenticated) sessionNotice.markExpired();
    void client.invalidateQueries({ queryKey: keys.session });
  }

  return client;
}
