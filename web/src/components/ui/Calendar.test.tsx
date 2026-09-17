import { act, fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Calendar } from './Calendar';

const day = (iso: string) => document.querySelector<HTMLButtonElement>(`[data-date="${iso}"]`);

function setup(props: Partial<Parameters<typeof Calendar>[0]> = {}) {
  const onChange = vi.fn();
  render(<Calendar label="Start date" value="2026-09-16" today="2026-09-16" onChange={onChange} {...props} />);
  return onChange;
}

describe('Calendar', () => {
  it('has one tab stop, on the selected day', () => {
    setup();
    const tabbable = [...document.querySelectorAll('[data-date]')].filter((el) => el.getAttribute('tabindex') === '0');
    expect(tabbable).toHaveLength(1);
    expect(tabbable[0]).toHaveAttribute('data-date', '2026-09-16');
    expect(day('2026-09-16')).toHaveAttribute('aria-current', 'date');
  });

  it('moves by day, week, week edge and month with the keyboard', () => {
    setup();
    const press = (key: string) => act(() => fireEvent.keyDown(document.activeElement as Element, { key }));
    act(() => day('2026-09-16')?.focus());

    press('ArrowRight');
    expect(document.activeElement).toBe(day('2026-09-17'));
    press('ArrowDown');
    expect(document.activeElement).toBe(day('2026-09-24'));
    press('Home'); // Sunday of that week
    expect(document.activeElement).toBe(day('2026-09-20'));
    press('End');
    expect(document.activeElement).toBe(day('2026-09-26'));
    press('PageDown'); // Same day next month, across the month change
    expect(document.activeElement).toBe(day('2026-10-26'));
    expect(screen.getByRole('grid')).toHaveAccessibleName(/October 2026/);
    press('ArrowUp');
    press('ArrowUp');
    press('ArrowUp');
    press('ArrowUp');
    expect(document.activeElement).toBe(day('2026-09-28'));
  });

  it('keeps out-of-range days focusable but not selectable', () => {
    const onChange = setup({ min: '2026-09-10', max: '2026-09-20' });
    const tooLate = day('2026-09-21');
    expect(tooLate).toHaveAttribute('aria-disabled', 'true');
    act(() => fireEvent.click(tooLate as HTMLButtonElement));
    expect(onChange).not.toHaveBeenCalled();

    act(() => day('2026-09-20')?.focus());
    act(() => fireEvent.keyDown(document.activeElement as Element, { key: 'ArrowRight' }));
    // Focus lands on the disabled day instead of falling off the grid.
    expect(document.activeElement).toBe(day('2026-09-21'));

    act(() => fireEvent.click(day('2026-09-12') as HTMLButtonElement));
    expect(onChange).toHaveBeenCalledWith('2026-09-12');
  });

  it('disables month navigation beyond min and max, and starts inside the range', () => {
    setup({ value: '', min: '2026-07-01', max: '2026-07-31' });
    expect(screen.getByRole('grid')).toHaveAccessibleName(/July 2026/);
    expect(screen.getByRole('button', { name: 'Previous month' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next month' })).toBeDisabled();
  });
});
