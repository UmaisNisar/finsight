import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { IconButton } from './Button';
import { SHOW_DELAY, Tooltip, TruncatedText } from './Tooltip';

/*
  jsdom doesn't apply popover styles, so a tag's visibility is read from its open state (data-open), which the
  component sets exactly when it calls showPopover or hidePopover.
*/

// jsdom treats [popover] elements as hidden, so tags are found with hidden: true.
const tag = () => screen.getByRole('tooltip', { hidden: true });
const isShown = () => screen.queryByRole('tooltip', { hidden: true })?.getAttribute('data-open') === 'true';
const hover = (element: HTMLElement, pointerType = 'mouse') => fireEvent.pointerEnter(element, { pointerType });

const originalMatchMedia = window.matchMedia;

beforeEach(() => {
  // Performance time is faked too, so the "recently closed, show the next at once" window elapses between tests.
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'performance'] });
  vi.advanceTimersByTime(5000);
});

afterEach(() => {
  vi.useRealTimers();
  window.matchMedia = originalMatchMedia;
});

function Help() {
  return (
    <Tooltip content="Gemini chose this category.">
      <button type="button">AI categorized</button>
    </Tooltip>
  );
}

describe('Tooltip', () => {
  it('describes its trigger and shows after a delay on hover, hiding when the pointer leaves', () => {
    render(<Help />);
    const button = screen.getByRole('button', { name: 'AI categorized' });
    expect(button).toHaveAccessibleDescription('Gemini chose this category.');

    act(() => hover(button));
    act(() => vi.advanceTimersByTime(SHOW_DELAY - 50));
    expect(isShown()).toBe(false);
    act(() => vi.advanceTimersByTime(50));
    expect(isShown()).toBe(true);
    expect(tag()).toHaveTextContent('Gemini chose this category.');

    act(() => fireEvent.pointerLeave(button, { pointerType: 'mouse' }));
    expect(isShown()).toBe(false);
  });

  it('shows on keyboard focus and hides on Escape', () => {
    render(<Help />);
    const button = screen.getByRole('button', { name: 'AI categorized' });
    act(() => {
      fireEvent.keyDown(document.body, { key: 'Tab' });
      button.focus();
    });
    act(() => vi.advanceTimersByTime(SHOW_DELAY));
    expect(isShown()).toBe(true);

    act(() => fireEvent.keyDown(document, { key: 'Escape' }));
    expect(isShown()).toBe(false);
  });

  it('doesn’t leave a tag behind when a mouse click focuses the button', () => {
    render(<Help />);
    const button = screen.getByRole('button', { name: 'AI categorized' });
    act(() => {
      fireEvent.pointerDown(button, { pointerType: 'mouse' });
      button.focus();
    });
    act(() => vi.advanceTimersByTime(SHOW_DELAY * 2));
    expect(isShown()).toBe(false);
  });

  it('never shows for touch', () => {
    render(<Help />);
    const button = screen.getByRole('button', { name: 'AI categorized' });
    act(() => hover(button, 'touch'));
    act(() => vi.advanceTimersByTime(SHOW_DELAY * 2));
    expect(isShown()).toBe(false);

    // Nor on devices that can't hover at all, whatever the event says.
    window.matchMedia = ((query: string) => ({ matches: query === '(hover: none)', media: query, addEventListener: () => undefined, removeEventListener: () => undefined })) as unknown as typeof window.matchMedia;
    act(() => hover(button));
    act(() => vi.advanceTimersByTime(SHOW_DELAY * 2));
    expect(isShown()).toBe(false);
  });

  it('shows the next tag at once while moving between items', () => {
    render(
      <>
        <IconButton label="Close">x</IconButton>
        <IconButton label="Hide key">o</IconButton>
      </>,
    );
    const close = screen.getByRole('button', { name: 'Close' });
    const hide = screen.getByRole('button', { name: 'Hide key' });
    act(() => hover(close));
    act(() => vi.advanceTimersByTime(SHOW_DELAY));
    act(() => fireEvent.pointerLeave(close, { pointerType: 'mouse' }));
    act(() => hover(hide));
    expect(screen.getAllByRole('tooltip', { hidden: true }).find((t) => t.dataset.open === 'true')).toHaveTextContent('Hide key');
    // An icon button's tag repeats its name, so it isn't added as a description.
    expect(hide).not.toHaveAttribute('aria-describedby');
  });

  it('shows truncated text in full only when it is actually truncated', () => {
    render(<TruncatedText>Amazon Marketplace Toronto ON</TruncatedText>);
    const text = screen.getByText('Amazon Marketplace Toronto ON');
    const size = (scroll: number, client: number) => {
      Object.defineProperty(text, 'scrollWidth', { configurable: true, value: scroll });
      Object.defineProperty(text, 'clientWidth', { configurable: true, value: client });
    };

    size(120, 120);
    act(() => hover(text));
    act(() => vi.advanceTimersByTime(SHOW_DELAY * 2));
    expect(isShown()).toBe(false);
    act(() => fireEvent.pointerLeave(text, { pointerType: 'mouse' }));

    size(260, 120);
    act(() => vi.advanceTimersByTime(1000));
    act(() => hover(text));
    act(() => vi.advanceTimersByTime(SHOW_DELAY));
    expect(isShown()).toBe(true);
    expect(tag()).toHaveTextContent('Amazon Marketplace Toronto ON');
    // The text itself is already read in full, so it isn't repeated as a description.
    expect(text).not.toHaveAttribute('aria-describedby');
  });

  it('explains a disabled button by hovering its wrapper', () => {
    render(
      <Tooltip wrap content="Available when the current import finishes">
        <button type="button" disabled>
          Analyze
        </button>
      </Tooltip>,
    );
    const button = screen.getByRole('button', { name: 'Analyze' });
    expect(button).toHaveAccessibleDescription('Available when the current import finishes');
    act(() => hover(button.parentElement as HTMLElement));
    act(() => vi.advanceTimersByTime(SHOW_DELAY));
    expect(isShown()).toBe(true);
  });
});
