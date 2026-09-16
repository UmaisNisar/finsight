import { motion } from 'motion/react';
import { ArrowDownRight, ArrowUpRight, FileUp, Mail, TriangleAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link, useLocation } from 'react-router';
import { errorMessage } from '@/api/client';
import { useAnalysis, useSession, useSummary } from '@/api/queries';
import type { SummaryResponse } from '@/api/schemas';
import { PageHeader } from '@/components/PageHeader';
import { PeriodPicker } from '@/components/PeriodPicker';
import { Card, EmptyState, ErrorState, Pill, SectionHeader, Skeleton } from '@/components/ui/primitives';
import { AiInsightCard, SavingsOpportunities } from '@/features/insights/AiComponents';
import { usePeriod } from '@/hooks/usePeriod';
import { cn } from '@/lib/cn';
import { CategoryGlyph, groupIdOf } from '@/lib/categories';
import { formatMoney, formatMonth, formatPercent, formatShortDate, greeting } from '@/lib/format';
import { isSingleMonth, trailingMonths, type PeriodSelection } from '@/lib/period';
import { usePreferences } from '@/hooks/usePreferences';
import { CashFlowChart } from './CashFlowChart';
import { SpendingBreakdown } from './SpendingBreakdown';

function Delta({ value, goodWhenUp, compareLabel }: { value: number | null; goodWhenUp: boolean; compareLabel: string }) {
  if (value === null || Math.abs(value) < 0.5) {
    return <span className="caption">{value === null ? `No data for ${compareLabel}` : `Same as ${compareLabel}`}</span>;
  }
  const up = value > 0;
  const good = up === goodWhenUp;
  const Icon = up ? ArrowUpRight : ArrowDownRight;
  return (
    <span className={cn('inline-flex items-center gap-0.5 text-[0.8125rem] font-medium', good ? 'text-positive' : 'text-label-secondary')}>
      <Icon size={14} strokeWidth={2.5} aria-hidden="true" />
      {formatPercent(Math.abs(value), { digits: 0 })}
      <span className="font-normal text-label-secondary">&nbsp;vs {compareLabel}</span>
    </span>
  );
}

function Figure({ label, value, children, emphasis }: { label: string; value: string; children?: ReactNode; emphasis?: boolean }) {
  return (
    <div className="min-w-0">
      <dt className="eyebrow">{label}</dt>
      <dd className="m-0">
        <p className={cn('mt-1.5 truncate', emphasis ? 'figure-hero' : 'figure text-[1.75rem] md:text-[2.125rem]')}>{value}</p>
        <div className="mt-1.5 min-h-5">{children}</div>
      </dd>
    </div>
  );
}

function HeroSummary({ data }: { data: SummaryResponse }) {
  const { summary, currency } = data;
  const compareLabel = isSingleMonth(summary.previous.range.start, summary.previous.range.end) ? formatMonth(summary.previous.range.start, 'long') : 'previous period';
  const net = summary.netCashFlow;

  return (
    <Card className="p-6 md:p-8" aria-labelledby="summary-title">
      <h2 id="summary-title" className="sr-only">
        Summary
      </h2>
      <dl className="grid grid-cols-2 gap-x-6 gap-y-7 md:grid-cols-4">
        <Figure label="Income" value={formatMoney(summary.income, currency, { whole: true })}>
          <Delta value={summary.previous.hasData ? summary.previous.incomeChangePercent : null} goodWhenUp compareLabel={compareLabel} />
        </Figure>
        <Figure label="Spent" value={formatMoney(summary.expenses, currency, { whole: true })}>
          <Delta value={summary.previous.hasData ? summary.previous.expenseChangePercent : null} goodWhenUp={false} compareLabel={compareLabel} />
        </Figure>
        <Figure label={net >= 0 ? 'Saved' : 'Overspent'} value={formatMoney(Math.abs(net), currency, { whole: true })}>
          {summary.refunds > 0 && <span className="caption">After {formatMoney(summary.refunds, currency, { whole: true })} in refunds</span>}
        </Figure>
        <Figure label="Savings rate" value={summary.savingsRate === null ? '—' : formatPercent(summary.savingsRate)}>
          {summary.savingsRate === null ? (
            <span className="caption">No income recorded</span>
          ) : (
            <div
              className="mt-2 h-1.5 w-full max-w-36 overflow-hidden rounded-full bg-fill"
              role="presentation"
            >
              <div className="h-full rounded-full bg-positive" style={{ width: `${Math.max(0, Math.min(100, summary.savingsRate))}%` }} />
            </div>
          )}
        </Figure>
      </dl>
      {summary.transfers.count > 0 && (
        <p className="caption mt-6 border-t border-separator pt-4">
          {formatMoney(summary.transfers.total, currency, { whole: true })} moved between your own accounts, including card payments, isn’t counted as spending.
        </p>
      )}
    </Card>
  );
}

