import {
  cloneElement,
  isValidElement,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type FocusEvent,
  type PointerEvent,
  type ReactElement,
  type ReactNode,
  type Ref,
} from 'react';
import { createPortal } from 'react-dom';
import { cn } from '@/lib/cn';

/*
  An AppKit-style help tag. It waits half a second on first hover, then while one is showing (or just closed) moving
  to the next item shows the next at once, as macOS does across a toolbar. It appears on keyboard focus too, but never
  for touch, and hides on Escape, scroll, press and leave.

  The tag is a manual popover, so it renders in the top layer: never clipped by a card or scroller, and above an open
  sheet. It lives in the nearest <dialog> when there is one (so it isn't in the inert part of the page) or the body.
*/

export const SHOW_DELAY = 500;
/** How long after a tag closes the next one still appears immediately. */
const WARM_WINDOW = 400;
const GAP = 8;
const EDGE = 8;

const warmth = { open: 0, closedAt: -Infinity };
const isWarm = () => warmth.open > 0 || performance.now() - warmth.closedAt < WARM_WINDOW;

// Focus tags are for keyboard users: a mouse click also focuses a button, and shouldn't leave a tag behind.
let keyboardModality = true;
let modalityTracked = false;
function trackModality() {
  if (modalityTracked || typeof document === 'undefined') return;
  modalityTracked = true;
  document.addEventListener('keydown', () => (keyboardModality = true), true);
  document.addEventListener('pointerdown', () => (keyboardModality = false), true);
}

/** Devices whose primary pointer can't hover (phones, tablets) never get tags. */
function hoverUnavailable(): boolean {
  return window.matchMedia?.('(hover: none)').matches ?? false;
}

function isTruncated(element: HTMLElement): boolean {
  return element.scrollWidth > element.clientWidth + 0.5;
}

type Placement = 'below' | 'over';

/** Below the trigger, above it when there isn't room; `over` lays the full text exactly over truncated text, like Finder. */
function position(tip: HTMLElement, trigger: HTMLElement, placement: Placement) {
  const rect = trigger.getBoundingClientRect();
  if (placement === 'over') {
    const style = getComputedStyle(trigger);
    tip.style.font = style.font;
    tip.style.letterSpacing = style.letterSpacing;
  }
  const width = tip.offsetWidth;
  const height = tip.offsetHeight;
  let left: number;
  let top: number;
  let side: 'above' | 'below' | 'over' = 'below';
  if (placement === 'over') {
    const paddingLeft = parseFloat(getComputedStyle(tip).paddingLeft) || 0;
    left = rect.left - paddingLeft;
    top = rect.top + (rect.height - height) / 2;
    side = 'over';
  } else {
    left = rect.left + rect.width / 2 - width / 2;
    top = rect.bottom + GAP;
    if (top + height > window.innerHeight - EDGE && rect.top - GAP - height >= EDGE) {
      top = rect.top - GAP - height;
      side = 'above';
    }
  }
  tip.style.left = `${Math.round(Math.min(Math.max(EDGE, left), window.innerWidth - width - EDGE))}px`;
  tip.style.top = `${Math.round(Math.min(Math.max(EDGE, top), window.innerHeight - height - EDGE))}px`;
  tip.dataset.side = side;
}

type TriggerProps = {
  ref?: Ref<HTMLElement>;
  'aria-describedby'?: string;
  onPointerEnter?: (event: PointerEvent<HTMLElement>) => void;
  onPointerLeave?: (event: PointerEvent<HTMLElement>) => void;
  onPointerDown?: (event: PointerEvent<HTMLElement>) => void;
  onFocus?: (event: FocusEvent<HTMLElement>) => void;
  onBlur?: (event: FocusEvent<HTMLElement>) => void;
};

export interface TooltipProps {
  /** What the tag says. Keep it short: a phrase or a sentence or two. */
  content: ReactNode;
  /** One element that accepts a ref and pointer/focus handlers: a DOM element, Button or IconButton. */
  children: ReactElement;
  /**
   * Adds the tag to the trigger's accessible description. Turn off when it repeats the accessible name (an icon
   * button's label) or visible text (a truncated title), so screen readers don't hear it twice.
   */
  describe?: boolean;
  /** Only shows when the trigger's text is cut off by an ellipsis, and then lays the full text over it. */
  onlyWhenTruncated?: boolean;
  /** Checked as the pointer or focus arrives; return false to skip (a label that's visible at this width). */
  when?: (trigger: HTMLElement) => boolean;
  /**
   * Hover a wrapper instead of the child. Needed for disabled buttons, which receive no pointer events; the child
   * still carries the description.
   */
  wrap?: boolean;
  disabled?: boolean;
  className?: string;
}

function assignRef<T>(ref: Ref<T> | undefined, value: T | null) {
  if (typeof ref === 'function') ref(value);
  else if (ref) (ref as { current: T | null }).current = value;
}

