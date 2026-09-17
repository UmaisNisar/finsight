import { ArrowDownRight, ArrowUpRight, CalendarSearch, FileText, FileUp, Mail, TriangleAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link } from 'react-router';
import { errorMessage } from '@/api/client';
import { useAnalysis, useGmail, useSession, useSummary } from '@/api/queries';
import type { AnalysisResponse, SummaryResponse } from '@/api/schemas';
import { PageHeader } from '@/components/PageHeader';
import { PeriodPicker } from '@/components/PeriodPicker';
import { buttonStyles } from '@/components/ui/Button';
import { WidgetBoundary } from '@/components/errors/WidgetBoundary';
import { Card, EmptyState, ErrorState, Pill, RowSkeleton, SectionHeader, Skeleton } from '@/components/ui/primitives';
import { AiInsightCard, SavingsOpportunities } from '@/features/insights/AiComponents';
import { usePeriod, usePeriodLink } from '@/hooks/usePeriod';
import { usePreferences } from '@/hooks/usePreferences';
import { cn } from '@/lib/cn';
import { CategoryGlyph, groupIdOf } from '@/lib/categories';
import { formatMoney, formatMonth, formatPercent, formatShortDate, greeting } from '@/lib/format';
import { gmailConnectUrl } from '@/lib/gmail';
import { anomalyReason } from '@/lib/labels';
import { isSingleMonth, trailingMonths, type PeriodSelection } from '@/lib/period';
import { CashFlowChart } from './CashFlowChart';
import { SpendingBreakdown } from './SpendingBreakdown';

/** The comparison label, shortened on phones so the line never wraps and resizes the card. */
function Compare({ long, short }: { long: string; short: string }) {
  return (
    <>
      <span className="sm:hidden">{short}</span>
      <span className="hidden sm:inline">{long}</span>
    </>
  );
}

function Delta({ value, goodWhenUp, compare }: { value: number | null; goodWhenUp: boolean; compare: { long: string; short: string } }) {
  if (value === null || Math.abs(value) < 0.5) {
    return (
      <span className="caption truncate">
        {value === null ? 'No data for ' : 'Same as '}
        <Compare {...compare} />
      </span>
    );
  }
  const up = value > 0;
  const good = up === goodWhenUp;
  const Icon = up ? ArrowUpRight : ArrowDownRight;
  return (
    <span className={cn('inline-flex min-w-0 items-center gap-0.5 text-[0.8125rem] font-medium whitespace-nowrap', good ? 'text-positive' : 'text-label-secondary')}>
      <Icon size={14} strokeWidth={2.5} className="shrink-0" aria-hidden="true" />
      {formatPercent(Math.abs(value), { digits: 0 })}
      <span className="truncate font-normal text-label-secondary">
        &nbsp;vs <Compare {...compare} />
      </span>
    </span>
  );
}

/** One headline figure. With no value it shows placeholders at the exact size of the real text. */
function Figure({ label, value, children, emphasis }: { label: string; value?: string; children?: ReactNode; emphasis?: boolean }) {
  return (
    <div className="min-w-0">
      <dt className="eyebrow">{label}</dt>
      <dd className="m-0">
        <p className={cn('mt-1.5 truncate', emphasis ? 'figure-hero' : 'figure text-[1.75rem] md:text-[2.125rem]')}>
          {value ?? <Skeleton className="h-[1em] w-[4.5ch] rounded-xl" />}
        </p>
        <div className="mt-1.5 flex min-h-5 items-center">{value === undefined ? <Skeleton className="h-3 w-24" /> : children}</div>
      </dd>
    </div>
  );
}

function compareLabels(data: SummaryResponse) {
  const { start, end } = data.summary.previous.range;
  return isSingleMonth(start, end) ? { long: formatMonth(start, 'long'), short: formatMonth(start) } : { long: 'previous period', short: 'previous' };
}

