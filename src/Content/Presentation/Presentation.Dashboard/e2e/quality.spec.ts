import { test, expect } from '@playwright/test';

test.describe('Quality Hub', () => {
  test.describe('Tools Page', () => {
    test.beforeEach(async ({ page }) => {
      await page.goto('/quality/tools');
      await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 15_000 });
    });

    test('renders tool KPI cards', async ({ page }) => {
      const kpis = page.locator('[data-testid^="kpi-"]');
      const count = await kpis.count();
      expect(count).toBeGreaterThanOrEqual(2);
    });

    test('total tool calls KPI shows data from echo agent', async ({ page }) => {
      const totalCalls = page.locator('[data-testid="kpi-total-calls"]');
      await expect(totalCalls).toBeVisible();
    });
  });

  test.describe('Safety Page', () => {
    test.beforeEach(async ({ page }) => {
      await page.goto('/quality/safety');
      await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 15_000 });
    });

    test('renders safety KPI cards', async ({ page }) => {
      const kpis = page.locator('[data-testid^="kpi-"]');
      const count = await kpis.count();
      expect(count).toBeGreaterThanOrEqual(2);
    });
  });

  test.describe('RAG Page', () => {
    test.beforeEach(async ({ page }) => {
      await page.goto('/quality/rag');
      await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 15_000 });
    });

    test('renders RAG KPI cards', async ({ page }) => {
      const kpis = page.locator('[data-testid^="kpi-"]');
      const count = await kpis.count();
      expect(count).toBeGreaterThanOrEqual(2);
    });
  });
});
