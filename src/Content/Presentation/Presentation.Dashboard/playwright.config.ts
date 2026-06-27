import { defineConfig } from '@playwright/test';

const BASE_URL = process.env.DASHBOARD_URL ?? 'http://localhost:5174';
const API_URL = process.env.API_URL ?? 'http://localhost:52000';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  reporter: process.env.CI ? 'html' : 'list',
  timeout: 60_000,

  use: {
    baseURL: BASE_URL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },

  globalSetup: './e2e/global-setup.ts',

  projects: [
    {
      name: 'chromium',
      use: {
        browserName: 'chromium',
        viewport: { width: 1440, height: 900 },
      },
    },
  ],

  // Expect servers to already be running (started by npm run test:e2e)
  // webServer is not used here because AgentHub + Vite need to start together
  // via the dev:all script which handles the SPA proxy wiring.
});
