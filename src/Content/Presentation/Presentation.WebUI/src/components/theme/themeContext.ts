import { createContext, useContext } from 'react';

/**
 * Context, types and consumer hook for the theme system, kept OUT of ThemeProvider.tsx.
 *
 * Fast Refresh only preserves component state for modules that export components and nothing
 * else. With the context and `useTheme` living beside the provider, every theme change edited
 * a module with mixed exports and forced a full reload instead of a hot swap. Splitting the
 * non-component exports here leaves ThemeProvider.tsx component-only.
 *
 * This file deliberately has no `.tsx` extension and renders nothing — it is types and a hook.
 */

export type ThemePreference = 'light' | 'dark' | 'system';
export type ResolvedTheme = 'light' | 'dark';

export interface ThemeContextValue {
  theme: ResolvedTheme;
  preference: ThemePreference;
  resolvedTheme: ResolvedTheme;
  setTheme: (pref: ThemePreference) => void;
  toggleTheme: () => void;
}

export const ThemeContext = createContext<ThemeContextValue | undefined>(undefined);

/** Storage key for the persisted preference. Shared with ThemeProvider. */
export const THEME_STORAGE_KEY = 'theme';

export function useTheme(): ThemeContextValue {
  const context = useContext(ThemeContext);
  if (!context) {
    throw new Error('useTheme must be used within a ThemeProvider');
  }
  return context;
}
