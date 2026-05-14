import { test, expect } from '@playwright/test';

test.describe('Pulse Page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/pulse');
    await page.waitForSelector('h1', { timeout: 10_000 });
  });

  test('renders the page header', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('Pulse');
  });

  test('displays tab navigation', async ({ page }) => {
    const tabs = page.locator('button').filter({ hasText: 'Health' });
    await expect(tabs).toBeVisible();
  });

  test('health tab renders KPI cards after loading', async ({ page }) => {
    await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 20_000 });
    const kpiCards = page.locator('[data-testid^="kpi-"]');
    const count = await kpiCards.count();
    expect(count).toBeGreaterThanOrEqual(4);
  });

  test('can switch between tabs', async ({ page }) => {
    // Wait for Health tab to finish loading first
    await page.waitForSelector('[data-testid^="kpi-"], [data-testid^="section-"]', { timeout: 20_000 });
    // TabNav buttons contain a div with the label text — click the button
    const activityButton = page.locator('button').filter({ hasText: 'Activity' });
    await activityButton.click();
    // Activity tab renders sections or panels
    await expect(
      page.locator('[data-testid^="kpi-"], [data-testid^="panel-"], [data-testid^="section-"]').first()
    ).toBeVisible({ timeout: 15_000 });
  });
});
