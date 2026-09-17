import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { formatMoney, formatPercent } from '@/lib/format';
import { AnimatedNumber, splitGlyphs } from './AnimatedNumber';

/*
  jsdom has no Web Animations API, so by default AnimatedNumber renders without motion. These tests install a fake
  `animate` to exercise the animated path: they record what would move and let a test finish animations on demand.
*/

interface FakeAnimation {
  keyframes: Keyframe[];
  options: KeyframeAnimationOptions;
  element: Element;
  playState: AnimationPlayState;
  onfinish: (() => void) | null;
  cancel: () => void;
  effect: { getComputedTiming: () => { progress: number } };
}

let animations: FakeAnimation[] = [];

function installAnimate() {
  animations = [];
  HTMLElement.prototype.animate = vi.fn(function (this: HTMLElement, keyframes: Keyframe[], options: KeyframeAnimationOptions) {
    const animation: FakeAnimation = {
      keyframes,
      options,
      element: this,
      playState: 'running',
      onfinish: null,
      cancel() {
        this.playState = 'idle';
      },
      effect: { getComputedTiming: () => ({ progress: 0 }) },
    };
    animations.push(animation);
    return animation as unknown as Animation;
  }) as unknown as HTMLElement['animate'];
}

function finishAll() {
  act(() => {
    for (const animation of animations.filter((a) => a.playState === 'running')) {
      animation.playState = 'finished';
      animation.onfinish?.();
    }
  });
}

