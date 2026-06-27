import type { Page } from '@playwright/test';

const API_URL = process.env.API_URL ?? 'http://localhost:52000';

/**
 * Parses the Prometheus text exposition format and returns the sum of all
 * series values for the given metric name (aggregates across label sets).
 * Returns -1 if the metric is not found at all (distinguishes "absent" from "zero").
 */
export function sumMetricValues(text: string, metricName: string): number {
  let total = 0;
  let found = false;
  for (const line of text.split('\n')) {
    if (line.startsWith('#') || line.trim() === '') continue;
    if (!line.startsWith(metricName)) continue;
    const ch = line.charAt(metricName.length);
    if (ch !== '{' && ch !== ' ') continue;
    found = true;
    const match = line.match(/\}\s+([\d.eE+-]+)/) ?? line.match(/^\S+\s+([\d.eE+-]+)/);
    if (match) total += parseFloat(match[1]);
  }
  return found ? total : -1;
}

/**
 * Fetches the raw Prometheus /metrics endpoint from the AgentHub.
 */
export async function fetchMetricsText(): Promise<string> {
  const res = await fetch(`${API_URL}/metrics`);
  if (!res.ok) throw new Error(`/metrics returned ${res.status}`);
  return res.text();
}

/**
 * Extracts the numeric value from a KPI card's value element.
 * Handles formatted strings like "1.2K", "$0.05", "42", "0.0".
 * Returns 0 for unparseable or missing values.
 */
export async function getKpiValue(page: Page, testId: string): Promise<number> {
  const el = page.locator(`[data-testid="${testId}-value"]`);
  const text = await el.textContent({ timeout: 20_000 });
  if (!text) return 0;
  const cleaned = text.replace(/[^0-9.-]/g, '');
  return parseFloat(cleaned) || 0;
}
