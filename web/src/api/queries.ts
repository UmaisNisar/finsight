import { keepPreviousData, notifyManager, type QueryClient, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useSyncExternalStore } from 'react';
import { api, type TransactionFilters, type TransactionUpdate } from './endpoints';
import { DEFAULT_PERIOD, periodQuery, type PeriodSelection } from '@/lib/period';
import type { AiKey, Session, SessionUser, Settings, SummaryResponse } from './schemas';

export const keys = {
  session: ['session'] as const,
  settings: ['settings'] as const,
  gmail: ['gmail'] as const,
  aiKey: ['ai-key'] as const,
  statements: ['statements'] as const,
  statement: (id: string) => ['statements', id] as const,
  transactions: (filters: TransactionFilters) => ['transactions', filters] as const,
  categories: ['categories'] as const,
  summary: (period: PeriodSelection) => ['summary', periodQuery(period)] as const,
  analysis: (period: PeriodSelection) => ['analysis', periodQuery(period)] as const,
  recurring: ['recurring'] as const,
  institutions: ['institutions'] as const,
};

/** Everything derived from transactions. Invalidated after imports and edits. */
export const financialDataKeys = [['summary'], ['analysis'], ['recurring'], ['transactions'], ['statements']] as const;

export function useInvalidateFinancialData() {
  const client = useQueryClient();
  return () => Promise.all(financialDataKeys.map((key) => client.invalidateQueries({ queryKey: key })));
}

export const useSession = () => useQuery({ queryKey: keys.session, queryFn: api.session, staleTime: 60_000 });

export const useSettings = (enabled = true) =>
  useQuery({ queryKey: keys.settings, queryFn: api.settings, staleTime: 5 * 60_000, enabled });

export function useUpdateSettings() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (settings: Settings) => api.updateSettings(settings),
    onMutate: async (settings) => {
      await client.cancelQueries({ queryKey: keys.settings });
      const previous = client.getQueryData<Settings>(keys.settings);
      client.setQueryData(keys.settings, settings);
      return { previous };
    },
    onError: (_error, _settings, context) => client.setQueryData(keys.settings, context?.previous),
    onSettled: () => {
      void client.invalidateQueries({ queryKey: keys.settings });
      void client.invalidateQueries({ queryKey: ['summary'] });
      void client.invalidateQueries({ queryKey: ['analysis'] });
      void client.invalidateQueries({ queryKey: ['recurring'] });
    },
  });
}

export const useGmail = (enabled = true) => useQuery({ queryKey: keys.gmail, queryFn: api.gmail, enabled });

export const useAiKey = (enabled = true) => useQuery({ queryKey: keys.aiKey, queryFn: api.aiKey, enabled });

/** Saves a Gemini key (the server verifies it with Google first). AI availability in the session follows. */
export function useSaveAiKey() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (apiKey: string) => api.saveAiKey(apiKey),
    onSuccess: (key) => {
      client.setQueryData(keys.aiKey, key);
      void client.invalidateQueries({ queryKey: keys.session });
      void client.invalidateQueries({ queryKey: ['analysis'] });
    },
    // Also after a failure: a save that timed out may still have been stored by the server.
    onSettled: () => client.invalidateQueries({ queryKey: keys.aiKey }),
  });
}

export function useDeleteAiKey() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: api.deleteAiKey,
    onSuccess: () => {
      const current = client.getQueryData<AiKey>(keys.aiKey);
      if (current) client.setQueryData<AiKey>(keys.aiKey, { ...current, hasUserKey: false, hint: null });
      void client.invalidateQueries({ queryKey: keys.aiKey });
      void client.invalidateQueries({ queryKey: keys.session });
      void client.invalidateQueries({ queryKey: ['analysis'] });
    },
  });
}

/**
 * Marks onboarding finished. The cached session flips straight away so the gate swaps to the app in one step,
 * then the server's session is fetched to confirm.
 */
export function useCompleteOnboarding() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: api.completeOnboarding,
    onSuccess: () => {
      const session = client.getQueryData<Session>(keys.session);
      if (session?.user) client.setQueryData<Session>(keys.session, { ...session, user: { ...session.user, onboardingCompleted: true } });
      void client.invalidateQueries({ queryKey: keys.session });
    },
  });
}

export const useStatements = () => useQuery({ queryKey: keys.statements, queryFn: api.statements });

/** `enabled` goes false while the sheet closes, so deleting a statement doesn't refetch it into an error mid-animation. */
export const useStatement = (id: string | null, enabled = true) =>
  useQuery({ queryKey: keys.statement(id ?? ''), queryFn: () => api.statement(id ?? ''), enabled: enabled && id !== null });

