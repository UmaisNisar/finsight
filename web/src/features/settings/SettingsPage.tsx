import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ChevronRight, LogOut } from 'lucide-react';
import type { ReactNode } from 'react';
import { useNavigate } from 'react-router';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys, useGmail, useInvalidateFinancialData, useSession, useSettings, useUpdateSettings } from '@/api/queries';
import type { Settings } from '@/api/schemas';
import { useToast } from '@/app/providers/ToastProvider';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { ErrorState, Skeleton, Switch } from '@/components/ui/primitives';
import { formatDate, formatRelativeTime } from '@/lib/format';

function Group({ title, footer, children }: { title: string; footer?: ReactNode; children: ReactNode }) {
  const id = `settings-${title.toLowerCase().replace(/\W+/g, '-')}`;
  return (
    <section aria-labelledby={id}>
      <h2 id={id} className="eyebrow mb-2 px-4">
        {title}
      </h2>
      <div className="card overflow-hidden [&>*+*]:shadow-[inset_0_0.5px_0_var(--separator)]">{children}</div>
      {footer && <p className="caption mt-2 px-4">{footer}</p>}
    </section>
  );
}

function Row({ label, detail, control, htmlFor }: { label: string; detail?: ReactNode; control: ReactNode; htmlFor?: string }) {
  const LabelTag = htmlFor ? 'label' : 'div';
  return (
    <div className="flex min-h-14 items-center justify-between gap-4 px-4 py-3 md:px-5">
      <LabelTag {...(htmlFor ? { htmlFor } : {})} className="min-w-0">
        <span className="block text-[0.9375rem]">{label}</span>
        {detail && <span className="caption block">{detail}</span>}
      </LabelTag>
      {control}
    </div>
  );
}

function ActionRow({ label, detail, onClick, destructive, disabled }: { label: string; detail?: string; onClick: () => void; destructive?: boolean; disabled?: boolean }) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className="flex min-h-14 w-full items-center justify-between gap-4 px-4 py-3 text-left transition-colors hover:bg-fill disabled:opacity-45 md:px-5"
    >
      <span>
        <span className={`block text-[0.9375rem] ${destructive ? 'text-critical' : 'text-accent'}`}>{label}</span>
        {detail && <span className="caption block">{detail}</span>}
      </span>
      <ChevronRight size={16} className="text-label-tertiary" aria-hidden="true" />
    </button>
  );
}

const DATE_FORMATS = ['MMM d, yyyy', 'd MMM yyyy', 'yyyy-MM-dd', 'MM/dd/yyyy', 'dd/MM/yyyy'];
const SAMPLE_DATE = '2026-09-12';

