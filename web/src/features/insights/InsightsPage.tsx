import { ArrowDownRight, ArrowUpRight, CircleAlert, Lightbulb, Sparkles, ThumbsUp, Info } from 'lucide-react';
import { Link, useLocation } from 'react-router';
import { errorMessage } from '@/api/client';
import { useAnalysis, useSummary } from '@/api/queries';
import type { Analysis } from '@/api/schemas';
import { PageHeader } from '@/components/PageHeader';
import { PeriodPicker } from '@/components/PeriodPicker';
import { Card, EmptyState, ErrorState, SectionHeader, Skeleton } from '@/components/ui/primitives';
import { usePeriod } from '@/hooks/usePeriod';
import { usePreferences } from '@/hooks/usePreferences';
import { cn } from '@/lib/cn';
import { CategoryGlyph, groupIdOf, groupStyle } from '@/lib/categories';
import { formatMoney, formatPercent, formatShortDate } from '@/lib/format';
import { trailingMonths } from '@/lib/period';
import { AiInsightCard, AnalysisCorrections, SavingsOpportunities } from './AiComponents';
import { SavingsRateChart } from './SavingsRateChart';

const SEVERITY = {
  positive: { icon: ThumbsUp, className: 'bg-positive-soft text-positive', label: 'Good news' },
  attention: { icon: CircleAlert, className: 'bg-attention-soft text-attention', label: 'Worth attention' },
  info: { icon: Info, className: 'bg-accent-soft text-accent', label: 'Observation' },
} as const;

function KeyInsights({ items }: { items: Analysis['keyInsights'] }) {
  return (
    <ul className="grid gap-3 md:grid-cols-2">
      {items.map((insight) => {
        const style = SEVERITY[insight.severity];
        const Icon = style.icon;
        return (
          <li key={insight.title} className="card flex gap-3.5 p-5">
            <span className={cn('flex size-9 shrink-0 items-center justify-center rounded-full', style.className)}>
              <Icon size={17} aria-hidden="true" />
              <span className="sr-only">{style.label}</span>
            </span>
            <div>
              <h3 className="text-[0.9375rem] font-semibold">{insight.title}</h3>
              <p className="mt-1 text-[0.9375rem] text-label-secondary">{insight.description}</p>
            </div>
          </li>
        );
      })}
    </ul>
  );
}

