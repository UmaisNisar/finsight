import { AnimatePresence, motion } from 'motion/react';
import { ChevronDown, Info, RefreshCw, ShieldCheck, Sparkles } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { errorMessage } from '@/api/client';
import { useAnalysis, useGenerateAnalysis } from '@/api/queries';
import type { Analysis, AnalysisResponse } from '@/api/schemas';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/primitives';
import { cn } from '@/lib/cn';
import { CategoryGlyph, groupIdOf } from '@/lib/categories';
import { formatMoney, formatRelativeTime } from '@/lib/format';
import type { PeriodSelection } from '@/lib/period';

export function useAnalysisState(period: PeriodSelection) {
  const analysis = useAnalysis(period);
  const generate = useGenerateAnalysis(period);
  return { analysis, generate };
}

function AiMark({ className }: { className?: string }) {
  return (
    <span
      aria-hidden="true"
      className={cn('flex size-8 items-center justify-center rounded-full text-white', className)}
      style={{ background: 'linear-gradient(135deg, #34c3a0, #0a84ff 55%, #8e6cf0)' }}
    >
      <Sparkles size={16} />
    </span>
  );
}

function GeneratingState() {
  return (
    <div aria-live="polite" aria-busy="true">
      <p className="flex items-center gap-2 text-[0.9375rem] text-label-secondary">
        <span className="size-4 animate-spin rounded-full border-2 border-accent/25 border-t-accent" aria-hidden="true" />
        Analyzing your spending. This takes about 20 seconds.
      </p>
      <div className="mt-4 space-y-2.5">
        <Skeleton className="h-4 w-full" />
        <Skeleton className="h-4 w-[92%]" />
        <Skeleton className="h-4 w-[70%]" />
      </div>
    </div>
  );
}

/** The AI summary for a period, with every state: unavailable, not yet generated, generating, stale, failed. */
export function AiInsightCard({ period, hasData, compact = false, linkSuffix = '' }: { period: PeriodSelection; hasData: boolean; compact?: boolean; linkSuffix?: string }) {
  const { analysis, generate } = useAnalysisState(period);
  const data = analysis.data;

  if (analysis.isPending) {
    return (
      <section className="card p-6" aria-label="AI insight">
        <Skeleton className="h-5 w-40" />
        <Skeleton className="mt-4 h-4 w-full" />
        <Skeleton className="mt-2 h-4 w-3/4" />
      </section>
    );
  }

  if (!data) return null;

  const unavailable = !data.availability.configured
    ? 'AI analysis isn’t set up on this server. Every number here is still calculated directly from your statements.'
    : !data.availability.enabled
      ? 'AI insights are turned off.'
      : null;

  return (
    <section aria-labelledby="ai-insight-title" className="card relative overflow-hidden p-6 md:p-7">
      <div
        aria-hidden="true"
        className="pointer-events-none absolute -top-24 -right-24 size-64 rounded-full opacity-[0.08] blur-3xl"
        style={{ background: 'radial-gradient(circle, #0a84ff, #8e6cf0)' }}
      />
      <div className="relative">
        <div className="mb-3 flex items-center gap-3">
          <AiMark />
          <h2 id="ai-insight-title" className="text-[1.0625rem] font-semibold tracking-[-0.01em]">
            {compact ? 'Insight' : 'Financial summary'}
          </h2>
          {data.generatedAt && !generate.isPending && (
            <span className="caption ml-auto hidden sm:inline">Generated {formatRelativeTime(data.generatedAt)}</span>
          )}
        </div>

        {generate.isPending ? (
          <GeneratingState />
        ) : unavailable ? (
          <p className="text-[0.9375rem] text-label-secondary">
            {unavailable}{' '}
            {!data.availability.enabled && data.availability.configured && (
              <Link to="/settings" className="text-accent">
                Turn on in Settings
              </Link>
            )}
          </p>
        ) : data.analysis ? (
          <>
            <p className={cn('text-pretty text-label', compact ? 'text-[1.0625rem] leading-relaxed' : 'text-[1.125rem] leading-relaxed')}>{data.analysis.summary}</p>
            {data.state === 'stale' && (
              <p className="mt-3 flex items-center gap-2 text-[0.875rem] text-attention">
                <Info size={15} aria-hidden="true" />
                Your transactions changed since this was written.
              </p>
            )}
            <div className="mt-5 flex flex-wrap items-center gap-2">
              {compact && (
                <Link to={`/insights${linkSuffix}`} className="inline-flex h-8 items-center rounded-full bg-accent-soft px-3.5 text-[0.8125rem] font-medium text-accent">
                  See full analysis
                </Link>
              )}
              <Button variant={data.state === 'stale' ? 'secondary' : 'plain'} size="sm" icon={<RefreshCw size={14} aria-hidden="true" />} onClick={() => generate.mutate()}>
                Regenerate
              </Button>
            </div>
          </>
        ) : hasData ? (
          <div>
            <p className="text-[0.9375rem] text-label-secondary">
              Get a plain-language explanation of this period: what changed, what stands out, and where you could save.
            </p>
            <Button className="mt-4" icon={<Sparkles size={16} aria-hidden="true" />} onClick={() => generate.mutate()}>
              Analyze spending
            </Button>
          </div>
        ) : (
          <p className="text-[0.9375rem] text-label-secondary">There are no transactions in this period to analyze.</p>
        )}

        {generate.isError && (
          <p role="alert" className="mt-4 text-[0.9375rem] text-critical">
            {errorMessage(generate.error)}
          </p>
        )}
      </div>
    </section>
  );
}

