/**
 * Pipeline integration tests — verify the echo agent seeding in global-setup
 * produced real OTel metrics visible at every stage:
 *
 *   Agent execution → OTel Counter → Prometheus /metrics → Query API → Dashboard
 *
 * These tests catch metric name mismatches and missing counter increments.
 * Example: ToolExecutionMetrics.Invocations was never incremented by the handler,
 * so agentic_harness_agent_tool_invocations_total didn't exist in /metrics.
 */
import { test, expect } from '@playwright/test';
import { sumMetricValues, fetchMetricsText } from './helpers';

const API_URL = process.env.API_URL ?? 'http://localhost:52000';

test.describe('Stage 1 — OTel counters exported to /metrics', () => {
  let metricsText: string;

  test.beforeEach(async () => {
    metricsText = await fetchMetricsText();
  });

  test('orchestration turns_total > 0', () => {
    const val = sumMetricValues(metricsText, 'agentic_harness_agent_orchestration_turns_total');
    expect(val, 'Metric not found — OrchestrationMetrics.TurnsTotal never incremented').not.toBe(-1);
    expect(val).toBeGreaterThan(0);
  });

  test('orchestration tool_call_count_total > 0', () => {
    const val = sumMetricValues(metricsText, 'agentic_harness_agent_orchestration_tool_call_count_total');
    expect(val, 'Metric not found — OrchestrationMetrics.ToolCalls never incremented').not.toBe(-1);
    expect(val).toBeGreaterThan(0);
  });

  test('tool invocations_total > 0', () => {
    const val = sumMetricValues(metricsText, 'agentic_harness_agent_tool_invocations_total');
    expect(val, 'Metric not found — ToolExecutionMetrics.Invocations never incremented in handler').not.toBe(-1);
    expect(val).toBeGreaterThan(0);
  });

  test('session_started_total > 0', () => {
    const val = sumMetricValues(metricsText, 'agentic_harness_agent_session_started_total');
    expect(val, 'Metric not found — SessionMetrics.SessionsStarted never incremented').not.toBe(-1);
    expect(val).toBeGreaterThan(0);
  });

  test('session_active gauge exists', () => {
    const val = sumMetricValues(metricsText, 'agentic_harness_agent_session_active');
    expect(val, 'Metric not found — SessionMetrics.ActiveSessions never registered').not.toBe(-1);
  });

  test('tokens_input histogram has data', () => {
    const val = sumMetricValues(metricsText, 'agentic_harness_agent_tokens_input_count');
    expect(val, 'Metric not found — token input histogram not recording').not.toBe(-1);
    expect(val).toBeGreaterThan(0);
  });

  test('tokens_output histogram has data', () => {
    const val = sumMetricValues(metricsText, 'agentic_harness_agent_tokens_output_count');
    expect(val, 'Metric not found — token output histogram not recording').not.toBe(-1);
    expect(val).toBeGreaterThan(0);
  });

  test('cost_estimated_total exists', () => {
    const val = sumMetricValues(metricsText, 'agentic_harness_agent_tokens_cost_estimated_total');
    expect(val, 'Metric not found — cost estimation not recording').not.toBe(-1);
  });

  test('turn_duration histogram has data', () => {
    const val = sumMetricValues(metricsText, 'agentic_harness_agent_orchestration_turn_duration_count');
    expect(val, 'Metric not found — turn duration not recording').not.toBe(-1);
    expect(val).toBeGreaterThan(0);
  });
});

test.describe('Stage 2 — Prometheus query API returns seeded data', () => {
  async function queryValue(promql: string): Promise<number> {
    const res = await fetch(`${API_URL}/api/metrics/instant?query=${encodeURIComponent(promql)}`);
    if (!res.ok) throw new Error(`Query API returned ${res.status}`);
    const body = await res.json();
    if (!body.series?.length) return 0;
    const dp = body.series[0].dataPoints;
    return dp?.length ? Number(dp[dp.length - 1].value) : 0;
  }

  test('sessions started > 0', async () => {
    const val = await queryValue('sum(agentic_harness_agent_session_started_total) or vector(0)');
    expect(val).toBeGreaterThan(0);
  });

  test('turns total > 0', async () => {
    const val = await queryValue('sum(agentic_harness_agent_orchestration_turns_total) or vector(0)');
    expect(val).toBeGreaterThan(0);
  });

  test('tool invocations > 0', async () => {
    const val = await queryValue('sum(agentic_harness_agent_tool_invocations_total) or vector(0)');
    expect(val).toBeGreaterThan(0);
  });

  test('token input > 0', async () => {
    const val = await queryValue('sum(agentic_harness_agent_tokens_input_sum) or vector(0)');
    expect(val).toBeGreaterThan(0);
  });

  test('token output > 0', async () => {
    const val = await queryValue('sum(agentic_harness_agent_tokens_output_sum) or vector(0)');
    expect(val).toBeGreaterThan(0);
  });
});

