import { test, expect } from '@playwright/test';

test.describe('Spend Hub', () => {
  test.describe('Tokens Page', () => {
    test.beforeEach(async ({ page }) => {
      await page.goto('/spend/tokens');
      await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 15_000 });
    });

    test('renders token KPI cards', async ({ page }) => {
      const kpis = page.locator('[data-testid^="kpi-"]');
      const count = await kpis.count();
      expect(count).toBeGreaterThanOrEqual(4);
    });

    test('input tokens KPI shows data', async ({ page }) => {
      const inputTokens = page.locator('[data-testid="kpi-input-tokens"]');
      await expect(inputTokens).toBeVisible();
    });

    test('output tokens KPI shows data', async ({ page }) => {
      const outputTokens = page.locator('[data-testid="kpi-output-tokens"]');
      await expect(outputTokens).toBeVisible();
    });
  });

  test.describe('Cost Page', () => {
    test.beforeEach(async ({ page }) => {
      await page.goto('/spend/cost');
      await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 15_000 });
    });

    test('renders cost KPI cards', async ({ page }) => {
      const kpis = page.locator('[data-testid^="kpi-"]');
      const count = await kpis.count();
      expect(count).toBeGreaterThanOrEqual(2);
    });

    test('total cost KPI is visible', async ({ page }) => {
      const totalCost = page.locator('[data-testid="kpi-total-cost"]');
      await expect(totalCost).toBeVisible();
    });
  });

  test.describe('Budget Page', () => {
    test.beforeEach(async ({ page }) => {
      await page.goto('/spend/budget');
      await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 15_000 });
    });

    test('renders budget KPI cards', async ({ page }) => {
      const kpis = page.locator('[data-testid^="kpi-"]');
      const count = await kpis.count();
      expect(count).toBeGreaterThanOrEqual(2);
    });
  });
});
