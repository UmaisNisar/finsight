import { Check, ChevronsUpDown, Search } from 'lucide-react';
import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { cn } from '@/lib/cn';

export interface PopUpOption<T extends string> {
  value: T;
  label: string;
  /** Stays listed whatever is typed in the search field, like "Other bank". */
  alwaysShown?: boolean;
}

export interface PopUpSection<T extends string> {
  title: string;
  options: PopUpOption<T>[];
}

interface Props<T extends string> {
  value: T;
  onChange: (value: T) => void;
  /** Flat options, or titled sections like a macOS menu with headers. */
  options: PopUpOption<T>[] | PopUpSection<T>[];
  /** Accessible name when no visible label exists. */
  label?: string;
  /** Id of a visible label element. */
  labelledBy?: string;
  /** `inline` sits at the end of a settings row; `field` fills a form column. */
  variant?: 'inline' | 'field';
  disabled?: boolean;
  className?: string;
  placeholder?: ReactNode;
  /** Adds a search field at the top of the menu, for long lists. */
  searchable?: boolean;
  searchPlaceholder?: string;
  /** Shown when nothing matches the search. */
  noMatchesText?: string;
}

function isSections<T extends string>(options: PopUpOption<T>[] | PopUpSection<T>[]): options is PopUpSection<T>[] {
  return options.length > 0 && 'options' in (options[0] as object);
}

/** Options matching a lowercase search. A match on a section title keeps the whole section, so "food" lists every Food category. */
function filterSections<T extends string>(sections: PopUpSection<T>[], needle: string): PopUpSection<T>[] {
  if (!needle) return sections;
  return sections
    .map((s) => (s.title.toLowerCase().includes(needle) ? s : { ...s, options: s.options.filter((o) => o.alwaysShown || o.label.toLowerCase().includes(needle)) }))
    .filter((s) => s.options.length > 0);
}

const MENU_GAP = 6;
const EDGE = 12;
const MAX_HEIGHT = 420;

/**
 * An Apple-style pop-up button: a glass capsule showing the current choice, opening a glass menu with a
 * checkmark beside the selected item. The menu uses the Popover API so it renders in the top layer, above
 * sheets and outside any scroll clipping, and implements the ARIA listbox pattern with typeahead.
 */