/** Banks with sign-in links and download instructions. Rarely changes. */
export const useInstitutions = (enabled = true) => useQuery({ queryKey: keys.institutions, queryFn: api.institutions, staleTime: 60 * 60_000, enabled });

export const useCategories = () => useQuery({ queryKey: keys.categories, queryFn: api.categories, staleTime: 10 * 60_000 });

export const useSummary = (period: PeriodSelection, enabled = true) =>
  useQuery({ queryKey: keys.summary(period), queryFn: () => api.summary(period), placeholderData: keepPreviousData, enabled });

export const useAnalysis = (period: PeriodSelection) =>
  useQuery({ queryKey: keys.analysis(period), queryFn: () => api.analysis(period), placeholderData: keepPreviousData });

export function useGenerateAnalysis(period: PeriodSelection) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => api.generateAnalysis(period),
    onSuccess: (data) => client.setQueryData(keys.analysis(period), data),
  });
}

export const useRecurring = () => useQuery({ queryKey: keys.recurring, queryFn: api.recurring, staleTime: 5 * 60_000 });

/**
 * Whether the account has any imported transactions, read from whichever up-to-date summary is already cached (any
 * period). Undefined until one has loaded. Pages use it to show their empty state straight away instead of a
 * full-page skeleton that would collapse into a short empty card.
 */
export function useHasAnyData(): boolean | undefined {
  const client = useQueryClient();
  // Batched like TanStack's own useIsFetching: cache events fire while other components render, and notifying
  // synchronously would update this component mid-render.
  const subscribe = useCallback((onChange: () => void) => client.getQueryCache().subscribe(notifyManager.batchCalls(onChange)), [client]);
  return useSyncExternalStore(
    subscribe,
    () => {
      const fresh = client.getQueryCache().findAll({ queryKey: ['summary'] }).find((q) => q.state.data !== undefined && !q.state.isInvalidated);
      return (fresh?.state.data as SummaryResponse | undefined)?.hasAnyData;
    },
  );
}

/**
 * Loads small, period-independent data as soon as the app opens, so Statements and Recurring show real content (or
 * their empty state) immediately, and every screen knows whether the account has any data at all.
 */
export function prefetchAppData(client: QueryClient, { gmail }: { gmail: boolean }) {
  if (gmail) void client.prefetchQuery({ queryKey: keys.gmail, queryFn: api.gmail });
  void client.prefetchQuery({ queryKey: keys.summary(DEFAULT_PERIOD), queryFn: () => api.summary(DEFAULT_PERIOD) });
  void client.prefetchQuery({ queryKey: keys.statements, queryFn: api.statements });
  void client.prefetchQuery({ queryKey: keys.recurring, queryFn: api.recurring, staleTime: 5 * 60_000 });
}

export const useTransactions = (filters: TransactionFilters, enabled = true) =>
  useInfiniteQuery({
    queryKey: keys.transactions(filters),
    queryFn: ({ pageParam }) => api.transactions(filters, pageParam),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
    enabled,
  });

export function useUpdateTransaction() {
  const invalidate = useInvalidateFinancialData();
  return useMutation({
    mutationFn: ({ id, update }: { id: string; update: TransactionUpdate }) => api.updateTransaction(id, update),
    onSuccess: () => invalidate(),
  });
}

/**
 * Signs out (or leaves the demo): drops every cached query except the session, which is marked signed out
 * immediately so the welcome screen appears without a loading flash, then confirmed with the server.
 */
export function useSignOut() {
  const client = useQueryClient();
  return useMutation({ mutationFn: api.logout, onSuccess: () => markSignedOut(client), meta: { errorToast: true } });
}

/**
 * Starts the demo: the session is marked signed in straight from the response, so the welcome screen is replaced
 * by the app in one step, with no window where it re-enables or flashes back while the session refetches.
 */
export function useStartDemo() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: api.startDemo,
    onSuccess: (user: SessionUser) => {
      const session = client.getQueryData<Session>(keys.session);
      client.removeQueries({ predicate: (query) => query.queryKey[0] !== keys.session[0] });
      if (session) client.setQueryData<Session>(keys.session, { ...session, authenticated: true, user });
      void client.invalidateQueries({ queryKey: keys.session });
    },
  });
}

export function markSignedOut(client: QueryClient) {
  const session = client.getQueryData<Session>(keys.session);
  client.removeQueries({ predicate: (query) => query.queryKey[0] !== keys.session[0] });
  if (session) client.setQueryData<Session>(keys.session, { ...session, authenticated: false, user: null });
  void client.invalidateQueries({ queryKey: keys.session });
}
