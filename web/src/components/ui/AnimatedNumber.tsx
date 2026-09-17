import { useLayoutEffect, useRef, useState } from 'react';
import { usePrefersReducedMotion } from '@/hooks/usePrefersReducedMotion';
import { cn } from '@/lib/cn';

/*
  Numbers that change like SwiftUI's `.contentTransition(.numericText())`: every digit that changes ticks out as its
  replacement ticks in, all in unison, upward when the value rises and downward when it falls. Unchanged digits don't
  move at all.

  The formatted string is split into glyphs keyed by what they mean rather than where they sit: integer digits by
  place value counted from the decimal point, separators by the digit to their left, the currency symbol and sign by
  their distance from the number. So "$987" becoming "$1,024" keeps the ones, tens and hundreds columns (they slide to
  their new digits) and adds a thousands digit and a separator (they grow in). Every frame shows glyphs of the two real
  formatted strings, never an unformatted intermediate like "$1543.2891".

  React renders once per value change; the motion itself is the Web Animations API on transforms and widths. Glyphs
  are drawn with CSS generated content, so the DOM's only text is the visually hidden final value: screen readers,
  copy and paste, and text queries get "$1,024" once, never the digits in motion.
*/

export interface Glyph {
  key: string;
  char: string;
  /** 0–9 for a digit column, null for a symbol, separator or sign. */
  digit: number | null;
  /** Left-to-right position, stable across values, used to place glyphs that are leaving. */
  order: number;
}

type Phase = 'present' | 'entering' | 'exiting';
type Rendered = Glyph & { phase: Phase };

const DIGIT = /[0-9]/;

let localeDecimal: string | null = null;
function decimalSeparator(): string {
  localeDecimal ??= new Intl.NumberFormat().formatToParts(1.5).find((part) => part.type === 'decimal')?.value ?? '.';
  return localeDecimal;
}

/** Splits a formatted number into glyphs keyed by meaning (see above). Exported for tests. */
export function splitGlyphs(text: string, decimal = decimalSeparator()): Glyph[] {
  const chars = [...text];
  const first = chars.findIndex((c) => DIGIT.test(c));
  if (first === -1) return chars.map((char, i) => ({ key: `t${i}:${char}`, char, digit: null, order: i }));
  let last = chars.length - 1;
  while (!DIGIT.test(chars[last] ?? '0')) last--;

  // The decimal separator is the last non-digit inside the number, and only if it is the locale's decimal mark.
  let dec = -1;
  for (let i = last; i > first; i--) {
    const c = chars[i] ?? '';
    if (DIGIT.test(c)) continue;
    if (c === decimal) dec = i;
    break;
  }

  const glyphs: Glyph[] = [];
  for (let i = 0; i < first; i++) {
    const char = chars[i] ?? '';
    const distance = first - 1 - i;
    glyphs.push({ key: `p${distance}:${char}`, char, digit: null, order: -1000 - distance });
  }

  const integer: Glyph[] = [];
  let place = 0;
  for (let i = dec === -1 ? last : dec - 1; i >= first; i--) {
    const char = chars[i] ?? '';
    if (DIGIT.test(char)) {
      integer.push({ key: `i${place}`, char, digit: Number(char), order: -2 * place });
      place++;
    } else {
      integer.push({ key: `g${place}:${char}`, char, digit: null, order: -2 * place + 1 });
    }
  }
  glyphs.push(...integer.reverse());

  if (dec !== -1) {
    glyphs.push({ key: `d:${decimal}`, char: decimal, digit: null, order: 0.5 });
    for (let i = dec + 1; i <= last; i++) {
      const char = chars[i] ?? '';
      const index = i - dec - 1;
      glyphs.push(DIGIT.test(char) ? { key: `f${index}`, char, digit: Number(char), order: 1 + index } : { key: `fs${index}:${char}`, char, digit: null, order: 1 + index });
    }
  }

  for (let i = last + 1; i < chars.length; i++) {
    const char = chars[i] ?? '';
    const distance = i - last - 1;
    glyphs.push({ key: `x${distance}:${char}`, char, digit: null, order: 1000 + distance });
  }
  return glyphs;
}

/** The next glyph list: new glyphs enter, missing ones stay in place while they leave. */
function merge(previous: Rendered[], next: Glyph[]): Rendered[] {
  const nextKeys = new Set(next.map((g) => g.key));
  const shown = new Set(previous.filter((g) => g.phase !== 'exiting').map((g) => g.key));
  const leaving = previous.filter((g) => !nextKeys.has(g.key)).map((g) => ({ ...g, phase: 'exiting' as const }));
  const staying = next.map((g) => ({ ...g, phase: shown.has(g.key) ? ('present' as const) : ('entering' as const) }));
  return [...staying, ...leaving].sort((a, b) => a.order - b.order);
}

