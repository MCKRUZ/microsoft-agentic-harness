import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';

export type ThemePreference = 'light' | 'dark' | 'system';
export type ResolvedTheme = 'light' | 'dark';

interface ThemeContextValue {
  theme: ResolvedTheme;
  preference: ThemePreference;
  resolvedTheme: ResolvedTheme;
  setTheme: (pref: ThemePreference) => void;
  toggleTheme: () => void;
}

export const ThemeContext = createContext<ThemeContextValue | undefined>(undefined);

const STORAGE_KEY = 'theme';

function getSystemTheme(): ResolvedTheme {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return 'light';
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function getInitialPreference(): ThemePreference {
  if (typeof localStorage === 'undefined') return 'system';
  const stored = localStorage.getItem(STORAGE_KEY);
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
    localStorage.setItem(STORAGE_KEY, preference);
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

export function useTheme(): ThemeContextValue {
  const context = useContext(ThemeContext);
  if (!context) {
    throw new Error('useTheme must be used within a ThemeProvider');
  }
  return context;
}
