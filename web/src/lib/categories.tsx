import {
  ArrowLeftRight,
  Banknote,
  Car,
  CircleEllipsis,
  Film,
  HeartPulse,
  Home,
  Landmark,
  type LucideIcon,
  Plane,
  ShoppingBag,
  Sparkles,
  UtensilsCrossed,
} from 'lucide-react';

interface GroupStyle {
  icon: LucideIcon;
  /** CSS variable for the group's chart colour. Fixed per group, never by rank. */
  color: string;
}

/**
 * Colour follows the category group, not its position in a list, so "Food" is the same colour on
 * every chart and every period. Groups beyond the seven validated series share a neutral grey.
 */
const GROUPS: Record<string, GroupStyle> = {
  housing: { icon: Home, color: 'var(--series-1)' },
  food: { icon: UtensilsCrossed, color: 'var(--series-2)' },
  transportation: { icon: Car, color: 'var(--series-3)' },
  shopping: { icon: ShoppingBag, color: 'var(--series-4)' },
  entertainment: { icon: Film, color: 'var(--series-5)' },
  health: { icon: HeartPulse, color: 'var(--series-6)' },
  travel: { icon: Plane, color: 'var(--series-7)' },
  personal: { icon: Sparkles, color: 'var(--series-neutral)' },
  financial: { icon: Landmark, color: 'var(--series-neutral)' },
  income: { icon: Banknote, color: 'var(--series-neutral)' },
  other: { icon: CircleEllipsis, color: 'var(--series-neutral)' },
};

const OTHER: GroupStyle = { icon: CircleEllipsis, color: 'var(--series-neutral)' };

export function groupStyle(groupId: string): GroupStyle {
  if (groupId === 'transfer') {
    return { icon: ArrowLeftRight, color: 'var(--series-neutral)' };
  }
  return GROUPS[groupId] ?? OTHER;
}

export function groupIdOf(categoryId: string): string {
  const parts = categoryId.split('.');
  return parts[0] === 'custom' ? (parts[1] ?? 'other') : (parts[0] ?? 'other');
}

export function CategoryGlyph({ groupId, type, size = 36 }: { groupId: string; type?: 'income' | 'expense' | 'transfer'; size?: number }) {
  const style = type === 'transfer' ? groupStyle('transfer') : groupStyle(groupId);
  const Icon = style.icon;
  const tint = type === 'income' ? 'var(--positive)' : style.color;

  return (
    <span
      aria-hidden="true"
      className="inline-flex shrink-0 items-center justify-center rounded-full"
      style={{ width: size, height: size, background: `color-mix(in srgb, ${tint} 14%, transparent)`, color: tint }}
    >
      <Icon size={Math.round(size * 0.47)} strokeWidth={2} />
    </span>
  );
}
