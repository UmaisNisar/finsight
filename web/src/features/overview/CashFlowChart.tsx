import { useReducedMotion } from 'motion/react';
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis, type TooltipContentProps } from 'recharts';
import type { Summary } from '@/api/schemas';
import { WidgetBoundary } from '@/components/errors/WidgetBoundary';
import { Skeleton } from '@/components/ui/primitives';
import { useChartColors } from '@/hooks/useChartColors';
import { formatMoney, formatMoneyCompact, formatMonth, formatMonthYear } from '@/lib/format';

interface Point {
  month: string;
  label: string;
  income: number;
  spending: number;
  hasData: boolean;
}

function ChartTooltip({ active, payload, currency }: Partial<TooltipContentProps<number, string>> & { currency: string }) {
  const point = payload?.[0]?.payload as Point | undefined;
  if (!active || !point) return null;
  const net = point.income - point.spending;

  return (
    <div className="tooltip-surface min-w-44 rounded-xl px-3 py-2 text-[0.75rem] leading-[1.35]">
      <p className="mb-1.5 font-semibold">{formatMonthYear(point.month)}</p>
      {point.hasData ? (
        <dl className="tabular space-y-1">
          <div className="flex items-center justify-between gap-4">
            <dt className="flex items-center gap-1.5 text-label-secondary">
              <span className="size-2 rounded-full" style={{ background: 'var(--series-3)' }} />
              Income
            </dt>
            <dd>{formatMoney(point.income, currency, { whole: true })}</dd>
          </div>
          <div className="flex items-center justify-between gap-4">
            <dt className="flex items-center gap-1.5 text-label-secondary">
              <span className="size-2 rounded-full" style={{ background: 'var(--series-1)' }} />
              Spent
            </dt>
            <dd>{formatMoney(point.spending, currency, { whole: true })}</dd>
          </div>
          <div className="flex items-center justify-between gap-4 border-t border-separator pt-1">
            <dt className="text-label-secondary">{net >= 0 ? 'Saved' : 'Overspent'}</dt>
            <dd className="font-medium">{formatMoney(Math.abs(net), currency, { whole: true })}</dd>
          </div>
        </dl>
      ) : (
        <p className="text-label-secondary">No statements for this month</p>
      )}
    </div>
  );
}

/** Placeholder bars inside the real plot area, so the chart appears in place when its data arrives. */
function ChartSkeleton() {
  const heights = [55, 70, 45, 80, 60, 72];
  return (
    <div className="flex h-full items-end gap-[8%] pr-2 pb-7 pl-14" aria-hidden="true">
      {heights.map((h, i) => (
        <Skeleton key={i} className="flex-1 rounded-b-none" style={{ height: `${h}%` }} />
      ))}
    </div>
  );
}

type CashFlowChartProps = { monthly?: Summary['monthly']; currency: string };

/** Income vs spending per month. Answers: "Am I consistently spending less than I earn?" Pass no data while loading. */
export function CashFlowChart(props: CashFlowChartProps) {
  // If the chart fails to render, the card keeps its height and offers Try again; new data also clears the error.
  return (
    <WidgetBoundary name="cash-flow" message="The cash flow chart couldn’t be shown." minHeight={288} queryKeys={[['summary']]} resetKeys={[props.monthly]}>
      <CashFlowChartContent {...props} />
    </WidgetBoundary>
  );
}

function CashFlowChartContent({ monthly, currency }: CashFlowChartProps) {
  const colors = useChartColors();
  const reduceMotion = useReducedMotion() ?? false;
  const data: Point[] = (monthly ?? []).map((m) => ({
    month: m.month,
    label: formatMonth(m.month),
    income: m.income,
    spending: m.expenses,
    hasData: m.hasData,
  }));

  const withData = data.filter((d) => d.hasData);
  const description =
    withData.length === 0
      ? 'No monthly data yet.'
      : `Income and spending for ${withData.length} months. ` +
        `Spending was below income in ${withData.filter((d) => d.spending < d.income).length} of them.`;

  return (
    <figure className="m-0">
      <div className="mb-3 flex items-center gap-4 text-[0.8125rem] text-label-secondary" aria-hidden="true">
        <span className="flex items-center gap-1.5">
          <span className="size-2.5 rounded-full" style={{ background: 'var(--series-3)' }} />
          Income
        </span>
        <span className="flex items-center gap-1.5">
          <span className="size-2.5 rounded-full" style={{ background: 'var(--series-1)' }} />
          Spent
        </span>
      </div>
      {!monthly ? (
        <div className="h-64">
          <ChartSkeleton />
        </div>
      ) : (
      <div className="fade-in h-64" role="img" aria-label={description}>
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={data} barGap={2} barCategoryGap="28%" margin={{ top: 4, right: 0, bottom: 0, left: 0 }}>
            <CartesianGrid vertical={false} stroke={colors['--chart-grid']} />
            <XAxis dataKey="label" axisLine={false} tickLine={false} tick={{ fill: colors['--chart-axis'], fontSize: 12 }} dy={6} />
            <YAxis
              axisLine={false}
              tickLine={false}
              width={52}
              tickCount={4}
              tick={{ fill: colors['--chart-axis'], fontSize: 12 }}
              tickFormatter={(v: number) => formatMoneyCompact(v, currency)}
            />
            <Tooltip cursor={{ fill: colors['--chart-grid'], opacity: 0.6, radius: 8 }} content={<ChartTooltip currency={currency} />} />
            <Bar dataKey="income" name="Income" fill={colors['--series-3']} radius={[4, 4, 0, 0]} maxBarSize={18} isAnimationActive={!reduceMotion} animationDuration={500} />
            <Bar dataKey="spending" name="Spent" fill={colors['--series-1']} radius={[4, 4, 0, 0]} maxBarSize={18} isAnimationActive={!reduceMotion} animationDuration={500} />
          </BarChart>
        </ResponsiveContainer>
      </div>
      )}
      <table className="sr-only">
        <caption>Monthly income and spending</caption>
        <thead>
          <tr>
            <th scope="col">Month</th>
            <th scope="col">Income</th>
            <th scope="col">Spent</th>
          </tr>
        </thead>
        <tbody>
          {data.map((d) => (
            <tr key={d.month}>
              <th scope="row">{formatMonthYear(d.month)}</th>
              <td>{d.hasData ? formatMoney(d.income, currency) : 'No data'}</td>
              <td>{d.hasData ? formatMoney(d.spending, currency) : 'No data'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </figure>
  );
}
