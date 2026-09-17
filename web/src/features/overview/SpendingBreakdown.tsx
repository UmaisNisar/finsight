import { ChevronRight } from 'lucide-react';
import { Link } from 'react-router';
import type { Summary } from '@/api/schemas';
import { WidgetBoundary } from '@/components/errors/WidgetBoundary';
import { AnimatedNumber } from '@/components/ui/AnimatedNumber';
import { Skeleton } from '@/components/ui/primitives';
import { groupStyle } from '@/lib/categories';
import { formatMoney, formatPercent } from '@/lib/format';

const MAX_ROWS = 6;

interface Row {
  groupId: string;
  name: string;
  amount: number;
  sharePercent: number;
  changePercent: number | null;
  folded?: boolean;
}

function foldGroups(groups: Summary['groups']): Row[] {
  if (groups.length <= MAX_ROWS) return groups;
  const head = groups.slice(0, MAX_ROWS - 1);
  const tail = groups.slice(MAX_ROWS - 1);
  return [
    ...head,
    {
      groupId: 'other',
      name: 'Everything else',
      amount: tail.reduce((sum, g) => sum + g.amount, 0),
      sharePercent: Math.round(tail.reduce((sum, g) => sum + g.sharePercent, 0) * 10) / 10,
      changePercent: null,
      folded: true,
    },
  ];
}

function BreakdownSkeleton() {
  return (
    <div aria-hidden="true">
      <Skeleton className="h-3 w-full rounded-full" />
      <div className="mt-5 space-y-1">
        {Array.from({ length: MAX_ROWS }, (_, i) => (
          <div key={i} className="flex h-14 items-center gap-3 px-2">
            <Skeleton className="size-2.5 rounded-full" />
            <span className="flex-1 space-y-1.5">
              <Skeleton className="h-3.5 w-28" />
              <Skeleton className="h-3 w-24" />
            </span>
            <Skeleton className="mr-7 h-3.5 w-14" />
          </div>
        ))}
      </div>
    </div>
  );
}

/**
 * Where the money went, by category group: a proportion strip for the whole, and a labelled list for
 * exact values. Colour is fixed per group; the list carries names so colour is never the only cue.
 * `compareLabel` is the short name of the comparison period ("Jul", "previous").
 */
type SpendingBreakdownProps = { groups?: Summary['groups']; currency: string; compareLabel?: string; linkFor: (groupId: string) => string };

export function SpendingBreakdown(props: SpendingBreakdownProps) {
  return (
    <WidgetBoundary name="breakdown" message="The spending breakdown couldn’t be shown." minHeight={320} queryKeys={[['summary']]} resetKeys={[props.groups]}>
      <SpendingBreakdownContent {...props} />
    </WidgetBoundary>
  );
}

function SpendingBreakdownContent({ groups, currency, compareLabel, linkFor }: SpendingBreakdownProps) {
  if (!groups) return <BreakdownSkeleton />;
  const rows = foldGroups(groups);

  return (
    <div className="fade-in">
      <div className="flex h-3 w-full gap-[2px] overflow-hidden rounded-full" aria-hidden="true">
        {rows.map((row) => (
          <div
            key={row.groupId + row.name}
            className="h-full transition-[width] duration-500 ease-[cubic-bezier(0.2,0.8,0.2,1)] first:rounded-l-full last:rounded-r-full"
            style={{ width: `${Math.max(row.sharePercent, 1.5)}%`, background: groupStyle(row.groupId).color }}
          />
        ))}
      </div>

      <ul className="mt-5 space-y-1" aria-label="Spending by category">
        {rows.map((row) => {
          const increase = row.changePercent !== null && row.changePercent > 0;
          const content = (
            <>
              <span className="size-2.5 shrink-0 rounded-full" style={{ background: groupStyle(row.groupId).color }} aria-hidden="true" />
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[0.9375rem]">{row.name}</span>
                <span className="caption tabular block truncate">
                  {formatPercent(row.sharePercent, { digits: 0 })} of spending
                  {row.changePercent !== null && Math.abs(row.changePercent) >= 1 && (
                    <span className={increase ? 'text-attention' : undefined}>
                      {' · '}
                      {formatPercent(row.changePercent, { signed: true, digits: 0 })}
                      {compareLabel && ` vs ${compareLabel}`}
                    </span>
                  )}
                </span>
              </span>
              <AnimatedNumber className="text-right text-[0.9375rem] font-medium" value={row.amount} format={(amount) => formatMoney(amount, currency, { whole: true })} />
              <span className="w-4 shrink-0" aria-hidden="true" />
            </>
          );

          return (
            <li key={row.groupId + row.name}>
              {row.folded ? (
                <div className="flex h-14 items-center gap-3 px-2">{content}</div>
              ) : (
                <Link to={linkFor(row.groupId)} className="group relative flex h-14 items-center gap-3 rounded-xl px-2 transition-colors hover:bg-fill">
                  {content}
                  <ChevronRight size={16} className="absolute right-2 text-label-tertiary opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100" aria-hidden="true" />
                </Link>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
