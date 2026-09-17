import { useId, useState } from 'react';
import { errorMessage } from '@/api/client';
import { useCategories, useUpdateTransaction } from '@/api/queries';
import type { Transaction, TransactionType } from '@/api/schemas';
import { useToast } from '@/app/providers/ToastProvider';
import { Collapse } from '@/components/ui/AutoHeight';
import { Button } from '@/components/ui/Button';
import { PopUpButton } from '@/components/ui/PopUpButton';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { Sheet } from '@/components/ui/Sheet';
import { Switch } from '@/components/ui/primitives';
import { useRetained } from '@/hooks/useRetained';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { CategoryGlyph } from '@/lib/categories';
import { cn } from '@/lib/cn';
import { accountLabel, formatDate, formatMoney } from '@/lib/format';

const SOURCE_LABEL: Record<Transaction['categorySource'], string> = {
  default: 'Not yet categorized',
  rule: 'Categorized automatically',
  ai: 'Categorized by AI',
  user: 'Categorized by you',
};

/**
 * Edits one transaction in a sheet. A fresh form mounts each time a transaction opens, so fields always start
 * from its saved values; the last one stays rendered while the sheet animates closed.
 */
export function TransactionEditor({ transaction, dateFormat, onClose }: { transaction: Transaction | null; dateFormat: string; onClose: () => void }) {
  const { item, open, key } = useRetained(transaction);
  if (!item) {
    return null;
  }
  return <EditorSheet key={key} transaction={item} open={open} dateFormat={dateFormat} onClose={onClose} />;
}

