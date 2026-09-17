import { CalendarClock, Repeat } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { errorMessage } from '@/api/client';
import { useHasAnyData, useRecurring } from '@/api/queries';
import type { Recurring } from '@/api/schemas';
import { PageHeader } from '@/components/PageHeader';
import { AnimatedNumber, useRevealedAfterLoading } from '@/components/ui/AnimatedNumber';
import { Card, EmptyState, ErrorState, GroupedList, RowSkeleton, Skeleton } from '@/components/ui/primitives';
import { TruncatedText } from '@/components/ui/Tooltip';
import { usePreferences } from '@/hooks/usePreferences';
import { cn } from '@/lib/cn';
import { CategoryGlyph, groupIdOf } from '@/lib/categories';
import { formatMoney, formatShortDate } from '@/lib/format';
import { FREQUENCY_LABEL } from '@/lib/labels';
import { localIsoDate } from '@/lib/period';

const SECTIONS: { kind: Recurring['kind'][]; title: string }[] = [
  { kind: ['subscription'], title: 'Subscriptions' },
  { kind: ['bill', 'loan'], title: 'Bills' },
  { kind: ['membership'], title: 'Memberships' },
  { kind: ['habit', 'other'], title: 'Other regular spending' },
  { kind: ['income'], title: 'Regular income' },
];

function RecurringRow({ item, currency, dateFormat }: { item: Recurring; currency: string; dateFormat: string }) {
  return (
    <li className={cn('flex items-center gap-3.5 px-4 py-3 md:px-5', !item.isActive && 'opacity-55')}>
      <CategoryGlyph groupId={item.isIncome ? 'income' : groupIdOf(item.categoryId)} type={item.isIncome ? 'income' : 'expense'} size={38} />
      <div className="min-w-0 flex-1">
        <TruncatedText as="p" className="text-[0.9375rem] font-medium">
          {item.merchant}
        </TruncatedText>
        <p className="caption truncate">
          {FREQUENCY_LABEL[item.frequency]}
          {item.amountVaries && ' · amount varies'}
          {item.isActive ? ` · next ~${formatShortDate(item.nextExpectedDate, dateFormat)}` : ` · last ${formatShortDate(item.lastDate, dateFormat)}, may be cancelled`}
        </p>
      </div>
      <div className="text-right">
        <p className={cn('tabular text-[0.9375rem] font-medium', item.isIncome && 'text-positive')}>
          {item.amountVaries && '~'}
          {formatMoney(item.amount, currency)}
        </p>
        {item.frequency !== 'monthly' && !item.isIncome && <p className="caption tabular">{formatMoney(item.monthlyEquivalent, currency)}/mo</p>}
      </div>
    </li>
  );
}

function Total({ label, value, hero, suffix, className, currency, countUp }: { label: string; value?: number; hero?: boolean; suffix?: ReactNode; className?: string; currency: string; countUp: boolean }) {
  return (
    <div className={className}>
      <p className="eyebrow">{label}</p>
      <p className={cn('mt-1.5', hero ? 'figure-hero' : 'figure text-[1.75rem]')}>
      {value === undefined ? (
        <Skeleton className="h-[1em] w-[4.5ch] rounded-xl" />
      ) : (
        <>
          <AnimatedNumber value={value} format={(amount) => formatMoney(amount, currency, { whole: true })} countUp={countUp && hero} />
          {suffix}
        </>
      )}
      </p>
    </div>
  );
}

