import { ArrowDownRight, ArrowUpRight, CircleAlert, Info, Lightbulb, Sparkles, ThumbsUp } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link } from 'react-router';
import { errorMessage } from '@/api/client';
import { useAnalysis, useSummary } from '@/api/queries';
import type { Analysis, AnalysisResponse, SummaryResponse } from '@/api/schemas';
import { PageHeader } from '@/components/PageHeader';
import { PeriodPicker } from '@/components/PeriodPicker';
import { WidgetBoundary } from '@/components/errors/WidgetBoundary';
import { AnimatedNumber } from '@/components/ui/AnimatedNumber';
import { buttonStyles } from '@/components/ui/Button';
import { Card, EmptyState, ErrorState, RowSkeleton, SectionHeader, Skeleton } from '@/components/ui/primitives';
import { usePeriod, usePeriodLink } from '@/hooks/usePeriod';
import { usePreferences } from '@/hooks/usePreferences';
import { cn } from '@/lib/cn';
import { CategoryGlyph, groupIdOf, groupStyle } from '@/lib/categories';
import { formatMoney, formatPercent, formatShortDate } from '@/lib/format';
import { anomalyReason, FREQUENCY_LABEL } from '@/lib/labels';
import { trailingMonths, type PeriodSelection } from '@/lib/period';
import { AiInsightCard, AnalysisCorrections, SavingsOpportunities } from './AiComponents';
import { SavingsRateChart } from './SavingsRateChart';

const wholePercent = (value: number) => formatPercent(value, { digits: 0 });

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

function Rows({ count, glyph }: { count: number; glyph?: number }) {
  return (
    <div className="space-y-1" aria-hidden="true">
      {Array.from({ length: count }, (_, i) => (glyph ? <RowSkeleton key={i} glyph={glyph} className="py-2" /> : <BarRowSkeleton key={i} />))}
    </div>
  );
}

function BarRowSkeleton() {
  return (
    <div className="pb-[17px]">
      <div className="flex items-center justify-between py-[3px]">
        <Skeleton className="h-3.5 w-36" />
        <Skeleton className="h-3.5 w-14" />
      </div>
      <Skeleton className="mt-2 h-2 w-full rounded-full" />
    </div>
  );
}

/** A card whose body is placeholder rows until `children` is provided. */
function LoadingCard({ busy, skeleton, children, className }: { busy: boolean; skeleton: ReactNode; children?: ReactNode; className?: string }) {
  return (
    <Card className={cn('p-5 md:p-6', className)} aria-busy={busy}>
      {busy ? skeleton : <div className="fade-in">{children}</div>}
    </Card>
  );
}

