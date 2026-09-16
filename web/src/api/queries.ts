import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, type TransactionFilters, type TransactionUpdate } from './endpoints';
import type { Settings } from './schemas';
import { periodKey, type PeriodSelection } from '@/lib/period';

export const keys = {
  session: ['session'] as const,
  settings: ['settings'] as const,
  gmail: ['gmail'] as const,
  statements: ['statements'] as const,
  statement: (id: string) => ['statements', id] as const,
  transactions: (filters: TransactionFilters) => ['transactions', filters] as const,
  categories: ['categories'] as const,
  summary: (period: PeriodSelection) => ['summary', periodKey(period)] as const,
  analysis: (period: PeriodSelection) => ['analysis', periodKey(period)] as const,
  recurring: ['recurring'] as const,
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

export const useStatements = () => useQuery({ queryKey: keys.statements, queryFn: api.statements });

export const useStatement = (id: string | null) =>
  useQuery({ queryKey: keys.statement(id ?? ''), queryFn: () => api.statement(id ?? ''), enabled: id !== null });

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

export const useTransactions = (filters: TransactionFilters) =>
  useInfiniteQuery({
    queryKey: keys.transactions(filters),
    queryFn: ({ pageParam }) => api.transactions(filters, pageParam),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
  });

export function useUpdateTransaction() {
  const invalidate = useInvalidateFinancialData();
  return useMutation({
    mutationFn: ({ id, update }: { id: string; update: TransactionUpdate }) => api.updateTransaction(id, update),
    onSuccess: () => invalidate(),
  });
}
