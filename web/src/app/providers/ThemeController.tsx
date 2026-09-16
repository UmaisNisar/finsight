import { useEffect } from 'react';
import { useSession, useSettings } from '@/api/queries';

/** Applies the theme from Settings. "System" removes the attribute so the OS preference applies. */
export function ThemeController() {
  const session = useSession();
  const settings = useSettings(session.data?.authenticated === true);
  const theme = settings.data?.theme ?? 'system';

  useEffect(() => {
    const root = document.documentElement;
    if (theme === 'system') {
      root.removeAttribute('data-theme');
    } else {
      root.setAttribute('data-theme', theme);
    }
  }, [theme]);

  return null;
}
