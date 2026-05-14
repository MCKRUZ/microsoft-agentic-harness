import { test, expect, type ConsoleMessage } from '@playwright/test';

/**
 * These tests catch DOM nesting violations and React warnings that only
 * manifest at runtime in the browser — unit tests with jsdom miss them.
 */

test.describe('DOM integrity', () => {
  let consoleErrors: ConsoleMessage[];
  let consoleWarnings: ConsoleMessage[];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    consoleWarnings = [];

    page.on('console', (msg) => {
      if (msg.type() === 'error') consoleErrors.push(msg);
      if (msg.type() === 'warning') consoleWarnings.push(msg);
    });

    await page.goto('/');
    await page.waitForLoadState('networkidle');
  });

  test('no nested interactive elements (button-in-button, a-in-a)', async ({ page }) => {
    const nestedButtons = await page.locator('button button').count();
    expect(nestedButtons).toBe(0);

    const nestedAnchors = await page.locator('a a').count();
    expect(nestedAnchors).toBe(0);

    const buttonInAnchor = await page.locator('a button').count();
    const anchorInButton = await page.locator('button a').count();
    expect(buttonInAnchor).toBe(0);
    expect(anchorInButton).toBe(0);
  });

  test('no React hydration or DOM nesting warnings in console', async () => {
    const nestingErrors = consoleErrors.filter((msg) => {
      const text = msg.text();
      return (
        text.includes('cannot be a descendant of') ||
        text.includes('hydration error') ||
        text.includes('Hydration failed')
      );
    });

    expect(nestingErrors).toHaveLength(0);
  });

  test('no unrecognized React props passed to DOM elements', async () => {
    const propWarnings = consoleErrors.filter((msg) => {
      const text = msg.text();
      return text.includes('does not recognize the') && text.includes('prop on a DOM element');
    });

    expect(propWarnings).toHaveLength(0);
  });

  test('no invalid HTML nesting detected by browser', async ({ page }) => {
    const violations = await page.evaluate(() => {
      const issues: string[] = [];
      // Check for buttons nested in buttons
      document.querySelectorAll('button').forEach((btn) => {
        if (btn.querySelector('button')) {
          issues.push(`Nested button found inside: ${btn.outerHTML.slice(0, 100)}`);
        }
      });
      // Check for interactive elements nested in labels
      document.querySelectorAll('label').forEach((label) => {
        const nested = label.querySelectorAll('label');
        if (nested.length > 0) {
          issues.push(`Nested label found inside: ${label.outerHTML.slice(0, 100)}`);
        }
      });
      return issues;
    });

    expect(violations).toEqual([]);
  });
});
