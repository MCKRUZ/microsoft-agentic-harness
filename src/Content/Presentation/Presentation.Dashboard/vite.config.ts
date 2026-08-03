// `vitest/config`, not `vite` — this file carries an inline `test:` block, and vite's own
// defineConfig has no such property. Vitest re-exports defineConfig with the config type
// widened to include it. The mismatch was invisible while the typecheck was a no-op.
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  server: {
    port: 5174,
    proxy: {
      '/api': { target: 'http://localhost:52000', changeOrigin: true },
      '/hubs': { target: 'http://localhost:52000', ws: true, changeOrigin: true },
      // AG-UI streaming endpoint. The agent panel POSTs to /ag-ui/run and reads an SSE
      // response; the proxy must not buffer it (the backend sets X-Accel-Buffering: no
      // and flushes each frame), so streamed tool-call and text events arrive live.
      '/ag-ui': { target: 'http://localhost:52000', changeOrigin: true },
    },
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    css: true,
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
  },
})
