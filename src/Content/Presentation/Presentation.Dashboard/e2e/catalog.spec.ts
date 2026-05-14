import { test, expect } from '@playwright/test';

test.describe('Catalog Page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/catalog');
    await page.waitForSelector('h1', { timeout: 10_000 });
  });

  test('renders the page header', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('Catalog');
  });

  test('renders tab navigation with Models, Agents, Tools', async ({ page }) => {
    await expect(page.locator('button').filter({ hasText: 'Models' })).toBeVisible();
    await expect(page.locator('button').filter({ hasText: 'Agents' })).toBeVisible();
    await expect(page.locator('button').filter({ hasText: 'Tools' })).toBeVisible();
  });

  test('displays tab content after loading', async ({ page }) => {
    // Wait for loading skeletons to disappear
    await page.waitForFunction(
      () => !document.querySelector('.animate-pulse'),
      { timeout: 15_000 },
    );
    // Each tab renders either data cards or an EmptyState with a title
    // The Models tab shows "No model data" when Prometheus has no model-labeled metrics
    const hasContent = await page.locator('h3, h2, [class*="font-semibold"]').count();
    expect(hasContent).toBeGreaterThan(0);
  });
});
