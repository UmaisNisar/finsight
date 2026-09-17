import { AnimatePresence, motion } from 'motion/react';
import { ArrowLeftRight, Search, SlidersHorizontal, X } from 'lucide-react';
import { useDeferredValue, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { errorMessage } from '@/api/client';
import type { TransactionFilters } from '@/api/endpoints';
import { useCategories, useHasAnyData, useSummary, useTransactions } from '@/api/queries';
import type { Transaction } from '@/api/schemas';
import { PageHeader } from '@/components/PageHeader';
import { PeriodPicker } from '@/components/PeriodPicker';
import { Collapse } from '@/components/ui/AutoHeight';
import { Button } from '@/components/ui/Button';
import { PopUpButton } from '@/components/ui/PopUpButton';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { Card, EmptyState, ErrorState, GroupedList, RowSkeleton, Skeleton } from '@/components/ui/primitives';
import { usePeriod } from '@/hooks/usePeriod';
import { usePreferences } from '@/hooks/usePreferences';
import { cn } from '@/lib/cn';
import { CategoryGlyph } from '@/lib/categories';
import { accountLabel, formatDate, formatMoney } from '@/lib/format';
import { TransactionEditor } from './TransactionEditor';

const TYPES = [
  { value: 'all', label: 'All' },
  { value: 'expense', label: 'Spending' },
  { value: 'income', label: 'Income' },
  { value: 'transfer', label: 'Transfers' },
] as const;

const SORTS = [
  { value: 'date-desc', label: 'Newest first' },
  { value: 'date-asc', label: 'Oldest first' },
  { value: 'amount-desc', label: 'Largest amount' },
  { value: 'amount-asc', label: 'Smallest amount' },
] as const;

type TypeFilter = (typeof TYPES)[number]['value'];
type Sort = (typeof SORTS)[number]['value'];

/** Reads an enum from the URL, ignoring values that aren't allowed (a hand-edited or stale link). */
function pick<T extends string>(value: string | null, allowed: readonly { value: T }[], fallback: T): T {
  return allowed.find((option) => option.value === value)?.value ?? fallback;
}

function TransactionRow({ transaction, onOpen }: { transaction: Transaction; onOpen: () => void }) {
  const isTransfer = transaction.type === 'transfer';
  const isIncome = transaction.type === 'income' && transaction.amount > 0;
  const muted = transaction.isExcluded || transaction.isReversal;

  return (
    <li>
      <button type="button" onClick={onOpen} className="flex w-full items-center gap-3.5 px-4 py-3 text-left transition-colors hover:bg-fill focus-visible:bg-fill md:px-5">
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
          <p className={cn('tabular text-[0.9375rem] font-medium', isIncome && 'text-positive', isTransfer && 'text-label-secondary', muted && 'line-through decoration-label-tertiary')}>
            {isTransfer && <ArrowLeftRight size={13} className="mr-1 inline align-[-1px]" aria-hidden="true" />}
            {formatMoney(transaction.amount, transaction.currency, { signed: transaction.amount > 0 })}
          </p>
          {transaction.categorySource === 'ai' && <p className="caption">AI categorized</p>}
        </div>
      </button>
    </li>
  );
}

/** Day sections of placeholder rows, laid out exactly like the real list. */
function ListSkeleton() {
  return (
    <div className="space-y-6" aria-hidden="true">
      {[3, 4, 2].map((rows, i) => (
        <div key={i}>
          <Skeleton className="mx-1 mb-2 h-3 w-24" />
          <div className="card grouped overflow-hidden">
            {Array.from({ length: rows }, (_, j) => (
              <RowSkeleton key={j} className="px-4 py-3 md:px-5" />
            ))}
          </div>
        </div>
      ))}
    </div>
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

  const typeFilter = pick<TypeFilter>(params.get('type'), TYPES, 'all');
  const sort = pick<Sort>(params.get('sort'), SORTS, 'date-desc');
  const categoryId = params.get('category') ?? '';
  const groupId = categoryId ? '' : (params.get('group') ?? '');

  function updateParams(patch: Record<string, string>) {
    // One update per interaction: setSearchParams calls don't queue, so a second call would overwrite the first.
    setParams(
      (current) => {
        const next = new URLSearchParams(current);
        for (const [key, value] of Object.entries(patch)) {
          if (value) next.set(key, value);
          else next.delete(key);
        }
        return next;
      },
      { replace: true },
    );
  }

  // The server resolves the period's dates. Until the current period's range is known, keep showing the
  // previous list (dimmed) rather than fetching an unfiltered one.
  const range = periodSummary.isPlaceholderData ? undefined : periodSummary.data?.period;
  const filters: TransactionFilters = useMemo(
    () => ({
      search: deferredSearch.trim() || undefined,
      type: typeFilter === 'all' ? undefined : typeFilter,
      categoryId: categoryId || undefined,
      groupId: groupId || undefined,
      from: range?.start,
      to: range?.end,
      sort,
    }),
    [deferredSearch, typeFilter, categoryId, groupId, sort, range?.start, range?.end],
  );

  const transactions = useTransactions(filters, range !== undefined);
  const pages = transactions.data?.pages ?? [];
  const items = pages.flatMap((p) => p.items);
  const first = pages[0];
  // An account with nothing imported goes straight to the empty state; a list-shaped skeleton would collapse into it.
  const noData = useHasAnyData() === false;
  const loading = transactions.isPending && !noData;
  const refreshing = transactions.isPlaceholderData || periodSummary.isPlaceholderData || (search !== deferredSearch && !loading);
  const currency = periodSummary.data?.currency ?? prefs.currency;

  const categoryName = categoryId
    ? categories.data?.flatMap((g) => g.categories).find((c) => c.id === categoryId)?.name
    : categories.data?.find((g) => g.id === groupId)?.name;
  const activeFilterCount = [categoryId || groupId, sort !== 'date-desc' ? sort : ''].filter(Boolean).length;
  const filtered = deferredSearch.trim() !== '' || activeFilterCount > 0 || typeFilter !== 'all';

  // Group by day when sorted by date, like a bank app's activity list.
  const sections = useMemo(() => {
    if (!sort.startsWith('date')) return [{ key: 'all', title: null as string | null, items }];
    const map = new Map<string, Transaction[]>();
    for (const t of items) {
      const list = map.get(t.date);
      if (list) list.push(t);
      else map.set(t.date, [t]);
    }
    return [...map.entries()].map(([date, list]) => ({ key: date, title: formatDate(date, prefs.dateFormat), items: list }));
  }, [items, sort, prefs.dateFormat]);

  function clearFilters() {
    setSearch('');
    updateParams({ type: '', category: '', group: '', sort: '' });
  }

  return (
    <div>
      <PageHeader
        title="Transactions"
        subtitle={periodSummary.data ? periodSummary.data.period.label : periodSummary.isPending ? 'loading' : undefined}
        actions={<PeriodPicker resolvedLabel={periodSummary.data?.period.label} />}
      />

      <div className="scroll-edge sticky top-0 isolate z-20 -mx-4 mb-3 space-y-3 px-4 pt-1 pb-4 sm:-mx-6 sm:px-6 lg:static lg:mx-0 lg:px-0">
        <div className="flex gap-2">
          <label className="relative flex-1">
            <span className="sr-only">Search transactions</span>
            {/* Stacked above the field: the glass input forms its own stacking layer and would otherwise cover the icon. */}
            <Search size={17} className="pointer-events-none absolute top-1/2 left-3.5 z-10 -translate-y-1/2 text-label-tertiary" aria-hidden="true" />
            <input
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search merchants, categories"
              className="glass-control h-10 w-full rounded-full pr-3 pl-10 text-[0.9375rem] placeholder:text-label-tertiary"
            />
          </label>
          <Button
            variant="secondary"
            aria-expanded={showFilters}
            aria-controls="transaction-filters"
            aria-label={activeFilterCount > 0 ? `Filters, ${activeFilterCount} active` : 'Filters'}
            icon={<SlidersHorizontal size={16} aria-hidden="true" />}
            onClick={() => setShowFilters((s) => !s)}
          >
            <span className="hidden sm:inline">Filters</span>
            {activeFilterCount > 0 && <span className="min-w-5 rounded-full bg-accent px-1.5 text-[0.75rem] text-accent-contrast">{activeFilterCount}</span>}
          </Button>
        </div>

        <SegmentedControl label="Transaction type" size="sm" value={typeFilter} onChange={(value) => updateParams({ type: value === 'all' ? '' : value })} options={[...TYPES]} />

        <Collapse open={showFilters} id="transaction-filters">
          <div className="card grid gap-3 rounded-2xl p-4 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <span id="filter-category-label" className="caption">
                Category
              </span>
              <PopUpButton
                variant="field"
                labelledBy="filter-category-label"
                searchable
                searchPlaceholder="Search categories"
                value={categoryId || (groupId ? `group:${groupId}` : 'all')}
                disabled={!categories.data}
                onChange={(value) =>
                  updateParams({ category: value.startsWith('group:') || value === 'all' ? '' : value, group: value.startsWith('group:') ? value.slice(6) : '' })
                }
                options={[
                  { title: '', options: [{ value: 'all', label: 'All categories' }] },
                  ...(categories.data ?? []).map((group) => ({
                    title: group.name,
                    options: [{ value: `group:${group.id}`, label: `All ${group.name}` }, ...group.categories.map((c) => ({ value: c.id, label: c.name }))],
                  })),
                ]}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <span id="filter-sort-label" className="caption">
                Sort by
              </span>
              <PopUpButton<Sort> variant="field" labelledBy="filter-sort-label" value={sort} onChange={(value) => updateParams({ sort: value === 'date-desc' ? '' : value })} options={[...SORTS]} />
            </div>
          </div>
        </Collapse>

        <AnimatePresence initial={false}>
          {categoryName && (
            <motion.div key="chip" initial={{ opacity: 0, scale: 0.9 }} animate={{ opacity: 1, scale: 1 }} exit={{ opacity: 0, scale: 0.9 }} transition={{ duration: 0.16 }} className="flex origin-left">
              <button
                type="button"
                aria-label={`Clear category filter: ${categoryName}`}
                onClick={() => updateParams({ category: '', group: '' })}
                className="inline-flex h-7 items-center gap-1 rounded-full bg-accent-soft pr-2 pl-3 text-[0.8125rem] font-medium text-accent transition-transform active:scale-[0.96]"
              >
                {categoryName}
                <X size={14} aria-hidden="true" />
              </button>
            </motion.div>
          )}
        </AnimatePresence>
      </div>

      {/* The totals line keeps its height through loading, so the list never shifts when it arrives. */}
      <p className="caption tabular mb-3 flex h-5 items-center px-1" aria-live="polite">
        {loading ? (
          <Skeleton className="h-3 w-56" />
        ) : (
          first &&
          !transactions.isPending &&
          !transactions.isError && (
            <span className="fade-in">
              {first.total} {first.total === 1 ? 'transaction' : 'transactions'}
              {first.total > 0 && ` · ${formatMoney(first.moneyIn, currency, { whole: true })} in · ${formatMoney(first.moneyOut, currency, { whole: true })} out`}
            </span>
          )
        )}
      </p>

      {loading ? (
        <ListSkeleton />
      ) : transactions.isError ? (
        <Card>
          <ErrorState message={errorMessage(transactions.error)} onRetry={() => void transactions.refetch()} />
        </Card>
      ) : items.length === 0 ? (
        <Card data-pending={refreshing}>
          <EmptyState
            icon={<Search size={24} aria-hidden="true" />}
            title={filtered ? 'No matching transactions' : 'No transactions in this period'}
            description={filtered ? 'Try a different search, or clear your filters.' : `There are no imported transactions for ${periodSummary.data?.period.label ?? 'this period'}.`}
            action={
              filtered && (
                <Button variant="secondary" onClick={clearFilters}>
                  Clear filters
                </Button>
              )
            }
          />
        </Card>
      ) : (
        <div className="space-y-6" data-pending={refreshing} aria-busy={refreshing}>
          {sections.map((section) => (
            <section key={section.key} aria-label={section.title ?? 'Transactions'}>
              {section.title && <h2 className="eyebrow mb-2 px-1">{section.title}</h2>}
              <GroupedList>
                {section.items.map((t) => (
                  <TransactionRow key={t.id} transaction={t} onOpen={() => setSelected(t)} />
                ))}
              </GroupedList>
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