function HeroSummary({ data }: { data?: SummaryResponse }) {
  const summary = data?.summary;
  const currency = data?.currency ?? '';
  const compare = data ? compareLabels(data) : { long: '', short: '' };
  const net = summary?.netCashFlow ?? 0;
  const footnotes: ReactNode[] = [];
  if (data && summary) {
    if (summary.coverage.isPartial) {
      footnotes.push(
        <span key="partial" className="flex items-start gap-1.5 text-attention">
          <TriangleAlert size={14} className="mt-0.5 shrink-0" aria-hidden="true" />
          Only {summary.coverage.monthsWithData} of {summary.coverage.monthsInRange} months in this period have statements. Totals include imported months only.
        </span>,
      );
    }
    if (summary.transfers.count > 0) {
      footnotes.push(
        <span key="transfers">
          {formatMoney(summary.transfers.total, currency, { whole: true })} moved between your own accounts, including card payments, isn’t counted as spending.
        </span>,
      );
    }
  }

  return (
    <Card className="p-6 md:p-8" aria-labelledby="summary-title" aria-busy={!data}>
      <h2 id="summary-title" className="sr-only">
        Summary
      </h2>
      <dl className="grid grid-cols-2 gap-x-6 gap-y-7 md:grid-cols-4">
        <Figure label="Income" value={summary && formatMoney(summary.income, currency, { whole: true })}>
          {summary && <Delta value={summary.previous.hasData ? summary.previous.incomeChangePercent : null} goodWhenUp compare={compare} />}
        </Figure>
        <Figure label="Spent" value={summary && formatMoney(summary.expenses, currency, { whole: true })}>
          {summary && <Delta value={summary.previous.hasData ? summary.previous.expenseChangePercent : null} goodWhenUp={false} compare={compare} />}
        </Figure>
        <Figure label={net >= 0 ? 'Saved' : 'Overspent'} value={summary && formatMoney(Math.abs(net), currency, { whole: true })}>
          {summary && summary.refunds > 0 && <span className="caption truncate">After {formatMoney(summary.refunds, currency, { whole: true })} in refunds</span>}
        </Figure>
        <Figure label="Savings rate" value={summary && (summary.savingsRate === null ? '—' : formatPercent(summary.savingsRate))}>
          {summary?.savingsRate === null ? (
            <span className="caption">No income recorded</span>
          ) : (
            <div className="h-1.5 w-full max-w-36 overflow-hidden rounded-full bg-fill" role="presentation">
              <div
                className="h-full rounded-full bg-positive transition-[width] duration-500 ease-[cubic-bezier(0.2,0.8,0.2,1)]"
                style={{ width: `${Math.max(0, Math.min(100, summary?.savingsRate ?? 0))}%` }}
              />
            </div>
          )}
        </Figure>
      </dl>
      {!data ? (
        <div className="caption mt-6 border-t border-separator pt-4" aria-hidden="true">
          {/* Caption-sized line boxes: the transfers note is one line on wide screens and three on phones. */}
          <span className="flex h-[1.45em] items-center">
            <Skeleton className="h-3 w-full sm:w-3/4" />
          </span>
          <span className="flex h-[1.45em] items-center sm:hidden">
            <Skeleton className="h-3 w-11/12" />
          </span>
          <span className="flex h-[1.45em] items-center sm:hidden">
            <Skeleton className="h-3 w-1/3" />
          </span>
        </div>
      ) : (
        footnotes.length > 0 && <div className="caption mt-6 space-y-1.5 border-t border-separator pt-4">{footnotes}</div>
      )}
    </Card>
  );
}