export default function InsightsPage() {
  const { period } = usePeriod();
  const prefs = usePreferences();
  const location = useLocation();
  const summary = useSummary(period);
  const analysis = useAnalysis(period);
  const trend = useSummary(summary.data ? trailingMonths(summary.data.period.end, 12) : period, summary.data !== undefined);

  const data = summary.data;
  const currency = data?.currency ?? prefs.currency;
  const ai = analysis.data?.analysis ?? null;

  const changes = (data?.summary.categories ?? [])
    .filter((c) => c.changePercent !== null && Math.abs(c.amount - c.previousAmount) >= 25)
    .sort((a, b) => Math.abs(b.amount - b.previousAmount) - Math.abs(a.amount - a.previousAmount))
    .slice(0, 5);

  return (
    <div>
      <PageHeader title="Insights" subtitle={data ? `What stands out in ${data.period.label}` : undefined} actions={<PeriodPicker resolvedLabel={data?.period.label} />} />

      {summary.isPending ? (
        <div className="space-y-6" aria-busy="true">
          <Skeleton className="h-40 w-full rounded-[20px]" />
          <Skeleton className="h-64 w-full rounded-[20px]" />
        </div>
      ) : summary.isError || !data ? (
        <Card>
          <ErrorState message={errorMessage(summary.error)} onRetry={() => void summary.refetch()} />
        </Card>
      ) : data.summary.transactionCount === 0 ? (
        <Card>
          <EmptyState icon={<Sparkles size={24} aria-hidden="true" />} title={`Nothing to analyze for ${data.period.label}`} description="Import statements that cover this period, or choose a different time range." />
        </Card>
      ) : (
        <div className="space-y-10">
          <section aria-label="Financial summary" className="space-y-4">
            <AiInsightCard period={period} hasData />
            {analysis.data?.analysis && <AnalysisCorrections response={analysis.data} />}
          </section>

          {ai && ai.keyInsights.length > 0 && (
            <section aria-labelledby="key-title">
              <SectionHeader id="key-title" title="Key insights" />
              <KeyInsights items={ai.keyInsights} />
            </section>
          )}

          <section aria-labelledby="where-title">
            <SectionHeader id="where-title" title="Where your money goes" subtitle={`${formatMoney(data.summary.expenses, currency, { whole: true })} spent · ${formatMoney(data.summary.fixedExpenses, currency, { whole: true })} fixed, ${formatMoney(data.summary.variableExpenses, currency, { whole: true })} flexible`} />
            <Card className="p-5 md:p-6">
              <ul className="space-y-4">
                {data.summary.categories.slice(0, 8).map((c) => {
                  const max = data.summary.categories[0]?.amount ?? 1;
                  return (
                    <li key={c.categoryId}>
                      <Link to={`/transactions?category=${c.categoryId}${location.search ? `&${location.search.slice(1)}` : ''}`} className="group block rounded-lg">
                        <div className="flex items-baseline justify-between gap-3">
                          <span className="text-[0.9375rem] group-hover:text-accent">
                            {c.name}
                            <span className="caption"> · {c.groupName}</span>
                          </span>
                          <span className="tabular text-[0.9375rem] font-medium">{formatMoney(c.amount, currency, { whole: true })}</span>
                        </div>
                        <div className="mt-1.5 flex items-center gap-3">
                          <div className="h-2 flex-1 overflow-hidden rounded-full bg-fill">
                            <div className="h-full rounded-full" style={{ width: `${(c.amount / max) * 100}%`, background: groupStyle(c.groupId).color }} />
                          </div>
                          <span className="caption tabular w-10 text-right">{formatPercent(c.sharePercent, { digits: 0 })}</span>
                        </div>
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </Card>
          </section>

          <section aria-labelledby="trends-title">
            <SectionHeader id="trends-title" title="Spending trends" />
            <div className="grid gap-6 lg:grid-cols-2">
              <Card className="p-5 md:p-6">
                <h3 className="mb-4 text-[0.9375rem] font-semibold">Savings rate, last 12 months</h3>
                {trend.data ? <SavingsRateChart monthly={trend.data.summary.monthly} /> : <Skeleton className="h-56 w-full" />}
              </Card>
              <Card className="p-5 md:p-6">
                <h3 className="mb-4 text-[0.9375rem] font-semibold">Biggest changes vs previous period</h3>
                {!data.summary.previous.hasData ? (
                  <p className="text-[0.9375rem] text-label-secondary">There’s no data for the previous period to compare with.</p>
                ) : changes.length === 0 ? (
                  <p className="text-[0.9375rem] text-label-secondary">Spending by category was about the same as the previous period.</p>
                ) : (
                  <ul className="space-y-1">
                    {changes.map((c) => {
                      const up = c.amount > c.previousAmount;
                      const Icon = up ? ArrowUpRight : ArrowDownRight;
                      return (
                        <li key={c.categoryId} className="flex items-center gap-3 py-2">
                          <CategoryGlyph groupId={c.groupId} size={32} />
                          <div className="min-w-0 flex-1">
                            <p className="truncate text-[0.9375rem]">{c.name}</p>
                            <p className="caption tabular">
                              {formatMoney(c.previousAmount, currency, { whole: true })} → {formatMoney(c.amount, currency, { whole: true })}
                            </p>
                          </div>
                          <span className={cn('tabular inline-flex items-center gap-0.5 text-[0.875rem] font-medium', up ? 'text-attention' : 'text-positive')}>
                            <Icon size={15} strokeWidth={2.5} aria-hidden="true" />
                            {formatMoney(Math.abs(c.amount - c.previousAmount), currency, { whole: true })}
                            <span className="sr-only">{up ? ' more' : ' less'}</span>
                          </span>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </Card>
            </div>
          </section>

          <div className="grid gap-6 lg:grid-cols-2">
            <section aria-labelledby="recurring-title">
              <SectionHeader
                id="recurring-title"
                title="Recurring expenses"
                action={
                  <Link to={`/recurring${location.search}`} className="text-[0.9375rem] text-accent">
                    See all
                  </Link>
                }
              />
              <Card className="p-5 md:p-6">
                {data.recurring.filter((r) => !r.isIncome).length === 0 ? (
                  <p className="text-[0.9375rem] text-label-secondary">No recurring payments detected yet.</p>
                ) : (
                  <ul className="space-y-1">
                    {data.recurring
                      .filter((r) => !r.isIncome)
                      .slice(0, 6)
                      .map((r) => {
                        const note = ai?.recurringExpenses.find((x) => x.merchant === r.merchant)?.note;
                        return (
                          <li key={r.merchantKey} className="flex items-start gap-3 py-2">
                            <CategoryGlyph groupId={groupIdOf(r.categoryId)} size={32} />
                            <div className="min-w-0 flex-1">
                              <p className="truncate text-[0.9375rem]">{r.merchant}</p>
                              <p className="caption">{note ?? `${r.frequency}${r.amountVaries ? ' · amount varies' : ''}`}</p>
                            </div>
                            <p className="tabular text-[0.9375rem] font-medium">{formatMoney(r.amount, currency)}</p>
                          </li>
                        );
                      })}
                  </ul>
                )}
              </Card>
            </section>

            <section aria-labelledby="unusual-title">
              <SectionHeader id="unusual-title" title="Unusual spending" />
              <Card className="p-5 md:p-6">
                {ai && ai.anomalies.length > 0 ? (
                  <ul className="space-y-4">
                    {ai.anomalies.map((a) => (
                      <li key={a.ref}>
                        <div className="flex items-baseline justify-between gap-3">
                          <p className="text-[0.9375rem] font-medium">{a.description}</p>
                          <p className="tabular text-[0.9375rem] font-medium">{formatMoney(a.amount, currency)}</p>
                        </div>
                        <p className="caption">
                          {a.merchant} · {formatShortDate(a.date, prefs.dateFormat)}
                        </p>
                        <p className="mt-1 text-[0.9375rem] text-label-secondary">{a.explanation}</p>
                      </li>
                    ))}
                  </ul>
                ) : data.anomalies.length > 0 ? (
                  <ul className="space-y-1">
                    {data.anomalies.map((a) => (
                      <li key={a.transactionId} className="flex items-center gap-3 py-2">
                        <CategoryGlyph groupId={groupIdOf(a.categoryId)} size={32} />
                        <div className="min-w-0 flex-1">
                          <p className="truncate text-[0.9375rem]">{a.merchant}</p>
                          <p className="caption">
                            {formatShortDate(a.date, prefs.dateFormat)} ·{' '}
                            {a.kind === 'possibleDuplicate' ? 'possible duplicate' : a.kind === 'newMerchant' ? 'new merchant' : `usually ${formatMoney(a.typicalAmount ?? 0, currency, { whole: true })}`}
                          </p>
                        </div>
                        <p className="tabular text-[0.9375rem] font-medium">{formatMoney(a.amount, currency)}</p>
                      </li>
                    ))}
                  </ul>
                ) : (
                  <p className="text-[0.9375rem] text-label-secondary">Nothing unusual stood out in this period.</p>
                )}
              </Card>
            </section>
          </div>

          {ai && (
            <>
              <section aria-labelledby="opportunities-title">
                <SectionHeader id="opportunities-title" title="Savings opportunities" subtitle="Estimates based on your own spending. Targets are suggestions, not rules." />
                <SavingsOpportunities items={ai.savingsOpportunities} currency={currency} />
              </section>

              <section aria-labelledby="recommendations-title">
                <SectionHeader id="recommendations-title" title="Recommendations" />
                <ol className="space-y-3">
                  {ai.recommendations.map((r, index) => (
                    <li key={r.title} className="card flex gap-4 p-5">
                      <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-accent-soft text-[0.875rem] font-semibold text-accent" aria-hidden="true">
                        {index + 1}
                      </span>
                      <div className="min-w-0">
                        <h3 className="text-[0.9375rem] font-semibold">{r.title}</h3>
                        <p className="mt-1 text-[0.9375rem] text-label-secondary">{r.description}</p>
                        {r.potentialImpact && (
                          <p className="mt-2 inline-flex items-center gap-1.5 text-[0.8125rem] text-positive">
                            <Lightbulb size={14} aria-hidden="true" />
                            {r.potentialImpact}
                          </p>
                        )}
                      </div>
                    </li>
                  ))}
                </ol>
              </section>

              {ai.caveats.length > 0 && (
                <section aria-labelledby="caveats-title" className="rounded-2xl bg-surface-sunken px-5 py-4">
                  <h2 id="caveats-title" className="eyebrow">
                    Keep in mind
                  </h2>
                  <ul className="mt-2 list-disc space-y-1 pl-5 text-[0.875rem] text-label-secondary">
                    {ai.caveats.map((c) => (
                      <li key={c}>{c}</li>
                    ))}
                  </ul>
                </section>
              )}
            </>
          )}

          <p className="caption text-center">
            Insights are generated from your imported statements and are for information only, not financial advice.
            {analysis.data?.model && ` Written by ${analysis.data.model}; all figures calculated by FinSight.`}
          </p>
        </div>
      )}
    </div>
  );
}
