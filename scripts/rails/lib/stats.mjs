// Small statistics and calendar helpers. One percentile definition everywhere: linear interpolation
// between closest ranks (numpy's default, Excel's PERCENTILE.INC), so median = percentile(50).

import { NO_DATA } from './review-parsers.mjs';

export const PERCENTILE_DEFINITION =
  'linear interpolation between closest ranks: rank = p/100 * (n-1) over ascending values (numpy default, Excel PERCENTILE.INC); median = p50';

const round = (value, places = 2) => Math.round(value * 10 ** places) / 10 ** places;

/** p in [0, 100]; values must be finite numbers. Empty → NO_DATA. */
export function percentile(values, p) {
  if (values.length === 0) return NO_DATA;
  const sorted = [...values].sort((a, b) => a - b);
  const rank = (p / 100) * (sorted.length - 1);
  const low = Math.floor(rank);
  const high = Math.ceil(rank);
  return round(sorted[low] + (sorted[high] - sorted[low]) * (rank - low));
}

/** { n, mean, median, p90, max } over numbers; NO_DATA when there are none. */
export function summarize(values) {
  if (values.length === 0) return NO_DATA;
  const total = values.reduce((sum, value) => sum + value, 0);
  return {
    n: values.length,
    mean: round(total / values.length),
    median: percentile(values, 50),
    p90: percentile(values, 90),
    max: Math.max(...values),
  };
}

/** numerator / denominator as a 4-place fraction; NO_DATA when the denominator is 0. */
export const share = (numerator, denominator) => (denominator === 0 ? NO_DATA : round(numerator / denominator, 4));

export function countBy(items, keyOf) {
  const counts = {};
  for (const item of items) {
    const key = keyOf(item);
    counts[key] = (counts[key] ?? 0) + 1;
  }
  return counts;
}

// ── ISO weeks ─────────────────────────────────────────────────────────────────────────────────────

/** ISO-8601 week label of a Date, e.g. '2026-W44' (UTC). */
export function isoWeek(date) {
  const day = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
  const weekday = day.getUTCDay() || 7;
  day.setUTCDate(day.getUTCDate() + 4 - weekday);
  const yearStart = new Date(Date.UTC(day.getUTCFullYear(), 0, 1));
  const week = Math.ceil(((day - yearStart) / 86400000 + 1) / 7);
  return `${day.getUTCFullYear()}-W${String(week).padStart(2, '0')}`;
}

/** Monday 00:00 UTC of the ISO week containing `date`. */
export function isoWeekStart(date) {
  const start = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
  start.setUTCDate(start.getUTCDate() - ((start.getUTCDay() || 7) - 1));
  return start;
}

/** The last complete ISO week before `now`: { since, until, label } with until exclusive. */
export function previousIsoWeekWindow(now) {
  const until = isoWeekStart(now);
  const since = new Date(until.getTime() - 7 * 86400000);
  return { since, until, label: isoWeek(since) };
}

/**
 * Week labels strictly between the latest existing file and `currentLabel` that have no file.
 * `existingLabels` are labels like '2026-W43'. With no history, nothing can be called missing.
 */
export function missingWeeks(existingLabels, currentLabel) {
  const prior = existingLabels.filter((label) => label < currentLabel).sort();
  if (prior.length === 0) return [];
  const missing = [];
  const have = new Set(prior);
  let cursor = weekStartFromLabel(prior[0]);
  const end = weekStartFromLabel(currentLabel);
  while (cursor < end) {
    const label = isoWeek(cursor);
    if (!have.has(label)) missing.push(label);
    cursor = new Date(cursor.getTime() + 7 * 86400000);
  }
  return missing;
}

/** Monday 00:00 UTC of the ISO week named by a label like '2026-W44'. */
export function weekStartFromLabel(label) {
  const [, year, week] = /^(\d{4})-W(\d{2})$/.exec(label).map(Number);
  const jan4 = new Date(Date.UTC(year, 0, 4));
  const week1 = isoWeekStart(jan4);
  return new Date(week1.getTime() + (week - 1) * 7 * 86400000);
}
