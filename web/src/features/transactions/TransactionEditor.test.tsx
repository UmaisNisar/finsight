import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Transaction } from '@/api/schemas';
import categories from '@/test/fixtures/categories.json';
import transactions from '@/test/fixtures/transactions.json';
import { json, mockApi, renderWithApp } from '@/test/utils';
import { TransactionEditor } from './TransactionEditor';

afterEach(() => vi.restoreAllMocks());

const transaction = transactions.items[0] as Transaction;

function setup(overrides: Partial<Transaction> = {}, patch: (body: unknown) => Response | unknown = (body) => ({ ...transaction, ...(body as object) })) {
  const bodies: unknown[] = [];
  const fetchSpy = mockApi({
    '/api/categories': categories,
    'PATCH /api/transactions/': ({ init }: { init?: RequestInit }) => {
      const body = JSON.parse(String(init?.body));
      bodies.push(body);
      return patch(body);
    },
  });
  const onClose = vi.fn();
  renderWithApp(<TransactionEditor transaction={{ ...transaction, ...overrides }} dateFormat="yyyy-MM-dd" onClose={onClose} />);
  return { bodies, onClose, fetchSpy };
}

const merchantField = () => screen.getByRole('textbox', { name: 'Merchant', hidden: true });
const saveButton = () => screen.getByRole('button', { name: 'Save', hidden: true });

describe('TransactionEditor', () => {
  it('starts from the saved values with Save disabled until something changes', () => {
    setup();
    expect(document.querySelector('dialog')).toHaveAttribute('open');
    expect(merchantField()).toHaveValue(transaction.merchant);
    expect(saveButton()).toBeDisabled();
  });

  it('requires a merchant name and explains why Save is unavailable', () => {
    setup();
    act(() => fireEvent.change(merchantField(), { target: { value: '   ' } }));
    expect(saveButton()).toBeDisabled();
    expect(merchantField()).toHaveAttribute('aria-invalid', 'true');
    expect(merchantField()).toHaveAccessibleDescription('Enter a merchant name.');
  });

  it('sends only the fields that changed, trimmed, then closes', async () => {
    const { bodies, onClose } = setup();
    act(() => fireEvent.change(merchantField(), { target: { value: '  Corner Cafe  ' } }));
    act(() => fireEvent.click(screen.getByRole('switch', { name: 'Exclude from analysis', hidden: true })));
    act(() => fireEvent.click(saveButton()));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(bodies).toEqual([{ merchant: 'Corner Cafe', isExcluded: !transaction.isExcluded, applyToMerchant: false }]);
    expect(await screen.findByText('Transaction updated')).toBeInTheDocument();
  });

  it('shows the server error inline and stays open when saving fails', async () => {
    const { onClose } = setup({}, () => json({ code: 'validation', message: 'Merchant names can’t contain only symbols.' }, 400));
    act(() => fireEvent.change(merchantField(), { target: { value: '???' } }));
    act(() => fireEvent.click(saveButton()));

    expect(await screen.findByRole('alert', { hidden: true })).toHaveTextContent('Merchant names can’t contain only symbols.');
    expect(onClose).not.toHaveBeenCalled();
    expect(document.querySelector('dialog')).toHaveAttribute('open');
  });

  it('restores the original details for an edited transaction, reporting failures instead of throwing', async () => {
    const { bodies, onClose } = setup({ isEdited: true }, () => json({ code: 'unexpected', message: 'Couldn’t restore it. Try again.' }, 500));
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Undo my changes', hidden: true })));

    expect(await screen.findByRole('alert', { hidden: true })).toHaveTextContent('Couldn’t restore it. Try again.');
    expect(bodies).toEqual([{ resetOverrides: true }]);
    expect(onClose).not.toHaveBeenCalled();
  });
});