export function Tooltip({ content, children, describe = true, onlyWhenTruncated = false, when, wrap = false, disabled = false, className }: TooltipProps) {
  const id = useId();
  const trigger = useRef<HTMLElement | null>(null);
  const tip = useRef<HTMLDivElement>(null);
  const timer = useRef(0);
  const [open, setOpen] = useState(false);
  // Where the tag renders. Described tags mount straight away so the description exists before first focus;
  // the rest (truncated text in long lists) wait until someone points at them.
  const [container, setContainer] = useState<HTMLElement | null>(null);
  const inactive = disabled || content === null || content === undefined || content === '';
  const describes = describe && !onlyWhenTruncated && !inactive;
  const placement: Placement = onlyWhenTruncated ? 'over' : 'below';

  const resolveContainer = () => {
    const element = trigger.current;
    if (!element || container) return;
    setContainer((element.closest('dialog') as HTMLElement | null) ?? document.body);
  };

  useLayoutEffect(() => {
    trackModality();
    if (describes) resolveContainer();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once a container is chosen it stays
  }, [describes]);

  function cancel() {
    window.clearTimeout(timer.current);
  }

  function request() {
    const element = trigger.current;
    if (inactive || !element || hoverUnavailable()) return;
    if (onlyWhenTruncated && !isTruncated(element)) return;
    if (when && !when(element)) return;
    resolveContainer();
    cancel();
    if (isWarm()) setOpen(true);
    else timer.current = window.setTimeout(() => setOpen(true), SHOW_DELAY);
  }

  function dismiss() {
    cancel();
    setOpen(false);
  }

  // Show or hide the popover to match, positioning it in the same task so its first frame is in place.
  useLayoutEffect(() => {
    const element = tip.current;
    const anchor = trigger.current;
    if (!element || !anchor) return;
    const showing = element.dataset.open === 'true';
    if (open && !inactive && !showing) {
      element.showPopover?.();
      element.dataset.open = 'true';
      position(element, anchor, placement);
      warmth.open++;
    } else if ((!open || inactive) && showing) {
      element.hidePopover?.();
      element.dataset.open = 'false';
      warmth.open--;
      warmth.closedAt = performance.now();
    }
  }, [open, inactive, container, placement]);

  // While shown: Escape, scrolling anywhere or resizing hides it. Unmounting mid-show settles the warm count.
  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') dismiss();
    };
    const onScroll = () => dismiss();
    document.addEventListener('keydown', onKey, true);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onScroll);
    const element = tip.current;
    return () => {
      document.removeEventListener('keydown', onKey, true);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onScroll);
      if (element?.dataset.open === 'true' && !element.isConnected) {
        warmth.open--;
        warmth.closedAt = performance.now();
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- dismiss only touches a ref and a state setter
  }, [open]);

  useEffect(() => cancel, []);

  if (!isValidElement<TriggerProps>(children)) return children;

  const childProps = children.props;
  const handlers = {
    onPointerEnter: (event: PointerEvent<HTMLElement>) => {
      if (!wrap) childProps.onPointerEnter?.(event);
      if (event.pointerType !== 'touch') request();
    },
    onPointerLeave: (event: PointerEvent<HTMLElement>) => {
      if (!wrap) childProps.onPointerLeave?.(event);
      dismiss();
    },
    // Pressing hides the tag (and a touch press cancels any pending one), as clicking does on the Mac.
    onPointerDown: (event: PointerEvent<HTMLElement>) => {
      if (!wrap) childProps.onPointerDown?.(event);
      dismiss();
    },
  };
  const focusHandlers = {
    onFocus: (event: FocusEvent<HTMLElement>) => {
      childProps.onFocus?.(event);
      if (keyboardModality) request();
    },
    onBlur: (event: FocusEvent<HTMLElement>) => {
      childProps.onBlur?.(event);
      dismiss();
    },
  };

  const describedBy = describes ? cn(childProps['aria-describedby'], id) : childProps['aria-describedby'];
  const childRef = childProps.ref;
  // The trigger ref is only written from the ref callback, never read during render.
  // eslint-disable-next-line react-hooks/refs
  const child = cloneElement(children, {
    ...(wrap ? {} : handlers),
    ...focusHandlers,
    'aria-describedby': describedBy || undefined,
    ref: (node: HTMLElement | null) => {
      if (!wrap) trigger.current = node;
      assignRef(childRef, node);
    },
  });

  const tag =
    container &&
    createPortal(
      <div ref={tip} id={id} role="tooltip" popover="manual" className={cn('tooltip', placement === 'over' && 'tooltip-over', className)}>
        {content}
      </div>,
      container,
    );

  if (wrap) {
    return (
      <span
        ref={(node) => {
          trigger.current = node;
        }}
        // A disabled control lets the pointer through to the wrapper, which Chrome and Safari otherwise don't.
        className="inline-flex [&>:disabled]:pointer-events-none"
        {...handlers}
      >
        {child}
        {tag}
      </span>
    );
  }

  return (
    <>
      {child}
      {tag}
    </>
  );
}

/** Text cut off with an ellipsis that shows its full self on hover, only when it is actually cut off. */
export function TruncatedText({ children, className, as: Tag = 'span' }: { children: string; className?: string; as?: 'span' | 'p' | 'h2' | 'h3' }) {
  return (
    <Tooltip content={children} onlyWhenTruncated>
      <Tag className={cn('truncate', className)}>{children}</Tag>
    </Tooltip>
  );
}