export default function SettingsPage() {
  const session = useSession();
  const settings = useSettings();
  const gmail = useGmail(session.data?.capabilities.gmail ?? false);
  const update = useUpdateSettings();
  const toast = useToast();
  const client = useQueryClient();
  const navigate = useNavigate();
  const invalidate = useInvalidateFinancialData();

  const user = session.data?.user;
  const isDemo = user?.isDemo ?? false;

  const disconnect = useMutation({
    mutationFn: api.disconnectGmail,
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: keys.gmail });
      toast('Gmail disconnected. FinSight can no longer read your email.', 'success');
    },
  });

  const destroy = useMutation({
    mutationFn: async (action: 'transactions' | 'statements' | 'all' | 'account') => {
      if (action === 'transactions') await api.deleteTransactions();
      else if (action === 'statements') await api.deleteStatements();
      else if (action === 'all') await api.deleteAllData();
      else await api.deleteAccount();
      return action;
    },
    onSuccess: async (action) => {
      if (action === 'account') {
        client.clear();
        navigate('/', { replace: true });
        return;
      }
      await invalidate();
      void client.invalidateQueries({ queryKey: keys.gmail });
      toast(action === 'transactions' ? 'All transactions deleted' : action === 'statements' ? 'All statements deleted' : 'All financial data deleted', 'success');
    },
  });

  const logout = useMutation({
    mutationFn: api.logout,
    onSuccess: () => {
      client.clear();
      navigate('/', { replace: true });
    },
  });

  function confirmDestroy(action: 'transactions' | 'statements' | 'all' | 'account', message: string) {
    if (window.confirm(message)) destroy.mutate(action);
  }

  function change(patch: Partial<Settings>) {
    if (settings.data) update.mutate({ ...settings.data, ...patch });
  }

  if (settings.isPending) {
    return (
      <div className="space-y-6" aria-busy="true">
        <Skeleton className="h-10 w-48" />
        <Skeleton className="h-40 w-full rounded-[20px]" />
        <Skeleton className="h-56 w-full rounded-[20px]" />
      </div>
    );
  }

  if (settings.isError || !settings.data) {
    return <ErrorState message={errorMessage(settings.error)} onRetry={() => void settings.refetch()} />;
  }

  const s = settings.data;
  const connection = gmail.data;

  return (
    <div className="mx-auto max-w-2xl">
      <PageHeader title="Settings" />

      <div className="space-y-8">
        {user && (
          <div className="card flex items-center gap-4 p-5">
            <span aria-hidden="true" className="flex size-12 items-center justify-center rounded-full bg-fill-strong text-lg font-semibold">
              {user.name.slice(0, 1).toUpperCase()}
            </span>
            <div className="min-w-0 flex-1">
              <p className="truncate text-[1.0625rem] font-semibold">{user.name}</p>
              <p className="caption truncate">{isDemo ? 'Demo account with sample data' : user.email}</p>
            </div>
            <Button variant="secondary" size="sm" icon={<LogOut size={14} aria-hidden="true" />} loading={logout.isPending} onClick={() => logout.mutate()}>
              Sign out
            </Button>
          </div>
        )}

        {!isDemo && session.data?.capabilities.gmail && (
          <Group title="Gmail" footer="FinSight uses read-only access to find statement emails and download their PDFs. It never reads other email content into its database, sends email or changes your inbox.">
            {connection?.connected ? (
              <>
                <Row
                  label={connection.email ?? 'Connected account'}
                  detail={connection.status === 'expired' ? 'Connection expired. Reconnect to scan again.' : connection.lastSyncedAt ? `Last scanned ${formatRelativeTime(connection.lastSyncedAt)}` : 'Connected'}
                  control={
                    connection.status === 'expired' ? (
                      <a href="/api/gmail/connect" className="text-[0.9375rem] text-accent">
                        Reconnect
                      </a>
                    ) : (
                      <span className="caption">Read-only</span>
                    )
                  }
                />
                <ActionRow
                  label="Disconnect Gmail"
                  detail="Revokes access at Google. Imported statements stay until you delete them."
                  destructive
                  disabled={disconnect.isPending}
                  onClick={() => {
                    if (window.confirm('Disconnect Gmail? FinSight will no longer be able to find new statements.')) disconnect.mutate();
                  }}
                />
              </>
            ) : (
              <Row
                label="Not connected"
                detail="Connect to find statements automatically."
                control={
                  <a href="/api/gmail/connect" className="text-[0.9375rem] font-medium text-accent">
                    Connect
                  </a>
                }
              />
            )}
          </Group>
        )}

        <Group title="AI" footer={session.data?.capabilities.ai ? 'The AI receives totals, categories and merchant names. Never account numbers, email content or your identity.' : 'AI isn’t configured on this server, so these settings have no effect yet.'}>
          <Row
            label="AI categorization"
            detail="Suggests categories for merchants FinSight doesn’t recognise."
            control={<Switch label="AI categorization" checked={s.aiCategorizationEnabled} onChange={(v) => change({ aiCategorizationEnabled: v })} />}
          />
          <Row
            label="AI insights"
            detail="Plain-language analysis of your spending."
            control={<Switch label="AI insights" checked={s.aiInsightsEnabled} onChange={(v) => change({ aiInsightsEnabled: v })} />}
          />
        </Group>

        <Group title="Preferences">
          <Row
            label="Currency"
            htmlFor="currency"
            detail="Amounts are shown in this currency. Nothing is converted."
            control={
              <select id="currency" value={s.currency} onChange={(e) => change({ currency: e.target.value })} className="h-9 rounded-lg bg-fill px-2.5 text-[0.9375rem]">
                {['CAD', 'USD', 'EUR', 'GBP'].map((c) => (
                  <option key={c}>{c}</option>
                ))}
              </select>
            }
          />
          <Row
            label="Date format"
            htmlFor="date-format"
            control={
              <select id="date-format" value={s.dateFormat} onChange={(e) => change({ dateFormat: e.target.value })} className="h-9 max-w-44 rounded-lg bg-fill px-2.5 text-[0.9375rem]">
                {DATE_FORMATS.map((f) => (
                  <option key={f} value={f}>
                    {formatDate(SAMPLE_DATE, f)}
                  </option>
                ))}
              </select>
            }
          />
          <div className="flex flex-col gap-3 px-4 py-3 sm:flex-row sm:items-center sm:justify-between md:px-5">
            <span className="text-[0.9375rem]">Appearance</span>
            <SegmentedControl
              label="Appearance"
              size="sm"
              value={s.theme}
              onChange={(theme) => change({ theme })}
              options={[
                { value: 'system', label: 'Automatic' },
                { value: 'light', label: 'Light' },
                { value: 'dark', label: 'Dark' },
              ]}
            />
          </div>
          <Row
            label="Notifications"
            detail="Email when new statements are found. Coming soon: your choice is saved, but nothing is sent yet."
            control={<Switch label="Notifications" checked={s.notificationsEnabled} onChange={(v) => change({ notificationsEnabled: v })} />}
          />
        </Group>

        {update.isError && (
          <p role="alert" className="px-4 text-[0.9375rem] text-critical">
            {errorMessage(update.error)}
          </p>
        )}

        <Group title="Your data" footer="Deleting is permanent. PDFs are never stored, so there are no files to remove.">
          <ActionRow
            label="Delete all transactions"
            detail="Statements found in Gmail can be analyzed again."
            destructive
            disabled={destroy.isPending}
            onClick={() => confirmDestroy('transactions', 'Delete every transaction? Gmail statements will return to “ready to analyze”.')}
          />
          <ActionRow label="Delete all statements" detail="Removes statements and their transactions." destructive disabled={destroy.isPending} onClick={() => confirmDestroy('statements', 'Delete every statement and all of their transactions?')} />
          <ActionRow
            label="Delete all financial data"
            detail="Statements, transactions, rules and insights. Disconnects Gmail."
            destructive
            disabled={destroy.isPending}
            onClick={() => confirmDestroy('all', 'Delete all of your financial data and disconnect Gmail? This can’t be undone.')}
          />
          <ActionRow label="Delete account" detail="Everything above, plus your FinSight account." destructive disabled={destroy.isPending} onClick={() => confirmDestroy('account', 'Delete your FinSight account and everything in it? This can’t be undone.')} />
        </Group>

        {destroy.isError && (
          <p role="alert" className="px-4 text-[0.9375rem] text-critical">
            {errorMessage(destroy.error)}
          </p>
        )}
      </div>
    </div>
  );
}