function OverviewSkeleton() {
  return (
    <div aria-busy="true" aria-label="Loading overview" className="space-y-6">
      <Skeleton className="h-44 w-full rounded-[20px]" />
      <Skeleton className="h-32 w-full rounded-[20px]" />
      <div className="grid gap-6 lg:grid-cols-5">
        <Skeleton className="h-80 rounded-[20px] lg:col-span-3" />
        <Skeleton className="h-80 rounded-[20px] lg:col-span-2" />
      </div>
    </div>
  );
}

function Onboarding({ canUseGmail }: { canUseGmail: boolean }) {
  return (
    <Card className="px-6 py-4">
      <EmptyState
        icon={<Mail size={26} aria-hidden="true" />}
        title={canUseGmail ? 'Connect your Gmail' : 'Add your first statement'}
        description={
          canUseGmail
            ? 'FinSight can automatically find your bank statements and analyze your spending. You choose which statements to import.'
            : 'Upload a PDF bank or credit card statement and FinSight will extract and categorize every transaction.'
        }
        action={
          <>
            {canUseGmail && (
              <Link to="/statements?connect=1" className="inline-flex h-10 items-center gap-2 rounded-full bg-accent px-5 text-[0.9375rem] font-medium text-accent-contrast">
                <Mail size={16} aria-hidden="true" />
                Connect Gmail
              </Link>
            )}
            <Link to="/statements?upload=1" className="inline-flex h-10 items-center gap-2 rounded-full bg-fill px-5 text-[0.9375rem] font-medium">
              <FileUp size={16} aria-hidden="true" />
              Upload a PDF
            </Link>
          </>
        }
      />
    </Card>
  );
}

function subtitleFor(data: SummaryResponse | undefined, period: PeriodSelection): string {
  if (!data) return 'Loading your financial picture…';
  const { start, end } = data.period;
  if (isSingleMonth(start, end)) {
    return `Here’s your financial picture for ${formatMonth(start, 'long')}.`;
  }
  if (period.preset === 'custom') {
    return `Here’s your financial picture for ${data.period.label}.`;
  }
  return `Here’s your financial picture for the ${data.period.label.toLowerCase().includes('–') ? data.period.label : `last ${data.summary.coverage.monthsInRange} months`}.`;
}