function ListSkeleton() {
  return (
    <div className="space-y-8" aria-hidden="true">
      {[4, 3].map((rows, i) => (
        <div key={i}>
          <div className="mb-3 flex h-6 items-center justify-between px-1">
            <Skeleton className="h-5 w-32" />
            <Skeleton className="h-3 w-16" />
          </div>
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

export default function RecurringPage() {
  const recurring = useRecurring();
  const prefs = usePreferences();
  const data = recurring.data;
  const currency = data?.currency ?? prefs.currency;

  const [{ today, in30 }] = useState(() => {
    const now = new Date();
    return { today: localIsoDate(now), in30: localIsoDate(new Date(now.getFullYear(), now.getMonth(), now.getDate() + 30)) };
  });
  const upcoming = (data?.items ?? [])
    .filter((i) => i.isActive && !i.isIncome && i.nextExpectedDate >= today && i.nextExpectedDate <= in30)
    .sort((a, b) => a.nextExpectedDate.localeCompare(b.nextExpectedDate));

  const countUp = useRevealedAfterLoading(data !== undefined);
  const noData = useHasAnyData() === false;
  // Known empty before the list arrives when the account has nothing imported, so no skeleton collapses into the empty card.
  const empty = data ? data.items.length === 0 : noData;

  return (
    <div>
      <PageHeader title="Recurring" subtitle="Subscriptions, bills and regular income, detected from your transactions." />

      {recurring.isError ? (
        <Card>
          <ErrorState message={errorMessage(recurring.error)} onRetry={() => void recurring.refetch()} />
        </Card>
      ) : empty ? (
        <Card>
          <EmptyState
            icon={<Repeat size={24} aria-hidden="true" />}
            title="No recurring payments yet"
            description="Recurring payments are detected once a charge repeats across at least two statements."
          />
        </Card>
      ) : (
        <div className="space-y-8">
          <Card className="grid gap-6 p-6 sm:grid-cols-3 md:p-8" aria-label="Recurring totals" aria-busy={!data}>
            <Total label="Every month" hero value={data?.monthlyTotal} currency={currency} countUp={countUp} />
            <Total label="Per year" className="sm:pt-6" value={data?.annualTotal} currency={currency} countUp={countUp} />
            <Total
              label="Subscriptions"
              className="sm:pt-6"
              value={data?.monthlySubscriptions}
              currency={currency}
              countUp={countUp}
              suffix={<span className="text-[1rem] font-normal text-label-secondary">/mo</span>}
            />
            {data?.aiReviewed && <p className="caption fade-in sm:col-span-3">Detected from your transaction history. Labels such as subscription or bill were suggested by AI.</p>}
          </Card>

          {!data ? (
            <ListSkeleton />
          ) : (
            <div className="space-y-8">
              {upcoming.length > 0 && (
                <section aria-labelledby="upcoming-title">
                  <h2 id="upcoming-title" className="title-section mb-3 flex items-center gap-2 px-1">
                    <CalendarClock size={20} className="text-label-secondary" aria-hidden="true" />
                    Next 30 days
                  </h2>
                  <div className="-mx-4 overflow-x-auto px-4 pb-1 sm:mx-0 sm:px-0">
                    <ul className="flex gap-3">
                      {upcoming.map((item) => (
                        <li key={item.merchantKey} className="card w-44 shrink-0 p-4">
                          <CategoryGlyph groupId={groupIdOf(item.categoryId)} size={32} />
                          <TruncatedText as="p" className="mt-3 text-[0.9375rem] font-medium">
                            {item.merchant}
                          </TruncatedText>
                          <p className="caption">{formatShortDate(item.nextExpectedDate, prefs.dateFormat)}</p>
                          <p className="tabular mt-2 text-[1.0625rem] font-semibold">{formatMoney(item.amount, currency)}</p>
                        </li>
                      ))}
                    </ul>
                  </div>
                </section>
              )}

              {SECTIONS.map((section) => {
                const items = data.items.filter((i) => section.kind.includes(i.kind)).sort((a, b) => Number(b.isActive) - Number(a.isActive) || b.monthlyEquivalent - a.monthlyEquivalent);
                if (items.length === 0) return null;
                const monthly = items.filter((i) => i.isActive).reduce((sum, i) => sum + i.monthlyEquivalent, 0);
                const id = `section-${section.kind[0]}`;
                return (
                  <section key={section.title} aria-labelledby={id}>
                    <div className="mb-3 flex items-baseline justify-between px-1">
                      <h2 id={id} className="title-section">
                        {section.title}
                      </h2>
                      <p className="caption tabular">{formatMoney(monthly, currency, { whole: true })}/mo</p>
                    </div>
                    <GroupedList>
                      {items.map((item) => (
                        <RecurringRow key={item.merchantKey + item.isIncome} item={item} currency={currency} dateFormat={prefs.dateFormat} />
                      ))}
                    </GroupedList>
                  </section>
                );
              })}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