function setReducedMotion(reduce: boolean) {
  window.matchMedia = ((query: string) => ({
    matches: reduce && query.includes('prefers-reduced-motion'),
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
}

/** What the glyphs draw, left to right: digits show their pair's top line at rest, symbols their own character. */
function drawn(container: HTMLElement, { includeLeaving = false } = {}): string {
  return [...container.querySelectorAll<HTMLElement>('.numeric-glyph')]
    .filter((glyph) => includeLeaving || glyph.dataset.phase !== 'exiting')
    .map((glyph) => glyph.querySelector<HTMLElement>('.numeric-pair')?.dataset.a ?? glyph.dataset.char)
    .join('');
}

const money = (value: number) => formatMoney(value, 'USD', { whole: true });
const cents = (value: number) => formatMoney(value, 'USD');
const originalMatchMedia = window.matchMedia;

beforeEach(() => setReducedMotion(false));
afterEach(() => {
  delete (HTMLElement.prototype as Partial<HTMLElement>).animate;
  window.matchMedia = originalMatchMedia;
  vi.restoreAllMocks();
});

describe('AnimatedNumber', () => {
  it('exposes only the final formatted value to assistive technology', () => {
    installAnimate();
    const { container, rerender } = render(<AnimatedNumber value={1543.29} format={money} />);
    expect(container).toHaveTextContent(/^\$1,543$/);
    expect(screen.getByText('$1,543')).toHaveClass('sr-only');
    expect(container.querySelector('.numeric-glyph')?.closest('[aria-hidden="true"]')).not.toBeNull();

    rerender(<AnimatedNumber value={17614.5} format={money} />);
    // Mid-animation, the text is already the final value, once: no frames, no old value.
    expect(container).toHaveTextContent(/^\$17,615$/);
    expect(screen.queryByText('$1,543')).not.toBeInTheDocument();
  });

  it('lands exactly on the final value, including added and removed digits', () => {
    installAnimate();
    const { container, rerender } = render(<AnimatedNumber value={987} format={money} />);
    expect(drawn(container)).toBe('$987');

    rerender(<AnimatedNumber value={1024} format={money} />);
    expect(animations.length).toBeGreaterThan(0);
    finishAll();
    expect(drawn(container, { includeLeaving: true })).toBe('$1,024');

    rerender(<AnimatedNumber value={42} format={money} />);
    // The thousands and hundreds digits and the separator shrink away, then are removed.
    expect(container.querySelectorAll('[data-phase="exiting"]')).toHaveLength(3);
    finishAll();
    expect(container.querySelectorAll('[data-phase="exiting"]')).toHaveLength(0);
    expect(drawn(container, { includeLeaving: true })).toBe('$42');
  });

  it('ticks digits in the direction of the change', () => {
    installAnimate();
    const { rerender } = render(<AnimatedNumber value={40} format={money} />);
    rerender(<AnimatedNumber value={50} format={money} />);
    const up = animations.find((a) => a.options.pseudoElement === '::before');
    // Rising: the new digit comes up from below.
    expect(up?.keyframes[0]?.transform).toBe('translateY(0.25em)');
    expect(up?.keyframes.at(-1)?.transform).toBe('none');

    finishAll();
    animations = [];
    rerender(<AnimatedNumber value={30} format={money} />);
    const down = animations.find((a) => a.options.pseudoElement === '::before');
    expect(down?.keyframes[0]?.transform).toBe('translateY(-0.25em)');
  });

  it('moves every changed glyph in unison, across figures, and leaves unchanged digits still', () => {
    installAnimate();
    const { container, rerender } = render(
      <>
        <AnimatedNumber value={6017} format={money} />
        <AnimatedNumber value={4474} format={money} />
      </>,
    );
    rerender(
      <>
        <AnimatedNumber value={17614} format={money} />
        <AnimatedNumber value={4480} format={money} />
      </>,
    );
    // $6,017 → $17,614: the thousands, hundreds and ones digits change, a ten-thousands digit arrives, the tens stay.
    // $4,474 → $4,480: the tens and ones change; the other digits stay.
    const incoming = animations.filter((a) => a.options.pseudoElement === '::before' || (a.element instanceof HTMLElement && a.element.dataset.phase === 'entering' && a.keyframes.some((k) => 'opacity' in k)));
    expect(incoming).toHaveLength(6);
    const timing = (a: FakeAnimation) => JSON.stringify({ duration: a.options.duration, delay: a.options.delay ?? 0, easing: a.options.easing, keyframes: a.keyframes });
    expect(new Set(incoming.map(timing)).size).toBe(1);
    // Every animation of the change, in or out, width included, shares one duration, delay and curve.
    expect(new Set(animations.map((a) => `${a.options.duration}/${a.options.delay ?? 0}/${a.options.easing}`)).size).toBe(1);

    const moving = new Set(animations.map((a) => (a.element as HTMLElement).closest('.numeric-glyph')));
    const [first, second] = container.querySelectorAll<HTMLElement>('.numeric');
    const still = (numeric: HTMLElement | undefined) => [...(numeric?.querySelectorAll<HTMLElement>('.numeric-digit') ?? [])].filter((g) => !moving.has(g)).map((g) => g.querySelector<HTMLElement>('.numeric-pair')?.dataset.a);
    expect(still(first)).toEqual(['1']);
    expect(still(second)).toEqual(['4', '4']);
  });

  it('does not animate when re-rendered with the same value, or a value that formats the same', () => {
    installAnimate();
    const { container, rerender } = render(<AnimatedNumber value={1543} format={money} />);
    rerender(<AnimatedNumber value={1543} format={money} />);
    rerender(<AnimatedNumber value={1543.2} format={money} />);
    expect(HTMLElement.prototype.animate).not.toHaveBeenCalled();
    expect(drawn(container)).toBe('$1,543');
  });

  it('shows the new value at once with reduced motion', () => {
    installAnimate();
    setReducedMotion(true);
    const { container, rerender } = render(<AnimatedNumber value={987} format={money} countUp />);
    expect(container.querySelector('.numeric-count')).toBeNull();

    rerender(<AnimatedNumber value={1024} format={money} />);
    rerender(<AnimatedNumber value={8} format={money} />);
    expect(HTMLElement.prototype.animate).not.toHaveBeenCalled();
    expect(container.querySelectorAll('[data-phase="exiting"], [data-phase="entering"]')).toHaveLength(0);
    expect(drawn(container, { includeLeaving: true })).toBe('$8');
  });

  it('never draws an unformatted intermediate', () => {
    installAnimate();
    const { container, rerender } = render(<AnimatedNumber value={1543.2891} format={cents} />);
    rerender(<AnimatedNumber value={20.5} format={cents} />);
    // Mid-animation every glyph, leaving ones included, belongs to one of the two formatted strings.
    for (const glyph of container.querySelectorAll<HTMLElement>('.numeric-glyph')) expect('$1,543.29$20.50').toContain(glyph.dataset.char ?? '?');
    expect(drawn(container)).toBe('$20.50');
    for (const pair of container.querySelectorAll<HTMLElement>('.numeric-pair')) {
      expect(pair.dataset.a).toMatch(/^\d$/);
      expect(pair.dataset.b).toMatch(/^\d?$/);
    }
  });

  it('counts up on reveal through formatted values only, then hands over to the glyphs', () => {
    installAnimate();
    const frames: FrameRequestCallback[] = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((callback) => frames.push(callback));
    vi.spyOn(performance, 'now').mockReturnValue(0);
    const { container } = render(<AnimatedNumber value={25.6} format={formatPercent} countUp />);
    const counter = container.querySelector<HTMLElement>('.numeric-count');
    expect(counter).not.toBeNull();

    const seen: string[] = [];
    for (let time = 16; frames.length > 0; time += 16) {
      act(() => frames.shift()?.(time));
      if (counter?.isConnected) seen.push(counter.dataset.text ?? '');
    }
    expect(seen.length).toBeGreaterThan(10);
    for (const text of seen) expect(text).toMatch(/^\d{1,2}(\.\d)?%$/);
    expect(seen.at(-1)).toBe('25.6%');
    expect(container.querySelector('.numeric-count')).toBeNull();
    expect(drawn(container)).toBe('25.6%');
  });
});

describe('splitGlyphs', () => {
  it('keys digits by place value, so columns survive a change in length', () => {
    const before = splitGlyphs('$987').map((g) => g.key);
    const after = splitGlyphs('$1,024').map((g) => g.key);
    expect(before).toEqual(['p0:$', 'i2', 'i1', 'i0']);
    expect(after).toEqual(['p0:$', 'i3', 'g3:,', 'i2', 'i1', 'i0']);
    expect(splitGlyphs('−$12.50').map((g) => g.key)).toEqual(['p1:−', 'p0:$', 'i1', 'i0', 'd:.', 'f0', 'f1']);
    expect(splitGlyphs('25%').map((g) => g.key)).toEqual(['i1', 'i0', 'x0:%']);
  });
});
