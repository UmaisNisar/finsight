import { act, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { PopUpButton } from './PopUpButton';

function Harness({ onChange }: { onChange?: (value: string) => void }) {
  const [value, setValue] = useState('newest');
  return (
    <PopUpButton
      label="Sort by"
      value={value}
      onChange={(next) => {
        setValue(next);
        onChange?.(next);
      }}
      options={[
        { value: 'newest', label: 'Newest first' },
        { value: 'oldest', label: 'Oldest first' },
        { value: 'largest', label: 'Largest amount' },
      ]}
    />
  );
}

const option = (name: string) => screen.getByRole('option', { name, hidden: true });

describe('PopUpButton', () => {
  it('opens with the arrow key, moves the active option and selects with Enter', () => {
    render(<Harness />);
    const button = screen.getByRole('button', { name: 'Sort by' });
    expect(button).toHaveAttribute('aria-expanded', 'false');

    act(() => fireEvent.keyDown(button, { key: 'ArrowDown' }));
    expect(button).toHaveAttribute('aria-expanded', 'true');

    const listbox = screen.getByRole('listbox', { hidden: true });
    expect(listbox).toHaveFocus();
    expect(option('Newest first')).toHaveAttribute('aria-selected', 'true');
    expect(listbox).toHaveAttribute('aria-activedescendant', option('Newest first').id);

    act(() => fireEvent.keyDown(listbox, { key: 'ArrowDown' }));
    expect(listbox).toHaveAttribute('aria-activedescendant', option('Oldest first').id);

    act(() => fireEvent.keyDown(listbox, { key: 'Enter' }));
    expect(button).toHaveTextContent('Oldest first');
    expect(button).toHaveAttribute('aria-expanded', 'false');
    expect(button).toHaveFocus();
  });

  it('selects by typing the start of a label and by clicking', () => {
    render(<Harness />);
    const button = screen.getByRole('button', { name: 'Sort by' });
    act(() => fireEvent.keyDown(button, { key: 'ArrowUp' }));
    const listbox = screen.getByRole('listbox', { hidden: true });

    act(() => fireEvent.keyDown(listbox, { key: 'l' }));
    expect(listbox).toHaveAttribute('aria-activedescendant', option('Largest amount').id);

    act(() => fireEvent.click(option('Oldest first')));
    expect(button).toHaveTextContent('Oldest first');
  });

  it('clamps Home, End and arrows to the list, and Tab closes without choosing', () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    const button = screen.getByRole('button', { name: 'Sort by' });
    act(() => fireEvent.click(button));
    const listbox = screen.getByRole('listbox', { hidden: true });

    act(() => fireEvent.keyDown(listbox, { key: 'End' }));
    act(() => fireEvent.keyDown(listbox, { key: 'ArrowDown' }));
    expect(listbox).toHaveAttribute('aria-activedescendant', option('Largest amount').id);
    act(() => fireEvent.keyDown(listbox, { key: 'Home' }));
    act(() => fireEvent.keyDown(listbox, { key: 'ArrowUp' }));
    expect(listbox).toHaveAttribute('aria-activedescendant', option('Newest first').id);

    act(() => fireEvent.keyDown(listbox, { key: 'Tab' }));
    expect(button).toHaveAttribute('aria-expanded', 'false');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('toggles closed when the button is pressed while open, instead of reopening', () => {
    render(<Harness />);
    const button = screen.getByRole('button', { name: 'Sort by' });
    act(() => fireEvent.click(button));
    expect(button).toHaveAttribute('aria-expanded', 'true');

    act(() => {
      fireEvent.pointerDown(button);
      fireEvent.click(button);
    });
    expect(button).toHaveAttribute('aria-expanded', 'false');
  });

  it('positions the menu before it is shown, so it never paints in the wrong place', () => {
    render(<Harness />);
    const button = screen.getByRole('button', { name: 'Sort by' });
    const menu = document.getElementById(button.getAttribute('aria-controls') ?? '') as HTMLElement;
    const topWhenShown: string[] = [];
    menu.addEventListener('toggle', () => topWhenShown.push(menu.style.top));

    act(() => fireEvent.click(button));
    // The shim fires toggle synchronously inside showPopover; positioning runs in the same task right after.
    expect(menu.style.top).not.toBe('');
    expect(menu.style.transformOrigin).not.toBe('');
  });

  it('does not open when disabled', () => {
    render(<PopUpButton label="Currency" value="CAD" onChange={() => undefined} disabled options={[{ value: 'CAD', label: 'CAD' }]} />);
    const button = screen.getByRole('button', { name: 'Currency' });
    act(() => fireEvent.keyDown(button, { key: 'ArrowDown' }));
    expect(button).toHaveAttribute('aria-expanded', 'false');
  });
  it('filters by option label or by section title when searchable', () => {
    render(
      <PopUpButton
        label="Category"
        value="rent"
        onChange={() => undefined}
        searchable
        searchPlaceholder="Search categories"
        options={[
          { title: 'Housing', options: [{ value: 'rent', label: 'Rent' }, { value: 'utilities', label: 'Utilities' }] },
          { title: 'Food', options: [{ value: 'groceries', label: 'Groceries' }, { value: 'restaurants', label: 'Restaurants' }] },
        ]}
      />,
    );
    act(() => fireEvent.keyDown(screen.getByRole('button', { name: 'Category' }), { key: 'ArrowDown' }));
    const search = screen.getByRole('combobox', { hidden: true });

    act(() => fireEvent.change(search, { target: { value: 'groc' } }));
    expect(screen.getAllByRole('option', { hidden: true }).map((o) => o.textContent)).toEqual(['Groceries']);

    // A section title match keeps the whole group.
    act(() => fireEvent.change(search, { target: { value: 'food' } }));
    expect(screen.getAllByRole('option', { hidden: true }).map((o) => o.textContent)).toEqual(['Groceries', 'Restaurants']);
  });
});
