import { defineConfig, configDefaults } from 'vitest/config'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // e2e/ holds Playwright specs. Vitest's default `include` matches **/*.spec.ts, so without this
    // it collects them, and they fail immediately on Playwright's `test`/`expect` fixtures — every
    // `npm test` run reported failures that had nothing to do with the unit suite.
    exclude: [...configDefaults.exclude, 'e2e/**'],
    environmentOptions: {
      jsdom: { url: 'http://localhost' },
    },
    typecheck: {
      tsconfig: './tsconfig.test.json',
    },
    coverage: {
      provider: 'v8',
      thresholds: { lines: 80, functions: 80, branches: 80, statements: 80 },
      exclude: ['src/test/**', 'src/components/ui/**', '**/*.d.ts', 'src/types/**'],
    },
  },
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
})
