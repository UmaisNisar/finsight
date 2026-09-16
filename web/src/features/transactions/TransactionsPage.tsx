import { ArrowLeftRight, Search, SlidersHorizontal, X } from 'lucide-react';
import { useDeferredValue, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { errorMessage } from '@/api/client';
import type { TransactionFilters } from '@/api/endpoints';
import { useCategories, useSummary, useTransactions } from '@/api/queries';
import type { Transaction, TransactionType } from '@/api/schemas';
import { PageHeader } from '@/components/PageHeader';
import { PeriodPicker } from '@/components/PeriodPicker';
import { Button } from '@/components/ui/Button';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { EmptyState, ErrorState, Skeleton } from '@/components/ui/primitives';
import { usePeriod } from '@/hooks/usePeriod';
import { usePreferences } from '@/hooks/usePreferences';
import { cn } from '@/lib/cn';
import { CategoryGlyph } from '@/lib/categories';
import { accountLabel, formatDate, formatMoney } from '@/lib/format';
import { TransactionEditor } from './TransactionEditor';

type TypeFilter = 'all' | TransactionType;
type Sort = NonNullable<TransactionFilters['sort']>;

function TransactionRow({ transaction, onOpen }: { transaction: Transaction; onOpen: () => void }) {
  const isTransfer = transaction.type === 'transfer';
  const isIncome = transaction.type === 'income' && transaction.amount > 0;
  const muted = transaction.isExcluded || transaction.isReversal;

  return (
    <li>
      <button
        type="button"
        onClick={onOpen}
        className="flex w-full items-center gap-3.5 px-4 py-3 text-left transition-colors hover:bg-fill focus-visible:bg-fill md:px-5"
      >
        <CategoryGlyph groupId={transaction.groupId} type={transaction.type} size={38} />
        <div className={cn('min-w-0 flex-1', muted && 'opacity-55')}>
          <p className="truncate text-[0.9375rem] font-medium">{transaction.merchant}</p>
          <p className="caption truncate">
            {isTransfer ? 'Transfer' : transaction.categoryName}
            {transaction.isRefund && ' · Refund'}
            {transaction.isExcluded && ' · Excluded'}
            {transaction.isReversal && ' · Reversed'}
            <span className="hidden sm:inline"> · {accountLabel(transaction.account.institution, transaction.account.mask)}</span>
          </p>
        </div>
        <div className={cn('text-right', muted && 'opacity-55')}>
          <p
            className={cn(
              'tabular text-[0.9375rem] font-medium',
              isIncome && 'text-positive',
              isTransfer && 'text-label-secondary',
              muted && 'line-through decoration-label-tertiary',
            )}
          >
            {isTransfer && <ArrowLeftRight size={13} className="mr-1 inline align-[-1px]" aria-label="Transfer" />}
            {formatMoney(transaction.amount, transaction.currency, { signed: transaction.amount > 0 })}
          </p>
          {transaction.categorySource === 'ai' && <p className="caption">AI categorized</p>}
        </div>
      </button>
    </li>
  );
}

export default function TransactionsPage() {
  const { period } = usePeriod();
  const prefs = usePreferences();
  const [params, setParams] = useSearchParams();
  const periodSummary = useSummary(period);
  const categories = useCategories();

  const [search, setSearch] = useState(params.get('q') ?? '');
  const deferredSearch = useDeferredValue(search);
  const [showFilters, setShowFilters] = useState(false);
  const [selected, setSelected] = useState<Transaction | null>(null);

  const typeFilter = (params.get('type') as TypeFilter | null) ?? 'all';
  const categoryId = params.get('category') ?? '';
  const groupId = params.get('group') ?? '';
  const sort = (params.get('sort') as Sort | null) ?? 'date-desc';

  function setParam(key: string, value: string) {
    setParams(
      (current) => {
        const next = new URLSearchParams(current);
        if (value) next.set(key, value);
        else next.delete(key);
        return next;
      },
      { replace: true },
    );
  }

  const filters: TransactionFilters = useMemo(
    () => ({
      search: deferredSearch.trim() || undefined,
      type: typeFilter === 'all' ? undefined : typeFilter,
      categoryId: categoryId || undefined,
      groupId: categoryId ? undefined : groupId || undefined,
      from: periodSummary.data?.period.start,
      to: periodSummary.data?.period.end,
      sort,
    }),
    [deferredSearch, typeFilter, categoryId, groupId, sort, periodSummary.data?.period.start, periodSummary.data?.period.end],
  );

  const transactions = useTransactions(filters);
  const pages = transactions.data?.pages ?? [];
  const items = pages.flatMap((p) => p.items);
  const first = pages[0];
  const groupName = categories.data?.find((g) => g.id === groupId)?.name;
  const activeFilterCount = [categoryId, groupId && !categoryId ? groupId : '', sort !== 'date-desc' ? sort : ''].filter(Boolean).length;

  // Group by day when sorted by date, like a bank app's activity list.
  const sections = useMemo(() => {
    if (!sort.startsWith('date')) return [{ key: 'all', title: null as string | null, items }];
    const map = new Map<string, Transaction[]>();
    for (const t of items) {
      map.set(t.date, [...(map.get(t.date) ?? []), t]);
    }
    return [...map.entries()].map(([date, list]) => ({ key: date, title: formatDate(date, prefs.dateFormat), items: list }));
  }, [items, sort, prefs.dateFormat]);

  return (
    <div>
      <PageHeader title="Transactions" subtitle={periodSummary.data?.period.label} actions={<PeriodPicker resolvedLabel={periodSummary.data?.period.label} />} />

      <div className="sticky top-0 z-20 -mx-4 mb-5 space-y-3 bg-canvas/85 px-4 pt-1 pb-3 backdrop-blur-xl sm:-mx-6 sm:px-6 lg:static lg:mx-0 lg:bg-transparent lg:px-0 lg:backdrop-blur-none">
        <div className="flex gap-2">
          <label className="relative flex-1">
            <span className="sr-only">Search transactions</span>
            <Search size={17} className="pointer-events-none absolute top-1/2 left-3.5 -translate-y-1/2 text-label-tertiary" aria-hidden="true" />
            <input
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search merchants, categories"
              className="h-10 w-full rounded-xl bg-fill pr-3 pl-10 text-[0.9375rem] placeholder:text-label-tertiary"
            />
          </label>
          <Button
            variant="secondary"
            aria-expanded={showFilters}
            icon={<SlidersHorizontal size={16} aria-hidden="true" />}
            onClick={() => setShowFilters((s) => !s)}
            className="rounded-xl"
          >
            <span className="hidden sm:inline">Filters</span>
            {activeFilterCount > 0 && <span className="rounded-full bg-accent px-1.5 text-[0.75rem] text-accent-contrast">{activeFilterCount}</span>}
          </Button>
        </div>

        <SegmentedControl
          label="Transaction type"
          size="sm"
          value={typeFilter}
          onChange={(value) => setParam('type', value === 'all' ? '' : value)}
          options={[
            { value: 'all', label: 'All' },
            { value: 'expense', label: 'Spending' },
            { value: 'income', label: 'Income' },
            { value: 'transfer', label: 'Transfers' },
          ]}
        />

        {showFilters && (
          <div className="grid gap-3 rounded-2xl bg-surface p-4 shadow-soft sm:grid-cols-2">
            <label className="caption flex flex-col gap-1.5">
              Category
              <select
                value={categoryId || (groupId ? `group:${groupId}` : '')}
                onChange={(e) => {
                  const value = e.target.value;
                  setParams(
                    (current) => {
                      const next = new URLSearchParams(current);
                      next.delete('category');
                      next.delete('group');
                      if (value.startsWith('group:')) next.set('group', value.slice(6));
                      else if (value) next.set('category', value);
                      return next;
                    },
                    { replace: true },
                  );
                }}
                className="h-10 rounded-xl bg-fill px-3 text-[0.9375rem] text-label"
              >
                <option value="">All categories</option>
                {categories.data?.map((group) => (
                  <optgroup key={group.id} label={group.name}>
                    <option value={`group:${group.id}`}>All {group.name}</option>
                    {group.categories.map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.name}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </select>
            </label>
            <label className="caption flex flex-col gap-1.5">
              Sort by
              <select value={sort} onChange={(e) => setParam('sort', e.target.value === 'date-desc' ? '' : e.target.value)} className="h-10 rounded-xl bg-fill px-3 text-[0.9375rem] text-label">
                <option value="date-desc">Newest first</option>
                <option value="date-asc">Oldest first</option>
                <option value="amount-desc">Largest amount</option>
                <option value="amount-asc">Smallest amount</option>
              </select>
            </label>
          </div>
        )}

        {(groupName || categoryId) && (
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => {
                setParam('group', '');
                setParam('category', '');
              }}
              className="inline-flex h-7 items-center gap-1 rounded-full bg-accent-soft pr-2 pl-3 text-[0.8125rem] font-medium text-accent"
            >
              {categoryId ? categories.data?.flatMap((g) => g.categories).find((c) => c.id === categoryId)?.name : groupName}
              <X size={14} aria-label="Clear category filter" />
            </button>
          </div>
        )}
      </div>

      {first && items.length > 0 && (
        <p className="caption tabular mb-3 px-1" aria-live="polite">
          {first.total} {first.total === 1 ? 'transaction' : 'transactions'} · {formatMoney(first.moneyIn, prefs.currency, { whole: true })} in ·{' '}
          {formatMoney(first.moneyOut, prefs.currency, { whole: true })} out
        </p>
      )}

      {transactions.isPending || periodSummary.isPending ? (
        <div className="card space-y-4 p-5" aria-busy="true" aria-label="Loading transactions">
          {Array.from({ length: 8 }, (_, i) => (
            <div key={i} className="flex items-center gap-3">
              <Skeleton className="size-9 rounded-full" />
              <div className="flex-1 space-y-1.5">
                <Skeleton className="h-3.5 w-40" />
                <Skeleton className="h-3 w-24" />
              </div>
              <Skeleton className="h-3.5 w-16" />
            </div>
          ))}
        </div>
      ) : transactions.isError ? (
        <div className="card">
          <ErrorState message={errorMessage(transactions.error)} onRetry={() => void transactions.refetch()} />
        </div>
      ) : items.length === 0 ? (
        <div className="card">
          <EmptyState
            icon={<Search size={24} aria-hidden="true" />}
            title={deferredSearch || activeFilterCount || typeFilter !== 'all' ? 'No matching transactions' : 'No transactions yet'}
            description={
              deferredSearch || activeFilterCount || typeFilter !== 'all'
                ? 'Try a different search or clear your filters.'
                : `There are no imported transactions for ${periodSummary.data?.period.label ?? 'this period'}.`
            }
          />
        </div>
      ) : (
        <div className={cn('space-y-6 transition-opacity', transactions.isPlaceholderData && 'opacity-60')}>
          {sections.map((section) => (
            <section key={section.key} aria-label={section.title ?? 'Transactions'}>
              {section.title && <h2 className="eyebrow mb-2 px-1">{section.title}</h2>}
              <ul className="card overflow-hidden [&>li+li]:shadow-[inset_0_0.5px_0_var(--separator)]">
                {section.items.map((t) => (
                  <TransactionRow key={t.id} transaction={t} onOpen={() => setSelected(t)} />
                ))}
              </ul>
            </section>
          ))}
          {transactions.hasNextPage && (
            <div className="flex justify-center">
              <Button variant="secondary" loading={transactions.isFetchingNextPage} onClick={() => void transactions.fetchNextPage()}>
                Show more
              </Button>
            </div>
          )}
        </div>
      )}

      <TransactionEditor transaction={selected} dateFormat={prefs.dateFormat} onClose={() => setSelected(null)} />
    </div>
  );
}
