import { ChevronRight, FileText, Settings } from 'lucide-react';
import { Link } from 'react-router';
import { PageHeader } from '@/components/PageHeader';

const LINKS = [
  { to: '/statements', label: 'Statements', detail: 'Gmail, uploads and processing', icon: FileText },
  { to: '/settings', label: 'Settings', detail: 'Account, AI, preferences and data', icon: Settings },
];

/** Overflow destinations for the phone tab bar. */
export default function MorePage() {
  return (
    <div>
      <PageHeader title="More" />
      <ul className="card overflow-hidden [&>li+li]:shadow-[inset_0_0.5px_0_var(--separator)]">
        {LINKS.map(({ to, label, detail, icon: Icon }) => (
          <li key={to}>
            <Link to={to} className="flex items-center gap-3.5 px-4 py-3.5 transition-colors hover:bg-fill">
              <span className="flex size-9 items-center justify-center rounded-[10px] bg-accent text-white" aria-hidden="true">
                <Icon size={18} />
              </span>
              <span className="min-w-0 flex-1">
                <span className="block text-[0.9375rem] font-medium">{label}</span>
                <span className="caption block">{detail}</span>
              </span>
              <ChevronRight size={17} className="text-label-tertiary" aria-hidden="true" />
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}
