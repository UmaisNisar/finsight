import { Check, CircleAlert } from 'lucide-react';
import type { Statement } from '@/api/schemas';
import { Pill } from '@/components/ui/primitives';

export function StatusPill({ status }: { status: Statement['status'] }) {
  switch (status) {
    case 'processed':
      return (
        <Pill tone="positive" icon={<Check size={12} strokeWidth={3} aria-hidden="true" />}>
          Processed
        </Pill>
      );
    case 'failed':
      return (
        <Pill tone="critical" icon={<CircleAlert size={12} strokeWidth={2.5} aria-hidden="true" />}>
          Needs attention
        </Pill>
      );
    case 'downloading':
    case 'processing':
      return (
        <Pill tone="accent" icon={<span className="size-2.5 animate-spin rounded-full border-[1.5px] border-accent/30 border-t-accent" aria-hidden="true" />}>
          {status === 'downloading' ? 'Downloading' : 'Processing'}
        </Pill>
      );
    default:
      return <Pill>Not analyzed</Pill>;
  }
}
