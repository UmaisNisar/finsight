import { AnimatePresence, motion } from 'motion/react';
import { useEffect, useRef, useState, type ReactNode } from 'react';

const HEIGHT_SPRING = { type: 'spring', stiffness: 440, damping: 42 } as const;

/**
 * Animates its height to fit its content. Wrap card bodies whose content changes shape (loading to loaded,
 * idle to generating), so the card and everything below it glide to the new size instead of snapping.
 * Content is clipped only while the height is moving, so shadows of controls inside are never cut off at rest.
 */
export function AutoHeight({ children, className }: { children: ReactNode; className?: string }) {
  const inner = useRef<HTMLDivElement>(null);
  const [height, setHeight] = useState<number | 'auto'>('auto');
  const [animating, setAnimating] = useState(false);

  useEffect(() => {
    const element = inner.current;
    if (!element || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(([entry]) => setHeight(entry?.borderBoxSize[0]?.blockSize ?? element.offsetHeight));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  return (
    <motion.div
      className={className}
      style={{ overflow: animating ? 'clip' : 'visible' }}
      initial={false}
      animate={{ height }}
      transition={HEIGHT_SPRING}
      onAnimationStart={() => setAnimating(true)}
      onAnimationComplete={() => setAnimating(false)}
    >
      <div ref={inner}>{children}</div>
    </motion.div>
  );
}

/**
 * Expands and collapses a section with a height spring, clipping only during the motion. It deliberately doesn't
 * fade: opacity on a wrapper would flatten any glass card inside it for the length of the animation.
 * Use for disclosures and panels that appear inline (filters, form options).
 */
export function Collapse({ open, children, className, id }: { open: boolean; children: ReactNode; className?: string; id?: string }) {
  return (
    <AnimatePresence initial={false}>
      {open && (
        <motion.div
          key="collapse"
          id={id}
          className={className}
          initial={{ height: 0, overflow: 'clip' }}
          animate={{ height: 'auto', transitionEnd: { overflow: 'visible' } }}
          exit={{ height: 0, overflow: 'clip' }}
          transition={HEIGHT_SPRING}
        >
          {children}
        </motion.div>
      )}
    </AnimatePresence>
  );
}
