import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // Scope collection to src/. Vitest's default include is **/*.{test,spec}.*, which also matched the
    // Playwright specs in e2e/; those fail on sight because Playwright's `test`/`expect` fixtures are
    // not vitest's, so every `npm test` reported failures unrelated to the unit suite. Mirrors the
    // sibling Presentation.Dashboard config, which scopes the same way.
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
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