/** For accounts with nothing imported yet, including people who chose "Set up later" during onboarding. */
function Onboarding({ canUseGmail }: { canUseGmail: boolean }) {
  const gmail = useGmail(canUseGmail);
  const connected = gmail.data?.connected === true && gmail.data.status !== 'expired';
  const offerConnect = canUseGmail && !connected;
  return (
    <Card className="px-6 py-4">
      <EmptyState
        icon={offerConnect ? <Mail size={26} aria-hidden="true" /> : <FileText size={26} aria-hidden="true" />}
        title={offerConnect ? 'Connect your Gmail' : 'Add your first statement'}
        description={
          offerConnect
            ? 'FinSight can automatically find your bank statements and analyze your spending. You choose which statements to import.'
            : connected
              ? 'Scan Gmail again or upload a PDF from Statements. You choose which statements to import.'
              : 'Upload a PDF bank or credit card statement and FinSight will extract and categorize every transaction.'
        }
        action={
          <>
            {offerConnect && (
              <a href={gmailConnectUrl('/statements')} className={buttonStyles()}>
                <Mail size={16} aria-hidden="true" />
                Connect Gmail
              </a>
            )}
            {connected && (
              <Link to="/statements" className={buttonStyles()}>
                <FileText size={16} aria-hidden="true" />
                Go to Statements
              </Link>
            )}
            <Link to="/statements?upload=1" className={buttonStyles({ variant: canUseGmail ? 'secondary' : 'primary' })}>
              <FileUp size={16} aria-hidden="true" />
              Upload a PDF
            </Link>
          </>
        }
      />
    </Card>
  );
}

/** A list card of rows, with placeholder rows while loading. */
function ListCard({ id, title, loadingRows, children }: { id: string; title: string; loadingRows: number; children?: ReactNode }) {
  return (
    <Card className="p-6" aria-labelledby={id} aria-busy={children === undefined}>
      <SectionHeader id={id} title={title} />
      {children ?? (
        <div className="space-y-1" aria-hidden="true">
          {Array.from({ length: loadingRows }, (_, i) => (
            <RowSkeleton key={i} glyph={32} className="py-1.5" />
          ))}
        </div>
      )}
    </Card>
  );
}

/**
 * The dashboard's cards. They render the same shells whether or not data has arrived, so loading swaps
 * placeholders for content inside each card and nothing on the page moves.
 */
function Dashboard({ data, analysis, period, pending }: { data?: SummaryResponse; analysis?: AnalysisResponse; period: PeriodSelection; pending: boolean }) {
  const prefs = usePreferences();
  const periodLink = usePeriodLink();
  const trendPeriod = data ? trailingMonths(data.period.end) : null;
  const trend = useSummary(trendPeriod ?? period, trendPeriod !== null);
  const currency = data?.currency ?? prefs.currency;
  const savings = analysis?.analysis?.savingsOpportunities ?? [];

  return (
    <div className="space-y-6" data-pending={pending} aria-busy={!data || pending}>
      <HeroSummary data={data} />

      <AiInsightCard period={period} hasData compact insightsLink={periodLink('/insights')} />

      <div className="grid gap-6 lg:grid-cols-5">
        <Card className="p-6 lg:col-span-3" aria-labelledby="cashflow-title">
          <SectionHeader id="cashflow-title" title="Cash flow" subtitle="Income and spending, last 6 months" />
          {/* Without this, a failed trend request would leave the chart's placeholder bars up for good. */}
          {trend.isError && !trend.data ? (
            <ErrorState className="h-72 justify-center py-0" message={errorMessage(trend.error)} onRetry={() => void trend.refetch()} />
          ) : (
            <CashFlowChart monthly={trend.data?.summary.monthly} currency={currency} />
          )}
        </Card>

        <Card className="p-6 lg:col-span-2" aria-labelledby="breakdown-title" aria-busy={!data}>
          <SectionHeader
            id="breakdown-title"
            title="Where your money went"
            subtitle={data ? `${formatMoney(data.summary.expenses, currency, { whole: true })} across ${data.summary.groups.length} categories` : <Skeleton className="my-[0.2em] h-[1em] w-40" />}
          />
          <SpendingBreakdown
            groups={data?.summary.groups}
            currency={currency}
            compareLabel={data && compareLabels(data).short}
            linkFor={(groupId) => periodLink(`/transactions?group=${groupId}`)}
          />
        </Card>
      </div>

      <div className="grid gap-6 md:grid-cols-2">
        <ListCard id="largest-title" title="Largest expenses" loadingRows={5}>
          {data && (
            <ul className="fade-in space-y-1">
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
          )}
        </ListCard>

        <ListCard id="attention-title" title="Worth a look" loadingRows={3}>
          {data &&
            (data.anomalies.length === 0 ? (
              <p className="fade-in text-[0.9375rem] text-label-secondary">Nothing unusual this period.</p>
            ) : (
              <ul className="fade-in space-y-1">
                {data.anomalies.slice(0, 4).map((a) => (
                  <li key={a.transactionId} className="flex items-center gap-3 py-1.5">
                    <CategoryGlyph groupId={groupIdOf(a.categoryId)} size={32} />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[0.9375rem]">{a.merchant}</p>
                      <p className="caption truncate">{anomalyReason(a, currency)}</p>
                    </div>
                    <Pill tone="attention">{formatMoney(a.amount, currency, { whole: true })}</Pill>
                  </li>
                ))}
              </ul>
            ))}
        </ListCard>
      </div>

      {/* Last on the page, so when an analysis adds this card nothing above it moves. */}
      {savings.length > 0 && (
        <Card className="p-6" aria-labelledby="savings-title">
          <SectionHeader id="savings-title" title="Savings opportunities" />
          <WidgetBoundary name="savings" message="Savings opportunities couldn’t be shown." queryKeys={[['analysis']]} resetKeys={[analysis]}>
            <SavingsOpportunities items={savings} currency={currency} limit={2} />
          </WidgetBoundary>
        </Card>
      )}
    </div>
  );
}