export function PopUpButton<T extends string>({ value, onChange, options, label, labelledBy, variant = 'inline', disabled, className, placeholder, searchable = false, searchPlaceholder = 'Search', noMatchesText = 'No matches' }: Props<T>) {
  const id = useId();
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const typeahead = useRef({ text: '', timer: 0 });
  const [open, setOpen] = useState(false);
  const openRef = useRef(false);
  const dismissedOnPress = useRef(false);
  const [active, setActive] = useState<T>(value);
  const [search, setSearch] = useState('');
  const searchRef = useRef<HTMLInputElement>(null);

  const allSections: PopUpSection<T>[] = isSections(options) ? options : [{ title: '', options }];
  const selected = allSections.flatMap((s) => s.options).find((o) => o.value === value);
  const needle = search.trim().toLowerCase();
  const sections = filterSections(allSections, needle);
  const flat = sections.flatMap((s) => s.options);
  const optionId = (v: string) => `${id}-opt-${v.replace(/[^\w-]/g, '_')}`;

  /** The toggle event that updates openRef is async, so also ask the DOM, which knows immediately. */
  function isShowing(menu: HTMLElement): boolean {
    try {
      return openRef.current || menu.matches(':popover-open');
    } catch {
      return openRef.current; // Environments without the :popover-open selector.
    }
  }

  /** Scrolls the menu itself (never the page or a sheet) so an option is visible. */
  function reveal(v: T, align: 'nearest' | 'center') {
    const menu = searchable ? listRef.current : menuRef.current;
    const option = document.getElementById(optionId(v));
    if (!menu || !option) return;
    const top = option.offsetTop - (searchable ? menu.offsetTop : 0);
    const bottom = top + option.offsetHeight;
    if (align === 'center') {
      menu.scrollTop = top - (menu.clientHeight - option.offsetHeight) / 2;
    } else if (top < menu.scrollTop + 6) {
      menu.scrollTop = top - 6;
    } else if (bottom > menu.scrollTop + menu.clientHeight - 6) {
      menu.scrollTop = bottom - menu.clientHeight + 6;
    }
  }

  /** Below the button by default, above when there isn't room, clamped to the viewport; grows from the button. */
  function position(menu: HTMLElement, button: HTMLElement) {
    const rect = button.getBoundingClientRect();
    menu.style.minWidth = `${Math.max(rect.width, 220)}px`;
    menu.style.maxHeight = 'none';
    const natural = menu.scrollHeight;
    const below = window.innerHeight - rect.bottom - MENU_GAP - EDGE;
    const above = rect.top - MENU_GAP - EDGE;
    const placeAbove = natural > below && above > below;
    const room = Math.min(placeAbove ? above : below, MAX_HEIGHT);
    const height = Math.min(natural, room);
    menu.style.maxHeight = `${room}px`;

    const menuWidth = menu.offsetWidth;
    const preferredLeft = variant === 'inline' ? rect.right - menuWidth : rect.left;
    const left = Math.min(Math.max(EDGE, preferredLeft), window.innerWidth - menuWidth - EDGE);
    menu.style.left = `${left}px`;
    // A searchable menu shrinks as its list filters, so a menu above the button is pinned by its bottom edge and
    // always stays next to the button.
    if (searchable && placeAbove) {
      menu.style.top = 'auto';
      menu.style.bottom = `${window.innerHeight - rect.top + MENU_GAP}px`;
    } else {
      menu.style.bottom = 'auto';
      menu.style.top = `${placeAbove ? rect.top - MENU_GAP - height : rect.bottom + MENU_GAP}px`;
    }
    const originX = Math.min(Math.max(rect.left + rect.width / 2 - left, 0), menuWidth);
    menu.style.transformOrigin = `${originX}px ${placeAbove ? '100%' : '0'}`;
  }

  /**
   * Opens the menu already in place: showPopover, then measure and position in the same task, so the first
   * painted frame is at the button and the scale-in grows from it. (Opening via popovertarget painted the
   * menu at its old position for a frame before the async toggle event let it move.)
   */
  function show() {
    const menu = menuRef.current;
    const button = buttonRef.current;
    if (disabled || !menu || !button || isShowing(menu)) return;
    setActive(value);
    setSearch('');
    menu.showPopover();
    position(menu, button);
    reveal(value, 'center');
    (searchable ? searchRef.current : listRef.current)?.focus({ preventScroll: true });
  }

  function hide(refocus = true) {
    const menu = menuRef.current;
    if (menu && isShowing(menu)) menu.hidePopover();
    if (refocus) buttonRef.current?.focus({ preventScroll: true });
  }

  function choose(next: T) {
    onChange(next);
    hide();
  }

  // Popover "auto" handles light dismiss and Escape; mirror its state so aria-expanded stays true to the UI.
  useEffect(() => {
    const menu = menuRef.current;
    if (!menu) return;
    const onToggle = (event: Event) => {
      const isOpen = (event as ToggleEvent).newState === 'open';
      openRef.current = isOpen;
      setOpen(isOpen);
      if (!isOpen && menu.contains(document.activeElement)) buttonRef.current?.focus({ preventScroll: true });
    };
    menu.addEventListener('toggle', onToggle);
    return () => menu.removeEventListener('toggle', onToggle);
  }, []);

  // Menus don't follow the page; close if anything outside them scrolls or the window resizes.
  useEffect(() => {
    const menu = menuRef.current;
    if (!open || !menu) return;
    const close = (event: Event) => {
      if (event.type === 'scroll' && menu.contains(event.target as Node)) return;
      if (isShowing(menu)) menu.hidePopover();
    };
    window.addEventListener('scroll', close, true);
    window.addEventListener('resize', close);
    return () => {
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('resize', close);
    };
  }, [open]);

  function onButtonKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      show();
    }
  }

  function onListKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const index = flat.findIndex((o) => o.value === active);
    const move = (next: number) => {
      const option = flat[Math.max(0, Math.min(flat.length - 1, next))];
      if (!option) return;
      setActive(option.value);
      reveal(option.value, 'nearest');
    };

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        move(index + 1);
        return;
      case 'ArrowUp':
        event.preventDefault();
        move(index - 1);
        return;
      case 'Home':
        event.preventDefault();
        move(0);
        return;
      case 'End':
        event.preventDefault();
        move(flat.length - 1);
        return;
      case 'Enter':
        event.preventDefault();
        if (flat.some((o) => o.value === active)) choose(active);
        return;
      case ' ':
        if (searchable) return;
        event.preventDefault();
        choose(active);
        return;
      case 'Tab':
        hide();
        return;
    }

    if (!searchable && event.key.length === 1 && !event.metaKey && !event.ctrlKey && !event.altKey) {
      const state = typeahead.current;
      window.clearTimeout(state.timer);
      state.text += event.key.toLowerCase();
      state.timer = window.setTimeout(() => (state.text = ''), 600);
      const match = flat.find((o) => o.label.toLowerCase().startsWith(state.text));
      if (match) {
        setActive(match.value);
        reveal(match.value, 'nearest');
      }
    }
  }

  return (
    <>
      <button
        ref={buttonRef}
        type="button"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={`${id}-menu`}
        aria-label={label}
        aria-labelledby={labelledBy ? `${labelledBy} ${id}-value` : undefined}
        disabled={disabled}
        // Pressing the button while the menu is open light-dismisses it first; remember that so the click
        // that follows doesn't reopen it.
        onPointerDown={() => {
          dismissedOnPress.current = menuRef.current ? isShowing(menuRef.current) : false;
        }}
        onClick={() => {
          if (dismissedOnPress.current) {
            dismissedOnPress.current = false;
            hide(false);
            return;
          }
          show();
        }}
        onKeyDown={onButtonKeyDown}
        className={cn(
          'glass-control inline-flex min-w-0 items-center gap-1.5 rounded-full text-[0.9375rem] text-label transition-[background-color,transform] duration-200 hover:bg-fill active:scale-[0.97] disabled:opacity-45 disabled:active:scale-100',
          variant === 'field' ? 'h-11 w-full justify-between pr-3 pl-4' : 'h-9 max-w-[16rem] pr-2.5 pl-3.5',
          className,
        )}
      >
        <span id={`${id}-value`} className="truncate">
          {selected?.label ?? placeholder}
        </span>
        <ChevronsUpDown size={15} strokeWidth={2.2} className="shrink-0 text-label-secondary" aria-hidden="true" />
      </button>

      <div
        ref={menuRef}
        id={`${id}-menu`}
        popover="auto"
        className={cn('popup-menu fixed m-0 overscroll-contain rounded-[18px] p-1.5 text-label', searchable && 'flex-col overflow-hidden [&:popover-open]:flex')}
      >
        {searchable && (
          <label className="mb-1.5 flex h-9 shrink-0 items-center gap-2 rounded-[10px] bg-fill px-2.5">
            <Search size={15} className="shrink-0 text-label-secondary" aria-hidden="true" />
            <span className="sr-only">{searchPlaceholder}</span>
            <input
              ref={searchRef}
              type="text"
              role="combobox"
              aria-expanded={open}
              aria-controls={`${id}-list`}
              aria-autocomplete="list"
              aria-activedescendant={open && flat.some((o) => o.value === active) ? optionId(active) : undefined}
              autoComplete="off"
              spellCheck={false}
              value={search}
              placeholder={searchPlaceholder}
              onChange={(event) => {
                const next = event.target.value;
                setSearch(next);
                const first = filterSections(allSections, next.trim().toLowerCase())[0]?.options[0];
                if (first) setActive(first.value);
              }}
              onKeyDown={onListKeyDown}
              className="min-w-0 flex-1 bg-transparent text-[0.9375rem] text-label outline-none placeholder:text-label-tertiary"
            />
          </label>
        )}
        <div
          ref={listRef}
          id={`${id}-list`}
          role="listbox"
          tabIndex={-1}
          aria-label={label}
          aria-labelledby={labelledBy}
          aria-activedescendant={open && !searchable ? optionId(active) : undefined}
          onKeyDown={onListKeyDown}
          className={cn('outline-none', searchable && 'relative min-h-0 flex-1 overflow-y-auto overscroll-contain')}
        >
          {flat.length === 0 && <div className="px-3 py-2 text-[0.875rem] text-label-secondary">{noMatchesText}</div>}
          {sections.map((section, sectionIndex) => (
            <div
              key={section.title || sectionIndex}
              role={section.title ? 'group' : 'presentation'}
              aria-labelledby={section.title ? `${id}-sec-${sectionIndex}` : undefined}
              className={cn(sectionIndex > 0 && 'mt-1.5 border-t border-separator pt-1.5')}
            >
              {section.title && (
                <div id={`${id}-sec-${sectionIndex}`} role="presentation" className="px-3 pt-1 pb-1 text-[0.75rem] font-semibold text-label-secondary">
                  {section.title}
                </div>
              )}
              {section.options.map((option) => {
                const isSelected = option.value === value;
                const isActive = option.value === active;
                return (
                  // Keyboard interaction lives on the listbox (aria-activedescendant); options only need pointer handling.
                  // eslint-disable-next-line jsx-a11y/click-events-have-key-events
                  <div
                    key={option.value}
                    id={optionId(option.value)}
                    role="option"
                    aria-selected={isSelected}
                    tabIndex={-1}
                    onPointerMove={() => setActive(option.value)}
                    onClick={() => choose(option.value)}
                    className={cn(
                      'flex h-9 cursor-default items-center gap-2 rounded-[10px] pr-4 pl-2 text-[0.9375rem] whitespace-nowrap select-none',
                      isActive ? 'bg-accent text-accent-contrast' : 'text-label',
                    )}
                  >
                    <span className="flex w-5 shrink-0 justify-center">{isSelected && <Check size={15} strokeWidth={2.75} aria-hidden="true" />}</span>
                    {option.label}
                  </div>
                );
              })}
            </div>
          ))}
        </div>
      </div>
    </>
  );
}