test.describe('Stage 3 — Dashboard renders real metric values', () => {
  test('Sessions page Total Sessions KPI > 0', async ({ page }) => {
    await page.goto('/sessions');
    await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 20_000 });
    const el = page.locator('[data-testid="kpi-total-sessions-value"]');
    await expect(el).toBeVisible();
    const text = await el.textContent();
    const num = parseFloat((text ?? '0').replace(/[^0-9.]/g, ''));
    expect(num, `Total Sessions KPI shows "${text}" — expected > 0`).toBeGreaterThan(0);
  });

  test('Tokens page Input Tokens KPI > 0', async ({ page }) => {
    await page.goto('/spend/tokens');
    await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 20_000 });
    const el = page.locator('[data-testid="kpi-input-tokens-value"]');
    await expect(el).toBeVisible();
    const text = await el.textContent();
    const num = parseFloat((text ?? '0').replace(/[^0-9.]/g, ''));
    expect(num, `Input Tokens KPI shows "${text}" — expected > 0`).toBeGreaterThan(0);
  });

  test('Tokens page Output Tokens KPI > 0', async ({ page }) => {
    await page.goto('/spend/tokens');
    await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 20_000 });
    const el = page.locator('[data-testid="kpi-output-tokens-value"]');
    await expect(el).toBeVisible();
    const text = await el.textContent();
    const num = parseFloat((text ?? '0').replace(/[^0-9.]/g, ''));
    expect(num, `Output Tokens KPI shows "${text}" — expected > 0`).toBeGreaterThan(0);
  });

  test('Tools page Total Calls KPI > 0', async ({ page }) => {
    await page.goto('/quality/tools');
    await page.waitForSelector('[data-testid^="kpi-"]', { timeout: 20_000 });
    const el = page.locator('[data-testid="kpi-total-calls-value"]');
    await expect(el).toBeVisible();
    const text = await el.textContent();
    const num = parseFloat((text ?? '0').replace(/[^0-9.]/g, ''));
    expect(num, `Tool Calls KPI shows "${text}" — expected > 0`).toBeGreaterThan(0);
  });

  test('Pulse Activity tab By Agent panel has data', async ({ page }) => {
    await page.goto('/pulse');
    await page.waitForSelector('[data-testid^="kpi-"], [data-testid^="section-"]', { timeout: 20_000 });
    const activityBtn = page.locator('button').filter({ hasText: 'Activity' });
    await activityBtn.click();
    await page.waitForSelector('[data-testid^="panel-"]', { timeout: 15_000 });
    const byAgentPanel = page.locator('[data-testid="panel-by-agent"]');
    await expect(byAgentPanel).toBeVisible();
    const bars = byAgentPanel.locator('.font-mono');
    const count = await bars.count();
    expect(count, 'By Agent panel has no bars — sessions_started metric not reaching dashboard').toBeGreaterThan(0);
  });

  test('Pulse Activity tab Live Session Feed has entries', async ({ page }) => {
    await page.goto('/pulse');
    await page.waitForSelector('[data-testid^="kpi-"], [data-testid^="section-"]', { timeout: 20_000 });
    const activityBtn = page.locator('button').filter({ hasText: 'Activity' });
    await activityBtn.click();
    await page.waitForSelector('[data-testid^="panel-"]', { timeout: 15_000 });
    const feedPanel = page.locator('[data-testid="panel-live-session-feed"]');
    await expect(feedPanel).toBeVisible();
    const feedItems = feedPanel.locator('.font-mono');
    const count = await feedItems.count();
    expect(count, 'Live session feed is empty — sessions API returning no data').toBeGreaterThan(0);
  });

  test('Sessions page has at least one row with turns > 0', async ({ page }) => {
    await page.goto('/sessions');
    await page.waitForSelector('[data-testid="session-table"]', { timeout: 15_000 });
    const firstRow = page.locator('[data-testid^="session-row-"]').first();
    await expect(firstRow).toBeVisible();
    const turnsCell = firstRow.locator('td').nth(4);
    const turnsText = await turnsCell.textContent();
    expect(Number(turnsText), `Turns column shows "${turnsText}" — expected > 0`).toBeGreaterThan(0);
  });
});
