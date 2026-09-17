import { act, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ConfirmProvider, useConfirm, type ConfirmOptions } from '@/app/providers/ConfirmProvider';
import { SegmentedControl } from './SegmentedControl';
import { Sheet } from './Sheet';

describe('SegmentedControl', () => {
  function Harness() {
    const [value, setValue] = useState<'a' | 'b' | 'c'>('a');
    return (
      <SegmentedControl
        label="View"
        value={value}
        onChange={setValue}
        options={[
          { value: 'a', label: 'All' },
          { value: 'b', label: 'Spending' },
          { value: 'c', label: 'Income' },
        ]}
      />
    );
  }

  it('is a radio group with a single tab stop that follows the selection', () => {
    render(<Harness />);
    expect(screen.getByRole('radiogroup', { name: 'View' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'All' })).toHaveAttribute('tabindex', '0');
    expect(screen.getByRole('radio', { name: 'Spending' })).toHaveAttribute('tabindex', '-1');
  });

  it('selects and focuses with arrow keys, wrapping at the ends', () => {
    render(<Harness />);
    const all = screen.getByRole('radio', { name: 'All' });
    act(() => all.focus());

    act(() => fireEvent.keyDown(all, { key: 'ArrowRight' }));
    expect(screen.getByRole('radio', { name: 'Spending' })).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByRole('radio', { name: 'Spending' })).toHaveFocus();

    act(() => fireEvent.keyDown(document.activeElement as Element, { key: 'ArrowLeft' }));
    act(() => fireEvent.keyDown(document.activeElement as Element, { key: 'ArrowLeft' }));
    expect(screen.getByRole('radio', { name: 'Income' })).toHaveAttribute('aria-checked', 'true');

    act(() => fireEvent.click(screen.getByRole('radio', { name: 'All' })));
    expect(screen.getByRole('radio', { name: 'All' })).toHaveAttribute('aria-checked', 'true');
  });
});

describe('Sheet', () => {
  function Harness({ onClose }: { onClose?: () => void }) {
    const [open, setOpen] = useState(false);
    return (
      <>
        <button type="button" onClick={() => setOpen(true)}>
          Open
        </button>
        <Sheet
          open={open}
          title="Edit transaction"
          onClose={() => {
            onClose?.();
            setOpen(false);
          }}
        >
          <p>Sheet body</p>
        </Sheet>
      </>
    );
  }

  const dialog = () => document.querySelector('dialog') as HTMLDialogElement;

  it('opens as a labelled modal and closes from the Close button', () => {
    render(<Harness />);
    expect(dialog().open).toBe(false);

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Open' })));
    expect(dialog().open).toBe(true);
    expect(dialog()).toHaveAccessibleName('Edit transaction');

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Close', hidden: true })));
    expect(dialog().open).toBe(false);
    // Content stays rendered so it can animate out.
    expect(screen.getByText('Sheet body', { ignore: false })).toBeInTheDocument();
  });

  it('closes on Escape (cancel) and on a backdrop click, once each', () => {
    const onClose = vi.fn();
    render(<Harness onClose={onClose} />);

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Open' })));
    act(() => {
      dialog().dispatchEvent(new Event('cancel', { cancelable: true }));
    });
    expect(dialog().open).toBe(false);
    expect(onClose).toHaveBeenCalledTimes(1);

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Open' })));
    act(() => fireEvent.click(dialog()));
    expect(dialog().open).toBe(false);
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});

describe('ConfirmProvider', () => {
  let confirmFn: ((options: ConfirmOptions) => Promise<boolean>) | null = null;
  function Capture() {
    confirmFn = useConfirm();
    return null;
  }

  function setup() {
    render(
      <ConfirmProvider>
        <Capture />
      </ConfirmProvider>,
    );
    const ask = (options: Partial<ConfirmOptions> = {}) => {
      let promise!: Promise<boolean>;
      act(() => {
        if (!confirmFn) throw new Error('ConfirmProvider did not render');
        promise = confirmFn({ title: 'Delete this statement?', message: 'This can’t be undone.', confirmLabel: 'Delete', destructive: true, ...options });
      });
      return promise;
    };
    return { ask, dialog: () => screen.getByRole('alertdialog', { hidden: true }) };
  }

  it('resolves true when confirmed, with Cancel focused first', async () => {
    const { ask, dialog } = setup();
    const answer = ask();
    expect(dialog()).toHaveAttribute('open');
    expect(dialog()).toHaveAccessibleName('Delete this statement?');
    expect(dialog()).toHaveAccessibleDescription('This can’t be undone.');
    expect(screen.getByRole('button', { name: 'Cancel', hidden: true })).toHaveFocus();

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Delete', hidden: true })));
    await expect(answer).resolves.toBe(true);
    expect(dialog()).not.toHaveAttribute('open');
  });

  it('resolves false on Cancel and on Escape', async () => {
    const { ask, dialog } = setup();
    const cancelled = ask();
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true })));
    await expect(cancelled).resolves.toBe(false);

    const escaped = ask({ cancelLabel: 'Keep' });
    act(() => {
      dialog().dispatchEvent(new Event('cancel', { cancelable: true }));
    });
    await expect(escaped).resolves.toBe(false);
    expect(dialog()).not.toHaveAttribute('open');
  });

  it('cancels an earlier alert still waiting when a new one is shown', async () => {
    const { ask } = setup();
    const first = ask({ title: 'First?' });
    const second = ask({ title: 'Second?', confirmLabel: 'OK', destructive: false });
    await expect(first).resolves.toBe(false);
    act(() => fireEvent.click(screen.getByRole('button', { name: 'OK', hidden: true })));
    await expect(second).resolves.toBe(true);
  });
});