export default function OverviewPage() {
  const { period } = usePeriod();
  const session = useSession();
  const prefs = usePreferences();
  const location = useLocation();
  const summary = useSummary(period);
  const trendPeriod = summary.data ? trailingMonths(summary.data.period.end) : null;
  const trend = useSummary(trendPeriod ?? period, trendPeriod !== null);
  const analysis = useAnalysis(period);

  const name = session.data?.user?.name;
  const data = summary.data;
  const currency = data?.currency ?? prefs.currency;
  const linkSuffix = location.search ? `&${location.search.slice(1)}` : '';

  return (
    <div>
      <PageHeader title={name ? `${greeting()}, ${name.split(' ')[0]}` : greeting()} subtitle={subtitleFor(data, period)} actions={<PeriodPicker resolvedLabel={data?.period.label} />} />

      {summary.isPending ? (
        <OverviewSkeleton />
      ) : summary.isError ? (
        <Card>
          <ErrorState message={errorMessage(summary.error)} onRetry={() => void summary.refetch()} />
        </Card>
      ) : !data?.hasAnyData ? (
        <Onboarding canUseGmail={session.data?.capabilities.gmail ?? false} />
      ) : data.summary.transactionCount === 0 ? (
        <Card className="px-6">
          <EmptyState
            title={`No transactions for ${data.period.label}`}
            description={
              data.latestTransactionDate
                ? `Your most recent imported transaction is from ${formatShortDate(data.latestTransactionDate, prefs.dateFormat)}. Statements usually arrive a few days after a month ends.`
                : 'Import a statement that covers this period.'
            }
            action={
              <Link to="/statements" className="inline-flex h-10 items-center rounded-full bg-fill px-5 text-[0.9375rem] font-medium">
                View statements
              </Link>
            }
          />
        </Card>
      ) : (
        <motion.div initial={{ opacity: 0 }} animate={{ opacity: summary.isPlaceholderData ? 0.6 : 1 }} transition={{ duration: 0.2 }} className="space-y-6">
          {data.summary.coverage.isPartial && (
            <p className="flex items-start gap-2 rounded-2xl bg-attention-soft px-4 py-3 text-[0.9375rem]">
              <TriangleAlert size={17} className="mt-0.5 shrink-0 text-attention" aria-hidden="true" />
              <span>
                Only {data.summary.coverage.monthsWithData} of {data.summary.coverage.monthsInRange} months in this period have statements. Totals include imported months only.
              </span>
            </p>
          )}

          <HeroSummary data={data} />

          <AiInsightCard period={period} hasData compact linkSuffix={location.search} />

          <div className="grid gap-6 lg:grid-cols-5">
            <Card className="p-6 lg:col-span-3" aria-labelledby="cashflow-title">
              <SectionHeader id="cashflow-title" title="Cash flow" subtitle="Income and spending, last 6 months" />
              {trend.data ? <CashFlowChart monthly={trend.data.summary.monthly} currency={currency} /> : <Skeleton className="h-64 w-full" />}
            </Card>

            <Card className="p-6 lg:col-span-2" aria-labelledby="breakdown-title">
              <SectionHeader id="breakdown-title" title="Where your money went" subtitle={`${formatMoney(data.summary.expenses, currency, { whole: true })} across ${data.summary.groups.length} categories`} />
              <SpendingBreakdown
                groups={data.summary.groups}
                currency={currency}
                compareLabel={formatMonth(data.summary.previous.range.start, 'long')}
                linkSuffix={linkSuffix}
              />
            </Card>
          </div>

          {analysis.data?.analysis && analysis.data.analysis.savingsOpportunities.length > 0 && (
            <Card className="p-6" aria-labelledby="savings-title">
              <SectionHeader
                id="savings-title"
                title="Savings opportunities"
                action={
                  <Link to={`/insights${location.search}`} className="text-[0.9375rem] text-accent">
                    All insights
                  </Link>
                }
              />
              <SavingsOpportunities items={analysis.data.analysis.savingsOpportunities} currency={currency} limit={2} />
            </Card>
          )}

          <div className="grid gap-6 md:grid-cols-2">
            <Card className="p-6" aria-labelledby="largest-title">
              <SectionHeader id="largest-title" title="Largest expenses" />
              <ul className="space-y-1">
                {data.summary.largestExpenses.slice(0, 5).map((t) => (
                  <li key={t.id} className="flex items-center gap-3 py-1.5">
                    <CategoryGlyph groupId={groupIdOf(t.categoryId)} size={32} />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[0.9375rem]">{t.merchant}</p>
                      <p className="caption">{formatShortDate(t.date, prefs.dateFormat)}</p>
                    </div>
                    <p className="tabular text-[0.9375rem] font-medium">{formatMoney(t.amount, currency)}</p>
                  </li>
                ))}
              </ul>
            </Card>

            <Card className="p-6" aria-labelledby="attention-title">
              <SectionHeader
                id="attention-title"
                title="Worth a look"
                action={
                  <Link to={`/recurring${location.search}`} className="text-[0.9375rem] text-accent">
                    Recurring
                  </Link>
                }
              />
              {data.anomalies.length === 0 ? (
                <p className="text-[0.9375rem] text-label-secondary">Nothing unusual this period.</p>
              ) : (
                <ul className="space-y-1">
                  {data.anomalies.slice(0, 4).map((a) => (
                    <li key={a.transactionId} className="flex items-center gap-3 py-1.5">
                      <CategoryGlyph groupId={groupIdOf(a.categoryId)} size={32} />
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-[0.9375rem]">{a.merchant}</p>
                        <p className="caption">
                          {a.kind === 'possibleDuplicate'
                            ? 'Possible duplicate charge'
                            : a.kind === 'newMerchant'
                              ? 'Large payment to a new merchant'
                              : `About ${Math.round(a.amount / (a.typicalAmount ?? a.amount))}× your usual ${a.categoryName.toLowerCase()}`}
                        </p>
                      </div>
                      <Pill tone="attention">{formatMoney(a.amount, currency, { whole: true })}</Pill>
                    </li>
                  ))}
                </ul>
              )}
            </Card>
          </div>
        </motion.div>
      )}
    </div>
  );
}