function Body({ data, analysis, period, currency, dateFormat }: { data?: SummaryResponse; analysis?: AnalysisResponse; period: PeriodSelection; currency: string; dateFormat: string }) {
  const periodLink = usePeriodLink();
  const trend = useSummary(data ? trailingMonths(data.period.end, 12) : period, data !== undefined);
  const ai = analysis?.analysis ?? null;
  const loading = !data;

  const changes = (data?.summary.categories ?? [])
    .filter((c) => c.changePercent !== null && Math.abs(c.amount - c.previousAmount) >= 25)
    .sort((a, b) => Math.abs(b.amount - b.previousAmount) - Math.abs(a.amount - a.previousAmount))
    .slice(0, 5);
  const recurring = (data?.recurring ?? []).filter((r) => !r.isIncome).slice(0, 6);
  const maxCategory = data?.summary.categories[0]?.amount || 1;
  const whole = (amount: number) => formatMoney(amount, currency, { whole: true });

  return (
    <div className="space-y-10">
      <section aria-label="Financial summary" className="space-y-4">
        <AiInsightCard period={period} hasData />
        {analysis?.analysis && <AnalysisCorrections response={analysis} />}
      </section>

      {ai && ai.keyInsights.length > 0 && (
        <section aria-labelledby="key-title">
          <SectionHeader id="key-title" title="Key insights" />
          <WidgetBoundary name="key-insights" message="Key insights couldn’t be shown." queryKeys={[['analysis']]} resetKeys={[ai]} shell={(fallback) => <Card>{fallback}</Card>}>
            <KeyInsights items={ai.keyInsights} />
          </WidgetBoundary>
        </section>
      )}

      <section aria-labelledby="where-title">
        <SectionHeader
          id="where-title"
          title="Where your money goes"
          subtitle={
            data ? (
              <>
                <AnimatedNumber value={data.summary.expenses} format={whole} /> spent · <AnimatedNumber value={data.summary.fixedExpenses} format={whole} /> fixed,{' '}
                <AnimatedNumber value={data.summary.variableExpenses} format={whole} /> flexible
              </>
            ) : (
              <Skeleton className="my-[0.2em] h-[1em] w-64" />
            )
          }
        />
        <LoadingCard busy={loading} skeleton={<Rows count={8} />}>
          <ul className="space-y-4">
            {data?.summary.categories.slice(0, 8).map((c) => (
              <li key={c.categoryId}>
                <Link to={periodLink(`/transactions?category=${c.categoryId}`)} className="group block rounded-lg">
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-[0.9375rem] group-hover:text-accent">
                      {c.name}
                      <span className="caption"> · {c.groupName}</span>
                    </span>
                    <AnimatedNumber className="text-[0.9375rem] font-medium" value={c.amount} format={whole} />
                  </div>
                  <div className="mt-1.5 flex items-center gap-3">
                    <div className="h-2 flex-1 overflow-hidden rounded-full bg-fill">
                      <div
                        className="h-full rounded-full transition-[width] duration-500 ease-[cubic-bezier(0.2,0.8,0.2,1)]"
                        style={{ width: `${(c.amount / maxCategory) * 100}%`, background: groupStyle(c.groupId).color }}
                      />
                    </div>
                    <span className="caption w-10 text-right">
                      <AnimatedNumber value={c.sharePercent} format={wholePercent} />
                    </span>
                  </div>
                </Link>
              </li>
            ))}
          </ul>
        </LoadingCard>
      </section>

      <section aria-labelledby="trends-title">
        <SectionHeader id="trends-title" title="Spending trends" />
        <div className="grid gap-6 lg:grid-cols-2">
          <Card className="p-5 md:p-6" aria-busy={!trend.data && !trend.isError}>
            <h3 className="mb-4 text-[0.9375rem] font-semibold">Savings rate, last 12 months</h3>
            {data && trend.isError && !trend.data ? (
              <ErrorState className="min-h-[246px] justify-center py-0" message={errorMessage(trend.error)} onRetry={() => void trend.refetch()} />
            ) : (
              <SavingsRateChart monthly={data ? trend.data?.summary.monthly : undefined} />
            )}
          </Card>
          <Card className="p-5 md:p-6" aria-busy={loading}>
            <h3 className="mb-4 text-[0.9375rem] font-semibold">Biggest changes vs previous period</h3>
            {!data ? (
              <Rows count={4} glyph={32} />
            ) : !data.summary.previous.hasData ? (
              <p className="fade-in text-[0.9375rem] text-label-secondary">There’s no data for the previous period to compare with.</p>
            ) : changes.length === 0 ? (
              <p className="fade-in text-[0.9375rem] text-label-secondary">Spending by category was about the same as the previous period.</p>
            ) : (
              <ul className="fade-in space-y-1">
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
              <Link to={periodLink('/recurring')} className="text-[0.9375rem] text-accent">
                See all
              </Link>
            }
          />
          <LoadingCard busy={loading} skeleton={<Rows count={4} glyph={32} />}>
            {recurring.length === 0 ? (
              <p className="text-[0.9375rem] text-label-secondary">No recurring payments detected yet.</p>
            ) : (
              <ul className="space-y-1">
                {recurring.map((r) => {
                  const note = ai?.recurringExpenses.find((x) => x.merchant === r.merchant)?.note;
                  return (
                    <li key={r.merchantKey} className="flex items-start gap-3 py-2">
                      <CategoryGlyph groupId={groupIdOf(r.categoryId)} size={32} />
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-[0.9375rem]">{r.merchant}</p>
                        <p className="caption">{note ?? `${FREQUENCY_LABEL[r.frequency]}${r.amountVaries ? ' · amount varies' : ''}`}</p>
                      </div>
                      <p className="tabular text-[0.9375rem] font-medium">{formatMoney(r.amount, currency)}</p>
                    </li>
                  );
                })}
              </ul>
            )}
          </LoadingCard>
        </section>

        <section aria-labelledby="unusual-title">
          <SectionHeader id="unusual-title" title="Unusual spending" />
          <LoadingCard busy={loading} skeleton={<Rows count={4} glyph={32} />}>
            {ai && ai.anomalies.length > 0 ? (
              <ul className="space-y-4">
                {ai.anomalies.map((a) => (
                  <li key={a.ref}>
                    <div className="flex items-baseline justify-between gap-3">
                      <p className="text-[0.9375rem] font-medium">{a.description}</p>
                      <p className="tabular text-[0.9375rem] font-medium">{formatMoney(a.amount, currency)}</p>
                    </div>
                    <p className="caption">
                      {a.merchant} · {formatShortDate(a.date, dateFormat)}
                    </p>
                    <p className="mt-1 text-[0.9375rem] text-label-secondary">{a.explanation}</p>
                  </li>
                ))}
              </ul>
            ) : data && data.anomalies.length > 0 ? (
              <ul className="space-y-1">
                {data.anomalies.map((a) => (
                  <li key={a.transactionId} className="flex items-center gap-3 py-2">
                    <CategoryGlyph groupId={groupIdOf(a.categoryId)} size={32} />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[0.9375rem]">{a.merchant}</p>
                      <p className="caption truncate">
                        {formatShortDate(a.date, dateFormat)} · {anomalyReason(a, currency)}
                      </p>
                    </div>
                    <p className="tabular text-[0.9375rem] font-medium">{formatMoney(a.amount, currency)}</p>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-[0.9375rem] text-label-secondary">Nothing unusual stood out in this period.</p>
            )}
          </LoadingCard>
        </section>
      </div>

      {ai && (
        <WidgetBoundary name="ai-details" message="Savings opportunities and recommendations couldn’t be shown." queryKeys={[['analysis']]} resetKeys={[ai]} shell={(fallback) => <Card>{fallback}</Card>}>
        <div className="space-y-10">
          <section aria-labelledby="opportunities-title">
            <SectionHeader id="opportunities-title" title="Savings opportunities" subtitle="Estimates based on your own spending. Targets are suggestions, not rules." />
            <SavingsOpportunities items={ai.savingsOpportunities} currency={currency} />
          </section>

          {ai.recommendations.length > 0 && (
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
          )}

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
        </div>
        </WidgetBoundary>
      )}

      <p className="caption text-center">
        Insights are generated from your imported statements and are for information only, not financial advice.
        {analysis?.model && ` Written by ${analysis.model}; all figures calculated by FinSight.`}
      </p>
    </div>
  );
}

export default function InsightsPage() {
  const { period } = usePeriod();
  const prefs = usePreferences();
  const summary = useSummary(period);
  const analysis = useAnalysis(period);

  // An empty period is known from the summary alone; only a period with data waits for the AI state too, so every card fills in together.
  const summaryData = summary.isPending ? undefined : summary.data;
  const state = summary.isPending ? 'loading' : !summaryData ? 'error' : summaryData.summary.transactionCount === 0 ? 'empty' : analysis.isPending ? 'loading' : 'ready';
  const loading = state === 'loading';
  const data = loading ? undefined : summaryData;
  const currency = data?.currency ?? prefs.currency;

  return (
    <div>
      <PageHeader
        title="Insights"
        subtitle={data ? `What stands out in ${data.period.label}` : loading ? 'loading' : undefined}
        actions={<PeriodPicker resolvedLabel={summary.data?.period.label} />}
      />

      {state === 'error' && (
        <Card>
          <ErrorState message={errorMessage(summary.error)} onRetry={() => void summary.refetch()} />
        </Card>
      )}

      {state === 'empty' && data && (
        <div data-pending={summary.isPlaceholderData}>
        <Card>
          <EmptyState
            icon={<Sparkles size={24} aria-hidden="true" />}
            title={`Nothing to analyze for ${data.period.label}`}
            description="Import statements that cover this period, or choose a different time range."
            action={
              <Link to="/statements" className={buttonStyles({ variant: 'secondary' })}>
                View statements
              </Link>
            }
          />
        </Card>
        </div>
      )}

      {(state === 'loading' || state === 'ready') && (
        <div data-pending={summary.isPlaceholderData} aria-busy={loading || summary.isPlaceholderData}>
          <Body data={data} analysis={analysis.data} period={period} currency={currency} dateFormat={prefs.dateFormat} />
        </div>
      )}
    </div>
  );
}
