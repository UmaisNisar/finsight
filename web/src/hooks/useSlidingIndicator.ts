import { useLayoutEffect, useRef } from 'react';

/**
 * Slides one persistent selection lens to the selected item inside a container (nav list, segmented control).
 * Positions come from the item's offset within the container, so the lens only ever moves between neighbouring
 * items: page scroll, fixed positioning and route changes can't throw it across the screen, as a shared-layout
 * animation measured in page coordinates could. It is placed without animation on first render and on resize;
 * CSS (`.sliding-lens`) animates selection changes and honours reduced motion.
 *
 * `selectedKey` should change whenever the selection does; `selector` finds the selected item. Attach the returned
 * `container` ref to the element the items are positioned in, and `lens` to an absolutely positioned child.
 */
export function useSlidingIndicator<C extends HTMLElement, L extends HTMLElement>(selector: string, selectedKey: unknown) {
  const container = useRef<C>(null);
  const lens = useRef<L>(null);

  useLayoutEffect(() => {
    const list = container.current;
    const indicator = lens.current;
    if (!list || !indicator) return;

    const place = (animate: boolean) => {
      const selected = list.querySelector<HTMLElement>(selector);
      if (!selected) {
        indicator.style.opacity = '0';
        return;
      }
      indicator.dataset.animate = String(animate);
      indicator.style.opacity = '1';
      indicator.style.width = `${selected.offsetWidth}px`;
      indicator.style.height = `${selected.offsetHeight}px`;
      indicator.style.transform = `translate(${selected.offsetLeft}px, ${selected.offsetTop}px)`;
    };

    // Animate only when moving from a position already shown.
    place(indicator.dataset.placed === 'true' && indicator.style.opacity === '1');
    indicator.dataset.placed = 'true';

    if (typeof ResizeObserver === 'undefined') return;
    // The observer reports once on observe; skip that, or it would cut the slide that just started.
    let initial = true;
    const observer = new ResizeObserver(() => {
      if (initial) initial = false;
      else place(false);
    });
    observer.observe(list);
    return () => observer.disconnect();
  }, [selector, selectedKey]);

  return { container, lens };
}