interface MotionPlan {
  enabled: boolean;
  /** 1 when the value went up (digits move up), -1 when it went down. */
  trend: 1 | -1;
  /** Increments per value change. */
  epoch: number;
}

/*
  One tick, shared by every glyph that changes, in every number. The same duration, curve, travel and fade for all of
  them, started in the same commit (so on the same animation frame), means the changed digits of a figure move as
  one and land together, and figures that change together (the Overview hero) finish together. The duration is fixed
  rather than scaled by the size of the change for exactly that reason.
*/
export const TICK = {
  duration: 480,
  /** Critically damped: no overshoot, no long creep, so every digit is on the baseline by the end. */
  easing: 'cubic-bezier(0.25, 1, 0.5, 1)',
} as const;

/** About a third of a digit's height: a crisp tick, not a roll. */
const TRAVEL = 0.25;
const shift = (lines: number) => (lines === 0 ? 'none' : `translateY(${lines * TRAVEL}em)`);

/** The new glyph rises (or falls) a short way into place, fully visible well before it lands. */
export function incomingKeyframes(trend: 1 | -1): Keyframe[] {
  return [
    { transform: shift(trend), opacity: 0 },
    { opacity: 0, offset: 0.08 },
    { opacity: 1, offset: 0.55 },
    { transform: shift(0), opacity: 1 },
  ];
}

/** The old glyph moves the same distance on its way out and has faded before it gets there. */
export function outgoingKeyframes(trend: 1 | -1): Keyframe[] {
  return [
    { transform: shift(0), opacity: 1 },
    { opacity: 0, offset: 0.45 },
    { transform: shift(-trend), opacity: 0 },
  ];
}

function GlyphView({ glyph, plan, onExited }: { glyph: Rendered; plan: MotionPlan; onExited: (key: string) => void }) {
  const column = useRef<HTMLSpanElement>(null);
  const pair = useRef<HTMLSpanElement>(null);
  const shownDigit = useRef<number | null>(glyph.digit);
  const tick = useRef<Animation[]>([]);
  const resize = useRef<{ animations: Animation[]; run: string } | null>(null);

  // A changed digit: the new one (::before) ticks in over the old one (::after) ticking out, in place.
  useLayoutEffect(() => {
    const digit = glyph.digit;
    const element = pair.current;
    const previous = shownDigit.current;
    shownDigit.current = digit;
    if (digit === null || previous === null || previous === digit || !element || !plan.enabled) return;

    // Interrupted mid-tick: the digit that was arriving is the one that leaves.
    tick.current.forEach((animation) => animation.cancel());
    element.dataset.a = String(digit);
    element.dataset.b = String(previous);
    const incoming = element.animate(incomingKeyframes(plan.trend), { ...TICK, pseudoElement: '::before' });
    const outgoing = element.animate(outgoingKeyframes(plan.trend), { ...TICK, pseudoElement: '::after' });
    outgoing.onfinish = () => {
      if (tick.current[1] === outgoing) element.dataset.b = '';
    };
    tick.current = [incoming, outgoing];
    // eslint-disable-next-line react-hooks/exhaustive-deps -- ticks only when the digit itself changes
  }, [glyph.digit]);

  // Glyphs that appear tick in exactly like a changed digit while their width opens; glyphs that go tick out while
  // it closes, then are removed.
  useLayoutEffect(() => {
    const element = column.current;
    if (glyph.phase === 'present') return;
    if (!element || !plan.enabled) {
      if (glyph.phase === 'exiting') onExited(glyph.key);
      return;
    }
    const run = `${glyph.phase}:${plan.epoch}`;
    const current = resize.current;
    // Already running for this change (an effect re-run, as in development's Strict Mode).
    if (current?.run === run && current.animations.every((a) => a.playState !== 'idle')) return;
    const width = element.getBoundingClientRect().width;
    const interrupted = current?.animations[0]?.playState === 'running';
    current?.animations.forEach((animation) => animation.cancel());

    let animations: Animation[];
    if (glyph.phase === 'entering') {
      const natural = element.getBoundingClientRect().width;
      animations = [
        // The column is fully open by the time the glyph is mostly visible, so it is never seen clipped.
        element.animate([{ width: `${interrupted ? width : 0}px` }, { width: `${natural}px`, offset: 0.45 }, { width: `${natural}px` }], TICK),
        element.animate(incomingKeyframes(plan.trend), TICK),
      ];
    } else {
      animations = [
        // The column closes only once the glyph has faded (see outgoingKeyframes).
        element.animate([{ width: `${width}px` }, { width: `${width}px`, offset: 0.45 }, { width: '0px' }], { ...TICK, fill: 'forwards' }),
        element.animate(outgoingKeyframes(plan.trend), { ...TICK, fill: 'forwards' }),
      ];
      (animations[0] as Animation).onfinish = () => onExited(glyph.key);
    }
    resize.current = { animations, run };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- runs once per value change that moves this glyph in or out
  }, [glyph.phase, plan.epoch]);

  return (
    <span ref={column} className={cn('numeric-glyph', glyph.digit !== null && 'numeric-digit')} data-char={glyph.char} data-phase={glyph.phase}>
      {glyph.digit !== null && <span ref={pair} className="numeric-pair" data-a={glyph.digit} data-b="" />}
    </span>
  );
}

