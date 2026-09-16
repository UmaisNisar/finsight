import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis, type TooltipContentProps } from 'recharts';
import type { Summary } from '@/api/schemas';
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
    <div className="glass min-w-44 rounded-xl px-3.5 py-2.5 text-[0.8125rem] shadow-float">
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

/** Income vs spending per month. Answers: "Am I consistently spending less than I earn?" */
export function CashFlowChart({ monthly, currency }: { monthly: Summary['monthly']; currency: string }) {
  const colors = useChartColors();
  const data: Point[] = monthly.map((m) => ({
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
      <div className="h-64" role="img" aria-label={description}>
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
            <Bar dataKey="income" name="Income" fill={colors['--series-3']} radius={[4, 4, 0, 0]} maxBarSize={18} />
            <Bar dataKey="spending" name="Spent" fill={colors['--series-1']} radius={[4, 4, 0, 0]} maxBarSize={18} />
          </BarChart>
        </ResponsiveContainer>
      </div>
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
