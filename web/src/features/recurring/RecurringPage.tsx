import { CalendarClock, Repeat } from 'lucide-react';
import { useState } from 'react';
import { errorMessage } from '@/api/client';
import { useRecurring } from '@/api/queries';
import type { Recurring } from '@/api/schemas';
import { PageHeader } from '@/components/PageHeader';
import { Card, EmptyState, ErrorState, Pill, Skeleton } from '@/components/ui/primitives';
import { usePreferences } from '@/hooks/usePreferences';
import { cn } from '@/lib/cn';
import { CategoryGlyph, groupIdOf } from '@/lib/categories';
import { formatMoney, formatShortDate } from '@/lib/format';
import { toIsoDate } from '@/lib/period';

const FREQUENCY: Record<Recurring['frequency'], string> = {
  weekly: 'Weekly',
  biweekly: 'Every 2 weeks',
  monthly: 'Monthly',
  quarterly: 'Quarterly',
  annual: 'Yearly',
};

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
        <p className="truncate text-[0.9375rem] font-medium">{item.merchant}</p>
        <p className="caption truncate">
          {FREQUENCY[item.frequency]}
          {item.amountVaries && ' · amount varies'}
          {item.isActive ? ` · next ~${formatShortDate(item.nextExpectedDate, dateFormat)}` : ` · last ${formatShortDate(item.lastDate, dateFormat)}`}
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

export default function RecurringPage() {
  const recurring = useRecurring();
  const prefs = usePreferences();
  const data = recurring.data;
  const currency = data?.currency ?? prefs.currency;

  const [{ today, in30 }] = useState(() => {
    const now = new Date();
    return { today: toIsoDate(now), in30: toIsoDate(new Date(now.getTime() + 30 * 86_400_000)) };
  });
  const upcoming = (data?.items ?? [])
    .filter((i) => i.isActive && !i.isIncome && i.nextExpectedDate >= today && i.nextExpectedDate <= in30)
    .sort((a, b) => a.nextExpectedDate.localeCompare(b.nextExpectedDate));

  return (
    <div>
      <PageHeader title="Recurring" subtitle="Subscriptions, bills and regular income, detected from your transactions." />

      {recurring.isPending ? (
        <div className="space-y-6" aria-busy="true">
          <Skeleton className="h-36 w-full rounded-[20px]" />
          <Skeleton className="h-72 w-full rounded-[20px]" />
        </div>
      ) : recurring.isError || !data ? (
        <Card>
          <ErrorState message={errorMessage(recurring.error)} onRetry={() => void recurring.refetch()} />
        </Card>
      ) : data.items.length === 0 ? (
        <Card>
          <EmptyState
            icon={<Repeat size={24} aria-hidden="true" />}
            title="No recurring payments yet"
            description="Recurring payments are detected once a charge repeats across at least two statements."
          />
        </Card>
      ) : (
        <div className="space-y-8">
          <Card className="grid gap-6 p-6 sm:grid-cols-3 md:p-8">
            <div>
              <p className="eyebrow">Every month</p>
              <p className="figure-hero mt-1.5">{formatMoney(data.monthlyTotal, currency, { whole: true })}</p>
            </div>
            <div className="sm:pt-6">
              <p className="eyebrow">Per year</p>
              <p className="figure mt-1.5 text-[1.75rem]">{formatMoney(data.annualTotal, currency, { whole: true })}</p>
            </div>
            <div className="sm:pt-6">
              <p className="eyebrow">Subscriptions</p>
              <p className="figure mt-1.5 text-[1.75rem]">
                {formatMoney(data.monthlySubscriptions, currency, { whole: true })}
                <span className="text-[1rem] font-normal text-label-secondary">/mo</span>
              </p>
            </div>
            {data.aiReviewed && (
              <p className="caption sm:col-span-3">Detected from your transaction history. Labels such as subscription or bill were suggested by AI.</p>
            )}
          </Card>

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
                      <p className="mt-3 truncate text-[0.9375rem] font-medium">{item.merchant}</p>
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
            return (
              <section key={section.title} aria-labelledby={`section-${section.title}`}>
                <div className="mb-3 flex items-baseline justify-between px-1">
                  <h2 id={`section-${section.title}`} className="title-section">
                    {section.title}
                  </h2>
                  <p className="caption tabular">{formatMoney(monthly, currency, { whole: true })}/mo</p>
                </div>
                <ul className="card overflow-hidden [&>li+li]:shadow-[inset_0_0.5px_0_var(--separator)]">
                  {items.map((item) => (
                    <RecurringRow key={item.merchantKey + item.isIncome} item={item} currency={currency} dateFormat={prefs.dateFormat} />
                  ))}
                </ul>
                {items.some((i) => !i.isActive) && (
                  <p className="caption mt-2 px-1">
                    <Pill>Faded</Pill> items haven’t charged recently and may be cancelled.
                  </p>
                )}
              </section>
            );
          })}
        </div>
      )}
    </div>
  );
}