function EditorSheet({ transaction, open, dateFormat, onClose }: { transaction: Transaction; open: boolean; dateFormat: string; onClose: () => void }) {
  const categories = useCategories();
  const update = useUpdateTransaction();
  const toast = useToast();
  const merchantId = useId();
  const merchantHintId = useId();
  const categoryLabelId = useId();
  const once = useSingleFlight();

  const [merchant, setMerchant] = useState(transaction.merchant);
  const [categoryId, setCategoryId] = useState(transaction.categoryId);
  const [type, setType] = useState<TransactionType>(transaction.type);
  const [excluded, setExcluded] = useState(transaction.isExcluded);
  const [applyToMerchant, setApplyToMerchant] = useState(false);

  const trimmed = merchant.trim();
  const categoryChanged = categoryId !== transaction.categoryId;
  const appliesToMerchant = categoryChanged && applyToMerchant;
  const merchantMissing = trimmed.length === 0;
  const dirty = trimmed !== transaction.merchant || categoryChanged || type !== transaction.type || excluded !== transaction.isExcluded;

  async function save() {
    if (merchantMissing || !dirty) return;
    try {
      await update.mutateAsync({
        id: transaction.id,
        update: {
          merchant: trimmed !== transaction.merchant ? trimmed : undefined,
          categoryId: categoryChanged ? categoryId : undefined,
          type: type !== transaction.type ? type : undefined,
          isExcluded: excluded !== transaction.isExcluded ? excluded : undefined,
          applyToMerchant: appliesToMerchant,
        },
      });
      toast(appliesToMerchant ? `Updated every ${trimmed} transaction` : 'Transaction updated', 'success');
      onClose();
    } catch {
      // The error is shown inline below.
    }
  }

  async function reset() {
    try {
      await update.mutateAsync({ id: transaction.id, update: { resetOverrides: true } });
      toast('Restored the original details', 'success');
      onClose();
    } catch {
      // The error is shown inline below.
    }
  }

  return (
    <Sheet
      open={open}
      onClose={onClose}
      title={transaction.merchant}
      subtitle={`${formatDate(transaction.date, dateFormat)} · ${accountLabel(transaction.account.institution, transaction.account.mask)}`}
      footer={
        <>
          {transaction.isEdited && (
            <Button variant="plain" onClick={() => void once(reset)} disabled={update.isPending}>
              Undo my changes
            </Button>
          )}
          <div className="ml-auto flex gap-2">
            <Button variant="secondary" onClick={onClose}>
              Cancel
            </Button>
            <Button onClick={() => void once(save)} disabled={!dirty || merchantMissing} loading={update.isPending}>
              Save
            </Button>
          </div>
        </>
      }
    >
      <div className="flex flex-col items-center pt-2 pb-6 text-center">
        <CategoryGlyph groupId={transaction.groupId} type={transaction.type} size={52} />
        <p className={cn('figure mt-3 text-[2.25rem]', transaction.amount > 0 && transaction.type === 'income' && 'text-positive')}>
          {formatMoney(transaction.amount, transaction.currency, { signed: transaction.amount > 0 })}
        </p>
        <p className="caption mt-1">{SOURCE_LABEL[transaction.categorySource]}</p>
      </div>

      <form
        className="space-y-6"
        onSubmit={(event) => {
          event.preventDefault();
          void once(save);
        }}
      >
        <div>
          <label htmlFor={merchantId} className="eyebrow mb-1.5 block">
            Merchant
          </label>
          <input
            id={merchantId}
            value={merchant}
            maxLength={80}
            aria-invalid={merchantMissing || undefined}
            aria-describedby={merchantMissing ? merchantHintId : undefined}
            onChange={(e) => setMerchant(e.target.value)}
            className={cn('glass-control h-11 w-full rounded-full px-4 text-[0.9375rem]', merchantMissing && 'shadow-[inset_0_0_0_1.5px_var(--critical)]')}
          />
          {merchantMissing && (
            <p id={merchantHintId} className="fade-in mt-1.5 px-4 text-[0.8125rem] text-critical">
              Enter a merchant name.
            </p>
          )}
        </div>

        <div>
          <span id={categoryLabelId} className="eyebrow mb-1.5 block">
            Category
          </span>
          <PopUpButton
            variant="field"
            labelledBy={categoryLabelId}
            value={categoryId}
            onChange={setCategoryId}
            searchable
            searchPlaceholder="Search categories"
            disabled={!categories.data}
            placeholder={transaction.categoryName}
            options={(categories.data ?? []).map((group) => ({ title: group.name, options: group.categories.map((c) => ({ value: c.id, label: c.name })) }))}
          />
          <Collapse open={categoryChanged}>
            <div className="glass-control mt-3 flex items-center justify-between gap-4 rounded-xl px-4 py-3">
              <div>
                <p className="text-[0.9375rem]">Apply to all from {trimmed || 'this merchant'}</p>
                <p className="caption">Past and future transactions</p>
              </div>
              <Switch checked={applyToMerchant} onChange={setApplyToMerchant} label={`Apply this category to every transaction from ${trimmed || 'this merchant'}`} />
            </div>
          </Collapse>
        </div>

        <div>
          <span className="eyebrow mb-1.5 block" aria-hidden="true">
            Counts as
          </span>
          <SegmentedControl
            label="Counts as"
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

        <div className="glass-control flex items-center justify-between gap-4 rounded-xl px-4 py-3">
          <div>
            <p className="text-[0.9375rem]">Exclude from analysis</p>
            <p className="caption">Hidden from totals, charts and insights</p>
          </div>
          <Switch checked={excluded} onChange={setExcluded} label="Exclude from analysis" />
        </div>

        <section aria-label="On your statement" className="rounded-xl bg-surface-sunken px-4 py-3">
          <h3 className="eyebrow">On your statement</h3>
          <p className="mt-1 font-mono text-[0.8125rem] break-words text-label-secondary">{transaction.description}</p>
          {transaction.postingDate && transaction.postingDate !== transaction.date && <p className="caption mt-1">Posted {formatDate(transaction.postingDate, dateFormat)}</p>}
          {transaction.isReversal && <p className="caption mt-1">Reversed by a matching credit, so it’s not counted.</p>}
        </section>

        {update.isError && (
          <p role="alert" className="text-[0.9375rem] text-critical">
            {errorMessage(update.error)}
          </p>
        )}
      </form>
    </Sheet>
  );
}
