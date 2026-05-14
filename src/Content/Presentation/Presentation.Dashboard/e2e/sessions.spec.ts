import { test, expect } from '@playwright/test';

test.describe('Sessions List Page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/sessions');
    await page.waitForSelector('[data-testid="session-table"]', { timeout: 15_000 });
  });

  test('renders KPI cards', async ({ page }) => {
    await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 20_000 });
    const kpis = page.locator('[data-testid^="kpi-"]');
    const count = await kpis.count();
    expect(count).toBeGreaterThanOrEqual(4);

    const totalSessions = page.locator('[data-testid="kpi-total-sessions"]');
    await expect(totalSessions).toBeVisible();
  });

  test('shows at least one session row from the echo agent', async ({ page }) => {
    const rows = page.locator('[data-testid^="session-row-"]');
    await expect(rows.first()).toBeVisible();

    const rowCount = await rows.count();
    expect(rowCount).toBeGreaterThan(0);
  });

  test('session table displays agent name and metrics', async ({ page }) => {
    const firstRow = page.locator('[data-testid^="session-row-"]').first();
    const cells = firstRow.locator('td');

    const agentName = await cells.nth(0).textContent();
    expect(agentName).toBeTruthy();

    const turns = await cells.nth(4).textContent();
    expect(Number(turns)).toBeGreaterThan(0);
  });

  test('session timeline panel renders', async ({ page }) => {
    const timeline = page.locator('[data-testid="panel-session-timeline"]');
    await expect(timeline).toBeVisible();
  });

  test('clicking a session row navigates to detail', async ({ page }) => {
    const firstRow = page.locator('[data-testid^="session-row-"]').first();
    await firstRow.click();

    await page.waitForURL(/\/sessions\/.+/);
    expect(page.url()).toMatch(/\/sessions\/.+/);
  });
});

test.describe('Session Detail Page', () => {
  test.beforeEach(async ({ page }) => {
    // Navigate to sessions list, then click into the first session
    await page.goto('/sessions');
    await page.waitForSelector('[data-testid="session-table"]', { timeout: 15_000 });
    const firstRow = page.locator('[data-testid^="session-row-"]').first();
    await firstRow.click();
    await page.waitForURL(/\/sessions\/.+/);
  });

  test('renders session identity with agent name and status', async ({ page }) => {
    const identity = page.locator('[data-testid="session-identity"]');
    await expect(identity).toBeVisible();

    // Agent name should be visible
    const heading = identity.locator('h2');
    await expect(heading).toBeVisible();
    const agentName = await heading.textContent();
    expect(agentName).toBeTruthy();
  });

  test('stat strip renders all metric categories', async ({ page }) => {
    const statStrip = page.locator('[data-testid="stat-strip"]');
    await expect(statStrip).toBeVisible();

    // All 7 stat categories should be present
    const expectedStats = ['turns', 'duration', 'tokens', 'cache-hit', 'tool-calls', 'cost', 'subagents'];
    for (const stat of expectedStats) {
      const cell = page.locator(`[data-testid="stat-${stat}"]`);
      await expect(cell).toBeVisible();
    }
  });

  test('turns stat shows non-zero value', async ({ page }) => {
    const turnsValue = page.locator('[data-testid="stat-turns-value"]');
    await expect(turnsValue).toBeVisible();

    const text = await turnsValue.textContent();
    expect(Number(text)).toBeGreaterThan(0);
  });

  test('tokens stat shows non-zero value', async ({ page }) => {
    const tokensValue = page.locator('[data-testid="stat-tokens-value"]');
    await expect(tokensValue).toBeVisible();

    const text = await tokensValue.textContent();
    // Tokens are formatted (e.g., "1.2k"), so check it's not literally "0"
    expect(text).not.toBe('0');
  });

  test('cost stat shows a dollar value', async ({ page }) => {
    const costValue = page.locator('[data-testid="stat-cost-value"]');
    await expect(costValue).toBeVisible();

    const text = await costValue.textContent();
    expect(text).toMatch(/\$/);
  });

  test('conversation timeline renders with messages', async ({ page }) => {
    const timeline = page.locator('[data-testid="conversation-timeline"]');
    await expect(timeline).toBeVisible();

    // Should have at least 2 messages (user + assistant from the echo agent seed)
    const messageRows = page.locator('[data-testid^="message-row-"]');
    const count = await messageRows.count();
    expect(count).toBeGreaterThanOrEqual(2);
  });

  test('conversation timeline shows both user and assistant messages', async ({ page }) => {
    const timeline = page.locator('[data-testid="conversation-timeline"]');
    await expect(timeline).toBeVisible();

    const userMessages = page.locator('[data-testid^="message-row-"][data-testid$="-user"]');
    const assistantMessages = page.locator('[data-testid^="message-row-"][data-testid$="-assistant"]');

    expect(await userMessages.count()).toBeGreaterThanOrEqual(1);
    expect(await assistantMessages.count()).toBeGreaterThanOrEqual(1);
  });

  test('multi-turn: all seeded messages appear in timeline', async ({ page }) => {
    // The global setup seeds 2 messages, which should produce at least 4 timeline entries
    // (2 user + 2 assistant). This test catches the bug where only the first turn is recorded.
    const messageRows = page.locator('[data-testid^="message-row-"]');
    const count = await messageRows.count();

    // Echo agent with 2 messages should produce at least 4 entries (2 user + 2 assistant)
    expect(count).toBeGreaterThanOrEqual(4);
  });

  test('cost waterfall renders turn rows', async ({ page }) => {
    const waterfall = page.locator('[data-testid="panel-cost-waterfall"]');
    const isVisible = await waterfall.isVisible().catch(() => false);
    if (!isVisible) {
      test.skip(true, 'No cost data in seeded session — cost waterfall not rendered');
      return;
    }

    const costRows = page.locator('[data-testid^="cost-row-"]');
    const rowCount = await costRows.count();
    expect(rowCount).toBeGreaterThanOrEqual(1);
  });

  test('back button returns to sessions list', async ({ page }) => {
    const backButton = page.locator('button', { hasText: '← sessions' });
    await expect(backButton).toBeVisible();
    await backButton.click();

    await page.waitForURL(/\/sessions$/);
  });
});
