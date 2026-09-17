import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ChevronRight, LogOut } from 'lucide-react';
import { useId, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys, markSignedOut, useAiKey, useDeleteAiKey, useGmail, useInvalidateFinancialData, useSession, useSettings, useSignOut, useUpdateSettings } from '@/api/queries';
import type { AiKey, Settings } from '@/api/schemas';
import { useConfirm } from '@/app/providers/ConfirmProvider';
import { useToast } from '@/app/providers/ToastProvider';
import { PageHeader } from '@/components/PageHeader';
import { Collapse } from '@/components/ui/AutoHeight';
import { Button, buttonStyles } from '@/components/ui/Button';
import { PopUpButton } from '@/components/ui/PopUpButton';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { Card, ErrorState, Skeleton, Switch } from '@/components/ui/primitives';
import { Sheet } from '@/components/ui/Sheet';
import { GeminiKeyForm } from '@/features/ai/GeminiKeyForm';
import { useRetained } from '@/hooks/useRetained';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { cn } from '@/lib/cn';
import { formatRelativeTime } from '@/lib/format';
import { gmailConnectUrl } from '@/lib/gmail';
import { CURRENCY_OPTIONS, DATE_FORMAT_OPTIONS } from '@/lib/preferences';

/*
  Layout: grouped inset lists in the style of System Settings. One column on phones and tablets; on wide screens
  two balanced columns, so the page uses the same width as Overview and Insights instead of one tall strip.
  Destructive data actions always come last (bottom of the right column, or the end of the page on phones).

  Rows are 44px for a single line (the Apple minimum hit target) and grow only for a one-line caption.
*/

const ROW = 'flex min-h-11 items-center justify-between gap-4 px-4 py-2';

function Group({ title, footer, children, className }: { title?: string; footer?: ReactNode; children: ReactNode; className?: string }) {
  const id = useId();
  return (
    <section aria-labelledby={title ? id : undefined} aria-label={title ? undefined : 'Account deletion'} className={className}>
      {title && (
        <h2 id={id} className="eyebrow mb-1 px-4">
          {title}
        </h2>
      )}
      <div className="card grouped overflow-hidden rounded-[18px]">{children}</div>
      {footer && <p className="caption mt-1 px-4 leading-snug">{footer}</p>}
    </section>
  );
}

function RowText({ label, detail, labelId }: { label: ReactNode; detail?: ReactNode; labelId?: string }) {
  return (
    <div className="min-w-0">
      <span id={labelId} className="block text-[0.9375rem] leading-5">
        {label}
      </span>
      {detail && <span className="caption block leading-[18px]">{detail}</span>}
    </div>
  );
}

function Row({ label, detail, control, labelId }: { label: ReactNode; detail?: ReactNode; control: ReactNode; labelId?: string }) {
  return (
    <div className={ROW}>
      <RowText label={label} detail={detail} labelId={labelId} />
      <div className="flex shrink-0 items-center">{control}</div>
    </div>
  );
}

function ActionRow({ label, detail, onClick, destructive, disabled }: { label: string; detail?: string; onClick: () => void; destructive?: boolean; disabled?: boolean }) {
  return (
    <button type="button" onClick={onClick} disabled={disabled} className={cn(ROW, 'w-full text-left transition-colors hover:bg-fill disabled:opacity-45')}>
      <RowText label={<span className={destructive ? 'text-critical' : 'text-accent'}>{label}</span>} detail={detail} />
      <ChevronRight size={16} className="shrink-0 text-label-tertiary" aria-hidden="true" />
    </button>
  );
}

/** Inline error under a group, which expands in and collapses away. */
function InlineError({ message, className }: { message: string | null; className?: string }) {
  const retained = useRetained(message);
  return (
    <Collapse open={retained.open}>
      <p role="alert" className={cn('px-4 pt-1.5 text-[0.875rem] text-critical', className)}>
        {retained.item}
      </p>
    </Collapse>
  );
}

/** Placeholders the size of each control, so rows keep their height while settings load. */
const SwitchSkeleton = () => <Skeleton className="h-[31px] w-[51px] rounded-full" />;
const PopUpSkeleton = () => <Skeleton className="h-8 w-28 rounded-full" />;

type DeleteAction = 'transactions' | 'statements' | 'all' | 'account';

export function aiKeyStatus(key: AiKey): string {
  if (key.hasUserKey) return `Your key ${key.hint ?? ''}`.trim();
  if (key.serverKeyAvailable) return 'Using FinSight’s built-in AI';
  return 'Not set up — AI categorization and insights are off';
}

