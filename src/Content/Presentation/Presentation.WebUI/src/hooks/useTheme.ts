// Re-export kept so app code imports the hook from @/hooks like every other hook. The
// definition moved to the theme context module when ThemeProvider.tsx was made
// component-only for Fast Refresh.
export { useTheme } from '@/components/theme/themeContext';
