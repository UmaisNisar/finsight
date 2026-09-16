import { useState } from 'react';
import { errorMessage } from '@/api/client';
import { useCategories, useUpdateTransaction } from '@/api/queries';
import type { Transaction, TransactionType } from '@/api/schemas';
import { useToast } from '@/app/providers/ToastProvider';
import { Button } from '@/components/ui/Button';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { Sheet } from '@/components/ui/Sheet';
import { Switch } from '@/components/ui/primitives';
import { CategoryGlyph } from '@/lib/categories';
import { accountLabel, formatDate, formatMoney } from '@/lib/format';

const SOURCE_LABEL: Record<Transaction['categorySource'], string> = {
  default: 'Not yet categorized',
  rule: 'Categorized automatically',
  ai: 'Categorized by AI',
  user: 'Categorized by you',
};

/** Mounts a fresh form per transaction, so its fields always start from that transaction's values. */
export function TransactionEditor({ transaction, dateFormat, onClose }: { transaction: Transaction | null; dateFormat: string; onClose: () => void }) {
  if (!transaction) {
    return null;
  }
  return <EditorSheet key={transaction.id} transaction={transaction} dateFormat={dateFormat} onClose={onClose} />;
}

function EditorSheet({ transaction, dateFormat, onClose }: { transaction: Transaction; dateFormat: string; onClose: () => void }) {
  const categories = useCategories();
  const update = useUpdateTransaction();
  const toast = useToast();

  const [merchant, setMerchant] = useState(transaction.merchant);
  const [categoryId, setCategoryId] = useState(transaction.categoryId);
  const [type, setType] = useState<TransactionType>(transaction.type);
  const [excluded, setExcluded] = useState(transaction.isExcluded);
  const [applyToMerchant, setApplyToMerchant] = useState(false);

  const dirty =
    merchant.trim() !== transaction.merchant || categoryId !== transaction.categoryId || type !== transaction.type || excluded !== transaction.isExcluded;

  async function save() {
    try {
      await update.mutateAsync({
        id: transaction.id,
        update: {
          merchant: merchant.trim() !== transaction.merchant ? merchant.trim() : undefined,
          categoryId: categoryId !== transaction.categoryId ? categoryId : undefined,
          type: type !== transaction.type ? type : undefined,
          isExcluded: excluded !== transaction.isExcluded ? excluded : undefined,
          applyToMerchant: categoryId !== transaction.categoryId && applyToMerchant,
        },
      });
      toast(applyToMerchant ? `Updated every ${merchant.trim()} transaction` : 'Transaction updated', 'success');
      onClose();
    } catch {
      // The error is shown inline below.
    }
  }

  async function reset() {
    await update.mutateAsync({ id: transaction.id, update: { resetOverrides: true } });
    toast('Restored the original details', 'success');
    onClose();
  }

  return (
    <Sheet
      open
      onClose={onClose}
      title={transaction.merchant}
      subtitle={`${formatDate(transaction.date, dateFormat)} · ${accountLabel(transaction.account.institution, transaction.account.mask)}`}
      footer={
        <>
          {transaction.isEdited && (
            <Button variant="plain" onClick={reset} disabled={update.isPending}>
              Undo my changes
            </Button>
          )}
          <div className="ml-auto flex gap-2">
            <Button variant="secondary" onClick={onClose}>
              Cancel
            </Button>
            <Button onClick={save} disabled={!dirty || merchant.trim().length === 0} loading={update.isPending}>
              Save
            </Button>
          </div>
        </>
      }
    >
      <div className="flex flex-col items-center pt-2 pb-6 text-center">
        <CategoryGlyph groupId={transaction.groupId} type={transaction.type} size={52} />
        <p className={`figure mt-3 text-[2.25rem] ${transaction.amount > 0 && transaction.type === 'income' ? 'text-positive' : ''}`}>
          {formatMoney(transaction.amount, transaction.currency, { signed: transaction.amount > 0 })}
        </p>
        <p className="caption mt-1">{SOURCE_LABEL[transaction.categorySource]}</p>
      </div>

      <div className="space-y-6">
        <label className="block">
          <span className="eyebrow mb-1.5 block">Merchant</span>
          <input
            value={merchant}
            maxLength={80}
            onChange={(e) => setMerchant(e.target.value)}
            className="h-11 w-full rounded-xl bg-surface px-3.5 text-[0.9375rem] shadow-soft"
          />
        </label>

        <label className="block">
          <span className="eyebrow mb-1.5 block">Category</span>
          <select
            value={categoryId}
            onChange={(e) => setCategoryId(e.target.value)}
            className="h-11 w-full appearance-none rounded-xl bg-surface px-3.5 text-[0.9375rem] shadow-soft"
            disabled={!categories.data}
          >
            {categories.data?.map((group) => (
              <optgroup key={group.id} label={group.name}>
                {group.categories.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </optgroup>
            ))}
          </select>
        </label>

        {categoryId !== transaction.categoryId && (
          <div className="flex items-center justify-between gap-4 rounded-xl bg-surface px-4 py-3 shadow-soft">
            <div>
              <p className="text-[0.9375rem]">Apply to all from {merchant.trim() || 'this merchant'}</p>
              <p className="caption">Past and future transactions</p>
            </div>
            <Switch checked={applyToMerchant} onChange={setApplyToMerchant} label={`Apply category to all transactions from ${merchant}`} />
          </div>
        )}

        <div>
          <span className="eyebrow mb-1.5 block" id="type-label">
            Counts as
          </span>
          <SegmentedControl
            label="Transaction type"
            className="w-full"
            value={type}
            onChange={setType}
            options={[
              { value: 'expense', label: 'Spending' },
              { value: 'income', label: 'Income' },
              { value: 'transfer', label: 'Transfer' },
            ]}
          />
          <p className="caption mt-2">Transfers between your own accounts and card payments aren’t counted as income or spending.</p>
        </div>

        <div className="flex items-center justify-between gap-4 rounded-xl bg-surface px-4 py-3 shadow-soft">
          <div>
            <p className="text-[0.9375rem]">Exclude from analysis</p>
            <p className="caption">Hidden from totals, charts and insights</p>
          </div>
          <Switch checked={excluded} onChange={setExcluded} label="Exclude from analysis" />
        </div>

        <section aria-labelledby="original-title" className="rounded-xl bg-surface-sunken px-4 py-3">
          <h3 id="original-title" className="eyebrow">
            On your statement
          </h3>
          <p className="mt-1 font-mono text-[0.8125rem] break-words text-label-secondary">{transaction.description}</p>
          {transaction.postingDate && transaction.postingDate !== transaction.date && (
            <p className="caption mt-1">Posted {formatDate(transaction.postingDate, dateFormat)}</p>
          )}
          {transaction.isReversal && <p className="caption mt-1">Reversed by a matching credit, so it’s not counted.</p>}
        </section>

        {update.isError && (
          <p role="alert" className="text-[0.9375rem] text-critical">
            {errorMessage(update.error)}
          </p>
        )}
      </div>
    </Sheet>
  );
}
