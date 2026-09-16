import { CartesianGrid, Line, LineChart, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis, type TooltipContentProps } from 'recharts';
import type { Summary } from '@/api/schemas';
import { useChartColors } from '@/hooks/useChartColors';
import { formatMonth, formatMonthYear, formatPercent } from '@/lib/format';

interface Point {
  month: string;
  label: string;
  rate: number | null;
}

function RateTooltip({ active, payload }: Partial<TooltipContentProps<number, string>>) {
  const point = payload?.[0]?.payload as Point | undefined;
  if (!active || !point) return null;
  return (
    <div className="glass rounded-xl px-3.5 py-2.5 text-[0.8125rem] shadow-float">
      <p className="font-semibold">{formatMonthYear(point.month)}</p>
      <p className="tabular text-label-secondary">{point.rate === null ? 'No income recorded' : `Saved ${formatPercent(point.rate)} of income`}</p>
    </div>
  );
}

/** One series, one axis: answers "Is the share of income I keep going up or down?" */
export function SavingsRateChart({ monthly }: { monthly: Summary['monthly'] }) {
  const colors = useChartColors();
  const data: Point[] = monthly.filter((m) => m.hasData).map((m) => ({ month: m.month, label: formatMonth(m.month), rate: m.savingsRate }));

  if (data.length < 2) {
    return <p className="text-[0.9375rem] text-label-secondary">Trends appear once you have at least two months of statements.</p>;
  }

  const rates = data.map((d) => d.rate).filter((r): r is number => r !== null);
  const average = rates.reduce((a, b) => a + b, 0) / Math.max(rates.length, 1);
  const last = data[data.length - 1];

  return (
    <figure className="m-0">
      <div className="h-56" role="img" aria-label={`Savings rate over ${data.length} months, averaging ${formatPercent(average)}. Latest: ${last?.rate === null || !last ? 'no income' : formatPercent(last.rate)}.`}>
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={data} margin={{ top: 12, right: 12, bottom: 0, left: 0 }}>
            <CartesianGrid vertical={false} stroke={colors['--chart-grid']} />
            <XAxis dataKey="label" axisLine={false} tickLine={false} tick={{ fill: colors['--chart-axis'], fontSize: 12 }} dy={6} />
            <YAxis axisLine={false} tickLine={false} width={40} tickCount={4} tick={{ fill: colors['--chart-axis'], fontSize: 12 }} tickFormatter={(v: number) => `${v}%`} />
            <ReferenceLine y={0} stroke={colors['--chart-axis']} strokeOpacity={0.5} />
            <Tooltip cursor={{ stroke: colors['--chart-axis'], strokeWidth: 1 }} content={<RateTooltip />} />
            <Line
              type="monotone"
              dataKey="rate"
              stroke={colors['--series-1']}
              strokeWidth={2}
              strokeLinecap="round"
              strokeLinejoin="round"
              connectNulls
              dot={{ r: 4, fill: colors['--series-1'], stroke: colors['--surface'], strokeWidth: 2 }}
              activeDot={{ r: 6, fill: colors['--series-1'], stroke: colors['--surface'], strokeWidth: 2 }}
            />
          </LineChart>
        </ResponsiveContainer>
      </div>
      <figcaption className="caption mt-2">Average {formatPercent(average)} across these months.</figcaption>
      <table className="sr-only">
        <caption>Savings rate by month</caption>
        <tbody>
          {data.map((d) => (
            <tr key={d.month}>
              <th scope="row">{formatMonthYear(d.month)}</th>
              <td>{d.rate === null ? 'No income' : formatPercent(d.rate)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </figure>
  );
}
