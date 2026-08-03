import { useEffect, useState, type ReactNode } from 'react';
import {
  ThemeContext,
  THEME_STORAGE_KEY,
  type ResolvedTheme,
  type ThemePreference,
} from './themeContext';

// Component-only module on purpose: the context, its types and the useTheme hook live in
// ./themeContext so Fast Refresh can hot-swap this provider instead of forcing a full reload.

function getSystemTheme(): ResolvedTheme {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return 'light';
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function getInitialPreference(): ThemePreference {
  if (typeof localStorage === 'undefined') return 'system';
  const stored = localStorage.getItem(THEME_STORAGE_KEY);
  if (stored === 'light' || stored === 'dark' || stored === 'system') return stored;
  return 'system';
}

interface ThemeProviderProps {
  children: ReactNode;
}

export function ThemeProvider({ children }: ThemeProviderProps) {
  const [preference, setPreference] = useState<ThemePreference>(getInitialPreference);
  const [systemTheme, setSystemTheme] = useState<ResolvedTheme>(getSystemTheme);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    const mql = window.matchMedia('(prefers-color-scheme: dark)');
    const onChange = (e: MediaQueryListEvent): void => {
      setSystemTheme(e.matches ? 'dark' : 'light');
    };
    mql.addEventListener('change', onChange);
    return () => { mql.removeEventListener('change', onChange); };
  }, []);

  const resolvedTheme: ResolvedTheme = preference === 'system' ? systemTheme : preference;

  useEffect(() => {
    document.documentElement.dataset['theme'] = resolvedTheme;
    localStorage.setItem(THEME_STORAGE_KEY, preference);
  }, [resolvedTheme, preference]);

  const setTheme = (pref: ThemePreference): void => { setPreference(pref); };
  const toggleTheme = (): void => { setPreference(resolvedTheme === 'light' ? 'dark' : 'light'); };

  return (
    <ThemeContext.Provider
      value={{ theme: resolvedTheme, preference, resolvedTheme, setTheme, toggleTheme }}
    >
      {children}
    </ThemeContext.Provider>
  );
}
