import { ArrowUpRight, FileUp, Lightbulb, Plus } from 'lucide-react';
import { useId, useState } from 'react';
import { useInstitutions } from '@/api/queries';
import { DropHighlight, UploadProgressList, usePdfPicker, type UploadRowData } from '@/components/UploadProgressList';
import { AutoHeight, Collapse } from '@/components/ui/AutoHeight';
import { Button, buttonStyles } from '@/components/ui/Button';
import { PopUpButton, type PopUpSection } from '@/components/ui/PopUpButton';
import { Skeleton } from '@/components/ui/primitives';
import { InstitutionBadge } from '@/features/statements/StatementAlerts';
import { useFileDrop } from '@/hooks/useFileDrop';

export const OTHER_BANK = 'other';
export const OTHER_BANK_HINT = 'Sign in to your bank’s website, download statements as PDFs (one per month is fine), then add them here.';

/** Long lists get a search field in the menu. */
const SEARCH_THRESHOLD = 10;

/**
 * A place to drop statement PDFs, or pick them. Highlights (an accent outline and tint, no glow) while files hover.
 * Shared by the add step and anywhere else that takes several PDFs at once.
 */
export function PdfDropZone({ onFiles, hint = 'One PDF per month is fine. Add as many as you like.' }: { onFiles: (files: File[]) => void; hint?: string }) {
  const picker = usePdfPicker(onFiles);
  const drop = useFileDrop(onFiles);
  return (
    <div {...drop.handlers} className="relative flex flex-col items-center rounded-[20px] px-5 py-6 text-center shadow-[inset_0_0_0_1.5px_color-mix(in_srgb,var(--label-tertiary)_30%,transparent)]">
      {picker.input}
      <span className="flex size-11 items-center justify-center rounded-[14px] bg-accent-soft text-accent" aria-hidden="true">
        <FileUp size={21} />
      </span>
      <p className="mt-3 text-[0.9375rem] font-medium">
        <span className="hidden sm:inline">Drop statement PDFs here</span>
        <span className="sm:hidden">Add statement PDFs</span>
      </p>
      <p className="caption mt-0.5 max-w-xs">{hint}</p>
      <Button variant="secondary" size="sm" className="mt-3.5" onClick={picker.open}>
        Choose PDF files
      </Button>
      <DropHighlight active={drop.dragging} />
    </div>
  );
}

interface AddStatementsStepProps {
  rows: UploadRowData[];
  onFiles: (files: File[]) => void;
  canContinue: boolean;
  onContinue: () => void;
  onSkip: () => void;
}

/**
 * The download path: choose a bank to see how to get statements from it (and a link to sign in), then add the
 * PDFs. Files upload one at a time with progress; the list stays as the user moves on to another bank.
 */
export function AddStatementsStep({ rows, onFiles, canContinue, onContinue, onSkip }: AddStatementsStepProps) {
  const institutions = useInstitutions();
  const [bankId, setBankId] = useState<string>('');
  const bankLabel = useId();

  const list = institutions.data ?? [];
  const bank = list.find((i) => i.id === bankId);
  const sections: PopUpSection<string>[] = [
    { title: '', options: list.map((i) => ({ value: i.id, label: i.name })) },
    { title: '', options: [{ value: OTHER_BANK, label: 'Other bank', alwaysShown: true }] },
  ].filter((section) => section.options.length > 0);
  const name = bank?.name ?? 'your bank';

  return (
    <>
      <p className="text-[0.9375rem] leading-relaxed text-label-secondary">Download PDF statements from online banking and add them here. FinSight reads each one as soon as it’s added.</p>

      <div className="mt-5 flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between sm:gap-4">
        <span id={bankLabel} className="px-1 text-[0.9375rem] font-medium">
          Your bank
        </span>
        <div className="sm:w-72">
          {institutions.isPending ? (
            <Skeleton className="h-11 w-full rounded-full" />
          ) : (
            <PopUpButton
              variant="field"
              labelledBy={bankLabel}
              value={bankId}
              onChange={setBankId}
              options={sections}
              placeholder={<span className="text-label-secondary">Choose your bank</span>}
              searchable={list.length > SEARCH_THRESHOLD}
              searchPlaceholder="Search banks"
              noMatchesText="No banks match"
            />
          )}
        </div>
      </div>

      <Collapse open={bankId !== ''}>
        <AutoHeight>
          <div key={bankId} className="fade-in mt-3 flex flex-col gap-3 rounded-[18px] bg-fill/60 px-4 py-3.5 sm:flex-row sm:items-center" data-testid="bank-guide">
            <div className="flex min-w-0 flex-1 items-start gap-3">
              {bank && <InstitutionBadge name={bank.name} size={34} />}
              <p className="min-w-0 text-[0.875rem] leading-snug text-label-secondary">{bank?.downloadHint?.trim() || OTHER_BANK_HINT}</p>
            </div>
            {bank?.signInUrl && (
              <a href={bank.signInUrl} target="_blank" rel="noopener noreferrer" className={buttonStyles({ variant: 'secondary', size: 'sm', className: 'self-start sm:self-center' })}>
                Open {name}
                <ArrowUpRight size={14} strokeWidth={2.25} aria-hidden="true" />
                <span className="sr-only">(opens in a new tab)</span>
              </a>
            )}
          </div>
        </AutoHeight>
      </Collapse>

      <div className="mt-4">
        <PdfDropZone onFiles={onFiles} />
      </div>

      <p className="caption mt-3 flex items-center gap-1.5 px-1">
        <Lightbulb size={13} className="shrink-0 text-label-tertiary" aria-hidden="true" />
        Tip: add the last 3–6 months for better insights.
      </p>

      <Collapse open={rows.length > 0}>
        <h3 className="eyebrow mt-5 mb-2 px-1">Added by you</h3>
        <UploadProgressList label="Added by you" rows={rows} />
        <Collapse open={bankId !== ''}>
          <Button variant="plain" size="sm" className="mt-2 -ml-2" icon={<Plus size={15} aria-hidden="true" />} onClick={() => setBankId('')}>
            Add statements from another bank
          </Button>
        </Collapse>
      </Collapse>

      <div className="mt-6 flex flex-col gap-2 *:w-full sm:flex-row sm:flex-wrap sm:items-center sm:*:w-auto">
        <Button disabled={!canContinue} onClick={onContinue}>
          Continue
        </Button>
        <Button variant="plain" onClick={onSkip}>
          Skip for now
        </Button>
      </div>
    </>
  );
}
