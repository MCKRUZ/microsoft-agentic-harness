import { test, expect, type Request } from '@playwright/test';

/**
 * Catches 404s on known API routes that the frontend attempts to call.
 * These fire during normal page load and user interactions — a 404 means
 * either the Vite proxy is misconfigured or the backend route is missing.
 */

test.describe('API route availability', () => {
  test('no 404 responses on app-initiated API requests during load', async ({ page }) => {
    const failedRequests: { url: string; status: number }[] = [];

    page.on('response', (response) => {
      const url = response.url();
      const isAppRoute =
        url.includes('/api/') ||
        url.includes('/ag-ui/') ||
        url.includes('/hubs/');

      if (isAppRoute && response.status() === 404) {
        failedRequests.push({ url, status: response.status() });
      }
    });

    await page.goto('/');
    await page.waitForLoadState('networkidle');

    expect(failedRequests).toEqual([]);
  });

  test('ag-ui/run endpoint is proxied (not served by Vite)', async ({ page }) => {
    const response = await page.request.post('/ag-ui/run', {
      data: { threadId: 'test', runId: 'test', messages: [{ role: 'user', content: 'hi' }] },
      headers: { 'Content-Type': 'application/json' },
    });

    // Should NOT be 404 (Vite fallback). Could be 401 (auth required) or
    // 502 (backend not running) — both prove the proxy is forwarding.
    expect(response.status()).not.toBe(404);
  });

  test('signalr hub negotiate endpoint is proxied', async ({ page }) => {
    const response = await page.request.post('/hubs/agent/negotiate', {
      headers: { 'Content-Type': 'application/json' },
    });

    // 401 or 200 means proxy works; 404 means Vite served it directly
    expect(response.status()).not.toBe(404);
  });
});
