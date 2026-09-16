import { useEffect, useState } from 'react';

const VARIABLES = ['--series-1', '--series-2', '--series-3', '--series-neutral', '--chart-grid', '--chart-axis', '--label', '--label-secondary', '--surface', '--positive'] as const;

export type ChartColors = Record<(typeof VARIABLES)[number], string>;

function read(): ChartColors {
  const style = getComputedStyle(document.documentElement);
  return Object.fromEntries(VARIABLES.map((name) => [name, style.getPropertyValue(name).trim()])) as ChartColors;
}

/**
 * SVG presentation attributes can't use CSS variables, so charts read the resolved token values and
 * re-read them whenever the theme changes (OS setting or the explicit choice in Settings).
 */
export function useChartColors(): ChartColors {
  const [colors, setColors] = useState<ChartColors>(read);

  useEffect(() => {
    const update = () => setColors(read());
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    media.addEventListener('change', update);
    const observer = new MutationObserver(update);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    return () => {
      media.removeEventListener('change', update);
      observer.disconnect();
    };
  }, []);

  return colors;
}
