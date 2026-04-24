import { create } from 'zustand';

type Theme = 'light' | 'dark' | 'system';

interface ThemeState {
  theme: Theme;
  setTheme: (theme: Theme) => void;
}

function applyTheme(theme: Theme): void {
  const resolved = theme === 'system'
    ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
    : theme;
  document.documentElement.setAttribute('data-theme', resolved);
}

const storedTheme = (typeof localStorage !== 'undefined'
  ? localStorage.getItem('dashboard-theme') as Theme | null
  : null) ?? 'system';

applyTheme(storedTheme);

export const useThemeStore = create<ThemeState>((set) => ({
  theme: storedTheme,
  setTheme: (theme) => {
    localStorage.setItem('dashboard-theme', theme);
    applyTheme(theme);
    set({ theme });
  },
}));