const supportsAnimation = () => typeof HTMLElement !== 'undefined' && typeof HTMLElement.prototype.animate === 'function';

export interface AnimatedNumberProps {
  value: number;
  /**
   * One of the app's formatters, so the figure reads exactly like the static one would. It must round to its display
   * precision (formatMoney and formatPercent do; wrap plain counts in Math.round), since a count-up formats fractions.
   */
  format: (value: number) => string;
  className?: string;
  /** Count up from zero when first shown. For hero figures replacing a loading placeholder only. */
  countUp?: boolean;
}

const COUNT_UP_DURATION = 750;

/**
 * A figure whose changed digits tick to the new value together. Same value, same text: nothing moves. With reduced
 * motion the new value simply appears. Width stays fixed while digits change (tabular figures); it changes, smoothly,
 * only when the number of glyphs does.
 */
export function AnimatedNumber({ value, format, className, countUp = false }: AnimatedNumberProps) {
  const reducedMotion = usePrefersReducedMotion();
  const enabled = !reducedMotion && supportsAnimation();
  const text = format(value);
  const [state, setState] = useState(() => ({ text, value, glyphs: splitGlyphs(text).map((g): Rendered => ({ ...g, phase: 'present' })), epoch: 0, trend: 1 as 1 | -1 }));
  const [counting, setCounting] = useState(() => countUp && enabled && value !== 0);
  const counter = useRef<HTMLSpanElement>(null);

  // Adjusting state while rendering, so the new glyphs are laid out in the same commit as the new value.
  if (state.text !== text) {
    if (counting) setCounting(false);
    const next = splitGlyphs(text);
    setState({
      text,
      value,
      glyphs: enabled ? merge(state.glyphs, next) : next.map((g) => ({ ...g, phase: 'present' })),
      epoch: state.epoch + 1,
      trend: value < state.value ? -1 : 1,
    });
  }

  // The count-up writes each frame to an attribute the overlay draws, over the final glyphs (hidden, holding the
  // final width), so nothing re-renders per frame and nothing moves when the glyphs take over.
  useLayoutEffect(() => {
    const element = counter.current;
    if (!counting || !element) return;
    const target = state.value;
    const start = performance.now();
    let frame = 0;
    const tick = (now: number) => {
      const t = Math.min(1, (now - start) / COUNT_UP_DURATION);
      element.dataset.text = format(target * (1 - (1 - t) ** 3));
      if (t < 1) frame = requestAnimationFrame(tick);
      else setCounting(false);
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- one count per reveal
  }, [counting]);

  const plan: MotionPlan = { enabled, trend: state.trend, epoch: state.epoch };
  const onExited = (key: string) => setState((current) => ({ ...current, glyphs: current.glyphs.filter((g) => g.key !== key || g.phase !== 'exiting') }));

  return (
    <span className={cn('numeric', counting && 'numeric-counting', className)}>
      <span className="sr-only">{text}</span>
      <span aria-hidden="true" className="numeric-glyphs">
        {state.glyphs.map((glyph) => (
          <GlyphView key={glyph.key} glyph={glyph} plan={plan} onExited={onExited} />
        ))}
      </span>
      {counting && <span ref={counter} aria-hidden="true" className="numeric-count" data-text={format(0)} />}
    </span>
  );
}

/** Whole counts with grouping ("1,204"), rounded so a count-up never shows fractions. */
export const formatCount = (value: number) => Math.round(value).toLocaleString();

/** True when the component using it first rendered while loading, so its figures may count up as they appear. */
export function useRevealedAfterLoading(loaded: boolean): boolean {
  const [wasLoading] = useState(!loaded);
  return wasLoading;
}