export function SavingsOpportunities({ items, currency, limit }: { items: Analysis['savingsOpportunities']; currency: string; limit?: number }) {
  const shown = limit ? items.slice(0, limit) : items;
  if (shown.length === 0) {
    return <p className="text-[0.9375rem] text-label-secondary">No clear savings opportunities in this period. Your discretionary spending looks steady.</p>;
  }

  return (
    <ul className="grid gap-3 md:grid-cols-2">
      {shown.map((item) => (
        <li key={item.categoryId} className="rounded-2xl bg-surface-sunken p-4 md:p-5">
          <div className="flex items-start gap-3">
            <CategoryGlyph groupId={groupIdOf(item.categoryId)} size={34} />
            <div className="min-w-0 flex-1">
              <div className="flex items-baseline justify-between gap-3">
                <h3 className="truncate text-[0.9375rem] font-semibold">{item.category}</h3>
                {item.estimatedMonthlySavings !== null && (
                  <p className="shrink-0 text-[0.9375rem] font-semibold text-positive">
                    {formatMoney(item.estimatedMonthlySavings, currency, { whole: true })}
                    <span className="text-[0.8125rem] font-normal">/mo</span>
                  </p>
                )}
              </div>
              <p className="caption tabular mt-0.5">
                {formatMoney(item.currentMonthlySpending, currency, { whole: true })} a month now
                {item.suggestedMonthlyTarget !== null && <> · target {formatMoney(item.suggestedMonthlyTarget, currency, { whole: true })}</>}
              </p>
              <p className="mt-2 text-[0.9375rem] text-label-secondary">{item.explanation}</p>
            </div>
          </div>
        </li>
      ))}
    </ul>
  );
}

/** Shows what the validator changed. Corrections are never hidden. */
export function AnalysisCorrections({ response }: { response: AnalysisResponse }) {
  const [open, setOpen] = useState(false);
  if (response.corrections.length === 0) {
    return (
      <p className="caption flex items-center gap-1.5">
        <ShieldCheck size={14} aria-hidden="true" />
        Every figure was checked against your transactions.
      </p>
    );
  }

  return (
    <div>
      <button type="button" aria-expanded={open} onClick={() => setOpen((o) => !o)} className="caption flex items-center gap-1.5 hover:text-label">
        <ShieldCheck size={14} aria-hidden="true" />
        FinSight checked this analysis and adjusted {response.corrections.length} {response.corrections.length === 1 ? 'item' : 'items'}
        <ChevronDown size={14} className={cn('transition-transform', open && 'rotate-180')} aria-hidden="true" />
      </button>
      <AnimatePresence initial={false}>
        {open && (
          <motion.ul
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: 'auto', opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            className="caption mt-2 list-disc space-y-1 overflow-hidden pl-8"
          >
            {response.corrections.map((c, i) => (
              <li key={i}>{c.message}</li>
            ))}
          </motion.ul>
        )}
      </AnimatePresence>
    </div>
  );
}

