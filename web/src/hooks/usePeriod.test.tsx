import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import { PERIOD_STORAGE_KEY, usePeriod, usePeriodLink } from './usePeriod';
import { useRetained } from './useRetained';

function setup(route: string) {
  const wrapper = ({ children }: { children: ReactNode }) => <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>;
  return renderHook(() => ({ ...usePeriod(), link: usePeriodLink(), location: useLocation() }), { wrapper });
}

describe('usePeriod', () => {
  it('reads the period from the URL first', () => {
    localStorage.setItem(PERIOD_STORAGE_KEY, 'last-6-months');
    const { result } = setup('/?period=last-3-months');
    expect(result.current.period).toEqual({ preset: 'last-3-months' });
  });

  it('falls back to the remembered preset, then to last month', () => {
    localStorage.setItem(PERIOD_STORAGE_KEY, 'last-6-months');
    expect(setup('/').result.current.period).toEqual({ preset: 'last-6-months' });
    localStorage.setItem(PERIOD_STORAGE_KEY, 'not-a-period');
    expect(setup('/?period=bogus').result.current.period).toEqual({ preset: 'last-month' });
  });

  it('accepts a custom range only when it is complete and in order', () => {
    expect(setup('/?period=custom&from=2026-03-01&to=2026-05-31').result.current.period).toEqual({ preset: 'custom', from: '2026-03-01', to: '2026-05-31' });
    expect(setup('/?period=custom&from=2026-05-31&to=2026-03-01').result.current.period).toEqual({ preset: 'last-month' });
  });

  it('writes presets to the URL and remembers them, keeping other parameters', () => {
    const { result } = setup('/transactions?group=food&period=custom&from=2026-03-01&to=2026-05-31');
    act(() => result.current.setPeriod({ preset: 'this-month' }));
    expect(result.current.location.search).toBe('?group=food&period=this-month');
    expect(localStorage.getItem(PERIOD_STORAGE_KEY)).toBe('this-month');
  });

  it('puts custom ranges in the URL without remembering them', () => {
    localStorage.setItem(PERIOD_STORAGE_KEY, 'last-3-months');
    const { result } = setup('/');
    act(() => result.current.setPeriod({ preset: 'custom', from: '2026-01-01', to: '2026-01-31' }));
    expect(result.current.location.search).toBe('?period=custom&from=2026-01-01&to=2026-01-31');
    expect(localStorage.getItem(PERIOD_STORAGE_KEY)).toBe('last-3-months');
  });
});

describe('usePeriodLink', () => {
  it('carries only the period parameters onto links, merged with the link’s own query', () => {
    const { result } = setup('/insights?period=custom&from=2026-01-01&to=2026-01-31&type=income');
    expect(result.current.link('/transactions?group=food')).toBe('/transactions?group=food&period=custom&from=2026-01-01&to=2026-01-31');
    expect(setup('/').result.current.link('/recurring')).toBe('/recurring');
  });
});

describe('useRetained', () => {
  it('keeps the last value while closed and bumps the key on every opening', () => {
    const { result, rerender } = renderHook(({ value }: { value: string | null }) => useRetained(value), { initialProps: { value: null as string | null } });
    expect(result.current).toMatchObject({ item: null, open: false });

    rerender({ value: 'a' });
    const firstKey = result.current.key;
    expect(result.current).toMatchObject({ item: 'a', open: true });

    rerender({ value: null });
    expect(result.current).toMatchObject({ item: 'a', open: false, key: firstKey });

    rerender({ value: 'a' });
    expect(result.current.open).toBe(true);
    expect(result.current.key).not.toBe(firstKey);
  });
});