/**
 * The user's own Gemini API key: status and model, a sheet with the guided form shared with onboarding, and removal.
 * One row in every state (text above its buttons on phones, beside them on wider screens), so loading and
 * adding or removing a key never change the group's height.
 */
function GeminiKeyRow() {
  const aiKey = useAiKey();
  const remove = useDeleteAiKey();
  const confirm = useConfirm();
  const toast = useToast();
  const once = useSingleFlight();
  const [sheetOpen, setSheetOpen] = useState(false);
  const [formKey, setFormKey] = useState(0);
  const key = aiKey.data;
  const retainedKey = useRetained(key ?? null).item;

  function openSheet() {
    remove.reset();
    setFormKey(formKey + 1);
    setSheetOpen(true);
  }

  function removeKey(current: AiKey) {
    void once(async () => {
      const ok = await confirm({
        title: 'Remove your API key?',
        message: current.serverKeyAvailable ? 'FinSight’s built-in AI will be used instead.' : 'AI categorization and insights will stop until you add a key again.',
        confirmLabel: 'Remove',
        destructive: true,
      });
      if (!ok) return;
      try {
        await remove.mutateAsync();
        toast('API key removed', 'success');
      } catch {
        // Shown inline under the group.
      }
    });
  }

  let detail: ReactNode;
  let controls: ReactNode;
  if (aiKey.isPending) {
    detail = (
      <span className="flex h-[18px] items-center">
        <Skeleton className="h-3 w-40 max-w-full" />
      </span>
    );
    controls = <Skeleton className="h-8 w-[84px] rounded-full" />;
  } else if (!key) {
    detail = 'Couldn’t check your key';
    controls = (
      <Button variant="tinted" size="sm" onClick={() => void aiKey.refetch()}>
        Try again
      </Button>
    );
  } else {
    detail = <span className="fade-in block">{aiKeyStatus(key)}</span>;
    controls = (
      <>
        {key.hasUserKey && (
          <Button variant="destructive-plain" size="sm" loading={remove.isPending} onClick={() => removeKey(key)}>
            Remove key
          </Button>
        )}
        <Button variant="tinted" size="sm" onClick={openSheet}>
          {key.hasUserKey ? 'Replace key' : 'Add key'}
        </Button>
      </>
    );
  }

  return (
    <div>
      <div className="flex min-h-11 flex-col gap-1 px-4 py-2 sm:flex-row sm:items-center sm:justify-between sm:gap-4">
        <RowText
          label={
            <>
              Gemini API key
              {key?.model && <span className="fade-in ml-1.5 text-[0.75rem] text-label-tertiary">{key.model}</span>}
            </>
          }
          detail={detail}
        />
        <div className="-mr-1 flex shrink-0 items-center justify-end gap-1 sm:mr-0">{controls}</div>
      </div>
      <InlineError message={remove.isError ? errorMessage(remove.error) : null} className="pt-0 pb-2.5" />

      <Sheet open={sheetOpen} onClose={() => setSheetOpen(false)} title={retainedKey?.hasUserKey ? 'Replace API key' : 'Add a Gemini API key'} subtitle="For AI categories and insights">
        <p className="mb-4 text-[0.9375rem] leading-relaxed text-label-secondary">
          Gemini sorts merchants into categories and writes insights about your spending. The numbers are always calculated by FinSight.
        </p>
        <GeminiKeyForm
          key={formKey}
          compact
          onCancel={() => setSheetOpen(false)}
          onSaved={() => {
            setSheetOpen(false);
            toast('API key saved', 'success');
          }}
        />
      </Sheet>
    </div>
  );
}

