import { ChevronDown, Info, RefreshCw, ShieldCheck, Sparkles } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { Link } from 'react-router';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys, useAnalysis, useGenerateAnalysis } from '@/api/queries';
import type { Analysis, AnalysisResponse } from '@/api/schemas';
import { AutoHeight, Collapse } from '@/components/ui/AutoHeight';
import { WidgetBoundary } from '@/components/errors/WidgetBoundary';
import { Button, buttonStyles } from '@/components/ui/Button';
import { ErrorState, Skeleton } from '@/components/ui/primitives';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { cn } from '@/lib/cn';
import { CategoryGlyph, groupIdOf } from '@/lib/categories';
import { formatMoney, formatRelativeTime } from '@/lib/format';
import type { PeriodSelection } from '@/lib/period';

function AiMark({ className }: { className?: string }) {
  return (
    <span
      aria-hidden="true"
      className={cn('flex size-8 shrink-0 items-center justify-center rounded-full text-white', className)}
      style={{ background: 'linear-gradient(135deg, #34c3a0, #0a84ff 55%, #8e6cf0)' }}
    >
      <Sparkles size={16} />
    </span>
  );
}

function GeneratingState() {
  return (
    <div aria-live="polite" aria-busy="true" className="fade-in">
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

/** Mirrors the most common first state (an explanation and the Analyze button), so loading doesn't resize the card. */
function InsightSkeleton() {
  return (
    <div aria-hidden="true">
      {/* The explanation runs one line on wide screens, two on tablets and three on phones. */}
      <div className="text-[0.9375rem]">
        <span className="flex h-[1.45em] items-center">
          <Skeleton className="h-4 w-full" />
        </span>
        <span className="flex h-[1.45em] items-center lg:hidden">
          <Skeleton className="h-4 w-[62%]" />
        </span>
        <span className="flex h-[1.45em] items-center sm:hidden">
          <Skeleton className="h-4 w-1/3" />
        </span>
      </div>
      <Skeleton className="mt-4 h-10 w-44 rounded-full" />
    </div>
  );
}

type AiInsightCardProps = { period: PeriodSelection; hasData: boolean };

const INSIGHT_CARD = 'card p-6 md:p-7';

function InsightTitle({ id }: { id?: string }) {
  return (
    <h2 id={id} className="text-[1.0625rem] font-semibold tracking-[-0.01em]">
      Financial summary
    </h2>
  );
}

/**
 * The AI summary for a period, with every state: loading, unavailable, not yet generated, generating, stale and
 * failed. The card shell and its header stay put through all of them; only the body changes, animating its height.
 * If it fails to render, the same shell shows a short message with Try again, and the page around it carries on.
 */
export function AiInsightCard(props: AiInsightCardProps) {
  return (
    <WidgetBoundary
      name="ai-insight"
      message="This insight couldn’t be shown."
      minHeight={96}
      className="py-2"
      queryKeys={[['analysis']]}
      resetKeys={[props.period]}
      shell={(fallback) => (
        <section aria-label="Financial summary" className={INSIGHT_CARD}>
          <div className="mb-3 flex items-center gap-3">
            <AiMark />
            <InsightTitle />
          </div>
          {fallback}
        </section>
      )}
    >
      <AiInsightCardContent {...props} />
    </WidgetBoundary>
  );
}

/**
 * The line under a summary FinSight wrote without AI: calm, short, and honest about why. A reason that no longer applies
 * (a key was added, today's allowance reset) drops back to the plain first sentence.
 */
export function builtInNote(data: Pick<AnalysisResponse, 'fallbackReason' | 'availability'>): { text: string; settingsLink?: string } {
  const lead = 'Written by FinSight without AI.';
  const { configured, blocked } = data.availability;
  switch (data.fallbackReason) {
    case 'key_refused':
      return !configured || blocked === 'key_refused' ? { text: `${lead} Google refused the Gemini key.`, settingsLink: 'Update it in Settings' } : { text: lead };
    case 'quota_exhausted':
      return { text: `${lead} Gemini’s daily limit was reached; try again after it resets.` };
    case 'limit_reached':
      return blocked === 'limit_reached' ? { text: `${lead} Today’s AI allowance is used up; it resets tomorrow.` } : { text: lead };
    case 'unavailable':
      return { text: `${lead} Gemini couldn’t be reached just now.` };
    default:
      return configured ? { text: lead } : { text: 'Written by FinSight from your numbers, without AI.' };
  }
}

function AiInsightCardContent({ period, hasData }: AiInsightCardProps) {
  const analysis = useAnalysis(period);
  const generate = useGenerateAnalysis(period);
  // Read from the cache only: the app shell has already loaded the session. Demo accounts can't add a key.
  const session = useQuery({ queryKey: keys.session, queryFn: api.session, enabled: false });
  // Analysis takes ~20s and is rate limited; a double click must never start two.
  const once = useSingleFlight();
  const analyze = () => void once(() => generate.mutateAsync());
  const titleId = useId();
  const data = analysis.data;
  const canManageKey = !!session.data?.user && !session.data.user.isDemo;

  // Whether asking Gemini could work right now. Without it, FinSight writes the summary itself.
  const aiReady = !!data && data.availability.configured && !data.availability.blocked;

  const addKeyLink =
    data && !data.availability.configured && canManageKey ? (
      <Link to="/settings" className={buttonStyles({ variant: 'plain', size: 'sm' })}>
        Add a Gemini key
      </Link>
    ) : null;

  function body() {
    if (analysis.isPending) return <InsightSkeleton />;
    if (!data) return <ErrorState className="py-2" message={errorMessage(analysis.error)} onRetry={() => void analysis.refetch()} />;
    if (generate.isPending) return <GeneratingState />;
    if (!data.availability.enabled) {
      return (
        <p className="fade-in text-[0.9375rem] text-label-secondary">
          AI insights are turned off.{' '}
          <Link to="/settings" className="text-accent">
            Turn on in Settings
          </Link>
        </p>
      );
    }
    if (data.analysis) {
      const builtIn = data.source === 'builtIn';
      const note = builtIn ? builtInNote(data) : null;
      return (
        <div className="fade-in">
          <p className="text-[1.125rem] leading-relaxed text-pretty text-label">{data.analysis.summary}</p>
          {note && (
            <p className="mt-3 text-[0.875rem] text-label-secondary">
              {note.text}
              {note.settingsLink && canManageKey && (
                <>
                  {' '}
                  <Link to="/settings" className="text-accent">
                    {note.settingsLink}
                  </Link>
                  .
                </>
              )}
            </p>
          )}
          {data.state === 'stale' && (
            <p className="mt-3 flex items-center gap-2 text-[0.875rem] text-attention">
              <Info size={15} aria-hidden="true" />
              Your transactions changed since this was written.
            </p>
          )}
          <div className="mt-5 flex flex-wrap items-center gap-2">
            {!builtIn ? (
              <Button variant={data.state === 'stale' ? 'secondary' : 'plain'} size="sm" icon={<RefreshCw size={14} aria-hidden="true" />} onClick={analyze}>
                Regenerate
              </Button>
            ) : aiReady ? (
              <Button variant={data.state === 'stale' ? 'secondary' : 'plain'} size="sm" icon={<Sparkles size={14} aria-hidden="true" />} onClick={analyze}>
                {data.fallbackReason === 'not_configured' ? 'Analyze with AI' : 'Try again with AI'}
              </Button>
            ) : (
              data.state === 'stale' && (
                <Button variant="secondary" size="sm" icon={<RefreshCw size={14} aria-hidden="true" />} onClick={analyze}>
                  Update summary
                </Button>
              )
            )}
            {builtIn && addKeyLink}
          </div>
        </div>
      );
    }
    if (hasData) {
      return aiReady ? (
        <div className="fade-in">
          <p className="text-[0.9375rem] text-label-secondary">Get a plain-language explanation of this period: what changed, what stands out, and where you could save.</p>
          <Button className="mt-4" icon={<Sparkles size={16} aria-hidden="true" />} onClick={analyze}>
            Analyze spending
          </Button>
        </div>
      ) : (
        <div className="fade-in">
          <p className="text-[0.9375rem] text-label-secondary">Get a plain-language summary of this period: what changed and what stands out. Without AI, FinSight writes it from your numbers.</p>
          <div className="mt-4 flex flex-wrap items-center gap-2">
            <Button icon={<Sparkles size={16} aria-hidden="true" />} onClick={analyze}>
              Summarize spending
            </Button>
            {addKeyLink}
          </div>
        </div>
      );
    }
    return <p className="fade-in text-[0.9375rem] text-label-secondary">There are no transactions in this period to analyze.</p>;
  }

  return (
    <section aria-labelledby={titleId} aria-busy={analysis.isPending || generate.isPending} className={INSIGHT_CARD}>
      <div>
        <div className="mb-3 flex items-center gap-3">
          <AiMark />
          <InsightTitle id={titleId} />
          {data?.generatedAt && !generate.isPending && <span className="caption fade-in ml-auto hidden sm:inline">Generated {formatRelativeTime(data.generatedAt)}</span>}
        </div>

        <AutoHeight>
          {body()}
          {generate.isError && (
            <p role="alert" className="mt-4 text-[0.9375rem] text-critical">
              {errorMessage(generate.error)}
            </p>
          )}
        </AutoHeight>
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

/** Shows what the validator changed. Corrections are never hidden. A summary FinSight wrote itself has none, so shows nothing. */
export function AnalysisCorrections({ response }: { response: AnalysisResponse }) {
  const [open, setOpen] = useState(false);
  if (response.source === 'builtIn') return null;
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
        <ChevronDown size={14} className={cn('transition-transform duration-200', open && 'rotate-180')} aria-hidden="true" />
      </button>
      <Collapse open={open}>
        <ul className="caption list-disc space-y-1 pt-2 pl-8">
          {response.corrections.map((c, i) => (
            <li key={i}>{c.message}</li>
          ))}
        </ul>
      </Collapse>
    </div>
  );
}
