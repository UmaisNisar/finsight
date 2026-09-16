import { ChevronRight } from 'lucide-react';
import { Link } from 'react-router';
import type { Summary } from '@/api/schemas';
import { groupStyle } from '@/lib/categories';
import { formatMoney, formatPercent } from '@/lib/format';

const MAX_ROWS = 6;

interface Row {
  groupId: string;
  name: string;
  amount: number;
  sharePercent: number;
  changePercent: number | null;
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
    },
  ];
}

/**
 * Where the money went, by category group: a proportion strip for the whole, and a labelled list for
 * exact values. Colour is fixed per group; the list carries names so colour is never the only cue.
 */
export function SpendingBreakdown({ groups, currency, compareLabel, linkSuffix }: { groups: Summary['groups']; currency: string; compareLabel?: string; linkSuffix: string }) {
  const rows = foldGroups(groups);

  return (
    <div>
      <div className="flex h-3 w-full gap-[2px] overflow-hidden rounded-full" aria-hidden="true">
        {rows.map((row) => (
          <div
            key={row.groupId + row.name}
            className="h-full first:rounded-l-full last:rounded-r-full"
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
                <span className="caption tabular block">
                  {formatPercent(row.sharePercent, { digits: 0 })} of spending
                  {row.changePercent !== null && Math.abs(row.changePercent) >= 1 && (
                    <span className={increase ? 'text-attention' : undefined}>
                      {' · '}
                      {formatPercent(row.changePercent, { signed: true, digits: 0 })}
                      {compareLabel && ` vs ${compareLabel.slice(0, 3)}`}
                    </span>
                  )}
                </span>
              </span>
              <span className="tabular text-right text-[0.9375rem] font-medium">{formatMoney(row.amount, currency, { whole: true })}</span>
              <span className="w-4 shrink-0" aria-hidden="true" />
            </>
          );

          return (
            <li key={row.groupId + row.name}>
              {row.name === 'Everything else' ? (
                <div className="flex h-14 items-center gap-3 px-2">{content}</div>
              ) : (
                <Link
                  to={`/transactions?group=${row.groupId}${linkSuffix}`}
                  className="group relative flex h-14 items-center gap-3 rounded-xl px-2 transition-colors hover:bg-fill"
                >
                  {content}
                  <ChevronRight size={16} className="absolute right-2 text-label-tertiary opacity-0 transition-opacity group-hover:opacity-100" aria-hidden="true" />
                </Link>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