export default function OverviewPage() {
  const { period } = usePeriod();
  const session = useSession();
  const prefs = usePreferences();
  const summary = useSummary(period);
  const analysis = useAnalysis(period);

  // Wait for both the figures and the AI state on first load, so every card fills in together.
  // Onboarding and an empty period are known from the summary alone; only a period with data waits for the AI state too.
  const summaryData = summary.isPending ? undefined : summary.data;
  const state = summary.isPending
    ? 'loading'
    : !summaryData
      ? 'error'
      : !summaryData.hasAnyData
        ? 'onboarding'
        : summaryData.summary.transactionCount === 0
          ? 'empty'
          : analysis.isPending
            ? 'loading'
            : 'ready';
  const loading = state === 'loading';
  const data = loading ? undefined : summaryData;
  const name = session.data?.user?.name;

  return (
    <div>
      <PageHeader
        title={name ? `${greeting()}, ${name.split(' ')[0]}` : greeting()}
        // The exact dates of the selected preset, kept to one line so changing the period never reflows the page.
        subtitle={data ? data.period.label : loading ? 'loading' : undefined}
        actions={<PeriodPicker resolvedLabel={summary.data?.period.label} />}
      />

      {state === 'error' && (
        <Card>
          <ErrorState message={errorMessage(summary.error)} onRetry={() => void summary.refetch()} />
        </Card>
      )}

      {state === 'onboarding' && <Onboarding canUseGmail={session.data?.capabilities.gmail ?? false} />}

      {state === 'empty' && data && (
        <div data-pending={summary.isPlaceholderData}>
        <Card className="px-6">
          <EmptyState
            icon={<CalendarSearch size={26} aria-hidden="true" />}
            title={`No transactions for ${data.period.label}`}
            description={
              data.latestTransactionDate
                ? `Your most recent imported transaction is from ${formatShortDate(data.latestTransactionDate, prefs.dateFormat)}. Statements usually arrive a few days after a month ends, so try an earlier period.`
                : 'Import a statement that covers this period.'
            }
            action={
              <Link to="/statements" className={buttonStyles({ variant: 'secondary' })}>
                View statements
              </Link>
            }
          />
        </Card>
        </div>
      )}

      {(state === 'loading' || state === 'ready') && <Dashboard data={data} analysis={analysis.data} period={period} pending={summary.isPlaceholderData} />}
    </div>
  );
}
