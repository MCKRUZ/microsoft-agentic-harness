import { test, expect } from '@playwright/test';

/**
 * Validates that interactive component composition follows HTML spec rules.
 * These are the kinds of bugs that only surface as browser warnings but
 * cause real accessibility and behavior issues.
 */

test.describe('Accessibility - interactive element nesting', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
  });

  test('tooltip triggers do not create duplicate interactive wrappers', async ({ page }) => {
    const tooltipTriggers = page.locator('[data-slot="tooltip-trigger"]');
    const count = await tooltipTriggers.count();

    for (let i = 0; i < count; i++) {
      const trigger = tooltipTriggers.nth(i);
      const tagName = await trigger.evaluate((el) => el.tagName.toLowerCase());

      if (tagName === 'button') {
        const nestedButtons = await trigger.locator('button').count();
        expect(nestedButtons).toBe(0);
      }
    }
  });

  test('all buttons have accessible names', async ({ page }) => {
    const buttons = page.locator('button');
    const count = await buttons.count();

    const unlabeled: string[] = [];
    for (let i = 0; i < count; i++) {
      const btn = buttons.nth(i);
      const visible = await btn.isVisible();
      if (!visible) continue;

      const ariaLabel = await btn.getAttribute('aria-label');
      const ariaLabelledBy = await btn.getAttribute('aria-labelledby');
      const textContent = (await btn.textContent())?.trim();
      const title = await btn.getAttribute('title');

      if (!ariaLabel && !ariaLabelledBy && !textContent && !title) {
        const html = await btn.evaluate((el) => el.outerHTML.slice(0, 120));
        unlabeled.push(html);
      }
    }

    expect(unlabeled).toEqual([]);
  });

  test('form controls are not nested inside other form controls', async ({ page }) => {
    const violations = await page.evaluate(() => {
      const issues: string[] = [];
      const interactiveSelectors = 'button, input, select, textarea, [role="button"]';

      document.querySelectorAll(interactiveSelectors).forEach((el) => {
        const parent = el.parentElement?.closest(interactiveSelectors);
        if (parent && parent !== el) {
          issues.push(
            `<${el.tagName.toLowerCase()}> nested inside <${parent.tagName.toLowerCase()}>: ${el.outerHTML.slice(0, 80)}`
          );
        }
      });

      return issues;
    });

    expect(violations).toEqual([]);
  });
});