export default function SettingsPage() {
  const session = useSession();
  const settings = useSettings();
  const user = session.data?.user;
  const isDemo = user?.isDemo ?? false;
  const showGmail = !!user && !isDemo && (session.data?.capabilities.gmail ?? false);
  const showKey = !!user && !isDemo;
  const gmail = useGmail(showGmail);
  const update = useUpdateSettings();
  const toast = useToast();
  const confirm = useConfirm();
  const client = useQueryClient();
  const navigate = useNavigate();
  const invalidate = useInvalidateFinancialData();
  const signOut = useSignOut();
  // One destructive or account action at a time, including while its confirmation alert is open.
  const once = useSingleFlight();
  const currencyLabel = useId();
  const dateFormatLabel = useId();
  const appearanceLabel = useId();

  const disconnect = useMutation({
    mutationFn: api.disconnectGmail,
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: keys.gmail });
      toast('Gmail disconnected. FinSight can no longer read your email.', 'success');
    },
  });

  const destroy = useMutation({
    mutationFn: async (action: DeleteAction) => {
      if (action === 'transactions') await api.deleteTransactions();
      else if (action === 'statements') await api.deleteStatements();
      else if (action === 'all') await api.deleteAllData();
      else await api.deleteAccount();
      return action;
    },
    onSuccess: async (action) => {
      if (action === 'account') {
        markSignedOut(client);
        navigate('/', { replace: true });
        return;
      }
      await invalidate();
      void client.invalidateQueries({ queryKey: keys.gmail });
      toast(action === 'transactions' ? 'All transactions deleted' : action === 'statements' ? 'All statements deleted' : 'All financial data deleted', 'success');
    },
  });

  function confirmDestroy(action: DeleteAction, title: string, message: string) {
    return once(async () => {
      if (await confirm({ title, message, confirmLabel: 'Delete', destructive: true })) await destroy.mutateAsync(action);
    });
  }

  const s = settings.data;
  const connection = gmail.data;

  function change(patch: Partial<Settings>) {
    if (s) update.mutate({ ...s, ...patch });
  }

  const account = user && (
    <Group title="Account">
      <div className="flex items-center gap-3 px-4 py-2.5">
        <span aria-hidden="true" className="flex size-9 shrink-0 items-center justify-center rounded-full bg-fill-strong text-[0.9375rem] font-semibold">
          {user.name.slice(0, 1).toUpperCase()}
        </span>
        <div className="min-w-0 flex-1">
          <p className="truncate text-[0.9375rem] leading-5 font-semibold">{user.name}</p>
          <p className="caption truncate leading-[18px]">{isDemo ? 'Demo account with sample data' : user.email}</p>
        </div>
        <Button
          variant="secondary"
          size="sm"
          icon={<LogOut size={14} aria-hidden="true" />}
          loading={signOut.isPending}
          onClick={() => void once(() => signOut.mutateAsync().then(() => navigate('/', { replace: true })))}
        >
          {isDemo ? 'Exit demo' : 'Sign out'}
        </Button>
      </div>
    </Group>
  );

  const gmailGroup = showGmail && (
    <Group title="Gmail" footer="Read-only access to find statement PDFs. FinSight never sends email or changes your inbox.">
      {gmail.isPending ? (
        <Row
          label={
            <span className="flex h-5 items-center">
              <Skeleton className="h-3.5 w-40" />
            </span>
          }
          detail={
            <span className="flex h-[18px] items-center">
              <Skeleton className="h-3 w-28" />
            </span>
          }
          control={<Skeleton className="h-3.5 w-16" />}
        />
      ) : connection?.connected ? (
        <>
          <Row
            label={connection.email ?? 'Connected account'}
            detail={connection.status === 'expired' ? 'Connection expired. Reconnect to scan again.' : connection.lastSyncedAt ? `Last scanned ${formatRelativeTime(connection.lastSyncedAt)}` : 'Connected'}
            control={
              connection.status === 'expired' ? (
                <a href={gmailConnectUrl('/statements')} className={buttonStyles({ variant: 'tinted', size: 'sm' })}>
                  Reconnect
                </a>
              ) : (
                <span className="caption">Read-only</span>
              )
            }
          />
          <ActionRow
            label="Disconnect Gmail"
            detail="Imported statements stay until you delete them."
            destructive
            disabled={disconnect.isPending}
            onClick={() =>
              void once(async () => {
                const ok = await confirm({
                  title: 'Disconnect Gmail?',
                  message: 'FinSight will no longer be able to find new statements. Imported statements stay until you delete them.',
                  confirmLabel: 'Disconnect',
                  destructive: true,
                });
                if (ok) await disconnect.mutateAsync();
              })
            }
          />
        </>
      ) : (
        <Row
          label="Not connected"
          detail="Connect to find statements automatically."
          control={
            <a href={gmailConnectUrl('/statements')} className={buttonStyles({ variant: 'tinted', size: 'sm' })}>
              Connect
            </a>
          }
        />
      )}
    </Group>
  );

  const aiGroup = (
    <Group
      title="AI"
      footer={
        session.data?.capabilities.ai
          ? 'The AI sees totals and merchant names, never account numbers or your identity.'
          : isDemo
            ? 'AI isn’t configured on this server, so these have no effect yet.'
            : 'Add a Gemini API key to turn these on. The AI never sees account numbers, email content or your identity.'
      }
    >
      {showKey && <GeminiKeyRow />}
      <Row
        label="AI categorization"
        detail="For merchants FinSight doesn’t know"
        control={s ? <Switch label="AI categorization" checked={s.aiCategorizationEnabled} onChange={(v) => change({ aiCategorizationEnabled: v })} /> : <SwitchSkeleton />}
      />
      <Row
        label="AI insights"
        detail="Plain-language analysis of your spending"
        control={s ? <Switch label="AI insights" checked={s.aiInsightsEnabled} onChange={(v) => change({ aiInsightsEnabled: v })} /> : <SwitchSkeleton />}
      />
    </Group>
  );

  const preferencesGroup = (
    <div>
      <Group title="Preferences">
        <Row
          label="Currency"
          labelId={currencyLabel}
          detail="Display only, nothing is converted"
          control={s ? <PopUpButton labelledBy={currencyLabel} value={s.currency} onChange={(currency) => change({ currency })} options={CURRENCY_OPTIONS} /> : <PopUpSkeleton />}
        />
        <Row
          label="Date format"
          labelId={dateFormatLabel}
          control={s ? <PopUpButton labelledBy={dateFormatLabel} value={s.dateFormat} onChange={(dateFormat) => change({ dateFormat })} options={DATE_FORMAT_OPTIONS} /> : <PopUpSkeleton />}
        />
        <Row
          label="Appearance"
          labelId={appearanceLabel}
          control={
            s ? (
              <SegmentedControl
                label="Appearance"
                size="sm"
                value={s.theme}
                onChange={(theme) => change({ theme })}
                options={[
                  { value: 'system', label: 'Auto' },
                  { value: 'light', label: 'Light' },
                  { value: 'dark', label: 'Dark' },
                ]}
              />
            ) : (
              <Skeleton className="h-9 w-[152px] rounded-full" />
            )
          }
        />
        <Row
          label="Email notifications"
          detail="New statements · coming soon"
          control={s ? <Switch label="Email notifications" checked={s.notificationsEnabled} onChange={(v) => change({ notificationsEnabled: v })} /> : <SwitchSkeleton />}
        />
      </Group>
      <InlineError message={update.isError ? errorMessage(update.error) : null} />
    </div>
  );

  const dataGroups = (
    <div className="space-y-3">
      <Group title="Your data" footer="Deleting is permanent. PDFs are never stored.">
        <ActionRow
          label="Delete all transactions"
          detail="Statements can be analyzed again"
          destructive
          disabled={destroy.isPending}
          onClick={() => void confirmDestroy('transactions', 'Delete all transactions?', 'Statements found in Gmail will return to “ready to analyze”.')}
        />
        <ActionRow
          label="Delete all statements"
          detail="Removes statements and their transactions"
          destructive
          disabled={destroy.isPending}
          onClick={() => void confirmDestroy('statements', 'Delete all statements?', 'Every statement and all of their transactions will be removed.')}
        />
        <ActionRow
          label="Delete all financial data"
          detail="Also rules and insights, and disconnects Gmail"
          destructive
          disabled={destroy.isPending}
          onClick={() => void confirmDestroy('all', 'Delete all financial data?', 'Statements, transactions, rules and insights will be removed and Gmail disconnected. This can’t be undone.')}
        />
      </Group>
      <Group>
        <ActionRow
          label="Delete account"
          detail="All of the above, plus your FinSight account"
          destructive
          disabled={destroy.isPending}
          onClick={() => void confirmDestroy('account', 'Delete your account?', 'Your FinSight account and everything in it will be removed. This can’t be undone.')}
        />
      </Group>
      <InlineError message={destroy.isError ? errorMessage(destroy.error) : null} />
    </div>
  );

  // Two balanced columns on wide screens. With Gmail or a key row, AI sits under the account; for the demo
  // (no Gmail, no key) Preferences does instead, so neither column runs much longer than the other.
  const aiLeft = showGmail || showKey;

  return (
    <div className="mx-auto max-w-2xl lg:max-w-none">
      <PageHeader title="Settings" />

      {settings.isError && (
        <Card className="mb-5">
          <ErrorState message={errorMessage(settings.error)} onRetry={() => void settings.refetch()} />
        </Card>
      )}

      {/* grid-cols-1 (minmax(0, 1fr)) so a wide row can't stretch the single phone column past the screen. */}
      <div className="grid grid-cols-1 items-start gap-5 lg:grid-cols-2 lg:gap-6">
        <div className="space-y-5">
          {account}
          {gmailGroup}
          {!settings.isError && (aiLeft ? aiGroup : preferencesGroup)}
        </div>
        {!settings.isError && (
          <div className="space-y-5">
            {aiLeft ? preferencesGroup : aiGroup}
            {dataGroups}
          </div>
        )}
      </div>
    </div>
  );
}
