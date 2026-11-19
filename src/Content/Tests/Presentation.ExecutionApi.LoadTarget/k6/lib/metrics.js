// Custom metrics and response classification shared by every scenario.
//
// k6's built-in http_req_failed counts every 4xx as a failure, which is wrong for this API: a 429 from
// the rate limiter or a 400 from the per-owner run cap is the host working as designed. So every
// response is classified here instead, and thresholds are written against these counters.

import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

/** Any 5xx the contract does not document for that call. The one number that must always be zero. */
export const serverErrors = new Counter('server_errors');

/** 429s from the rate limiter. Expected only where a scenario deliberately exceeds a limit. */
export const rateLimited = new Counter('rate_limited');

/** Documented refusals that are not throttling: per-owner run cap (400), host-wide stream cap (503). */
export const capacityRefusals = new Counter('capacity_refusals');

/** Any status that is neither expected success nor a documented refusal (e.g. 401, 404, 409 where none should occur). */
export const unexpectedStatus = new Counter('unexpected_status');

/**
 * Runs that finished far faster than the model can answer: the chat response cache answered them, so they
 * measured the cache, not the harness. Must stay zero; a scenario that trips it is not measuring the host.
 */
export const cachedAnswers = new Counter('cached_answers');

/** Runs whose outcome was recorded. Lets a threshold insist a phase produced data at all. */
export const runsRecorded = new Counter('runs_recorded');

/** Share of runs that ended Succeeded with the conversation itself succeeding. */
export const runSucceeded = new Rate('run_succeeded');

/** Server-side: how long a background run sat in the dispatch queue (startedAt - createdAt). */
export const runQueueWait = new Trend('run_queue_wait', true);

/** Server-side: how long a run executed once started (completedAt - startedAt). */
export const runExecution = new Trend('run_execution', true);

/** Client-side: from starting a run until polling saw it finish. */
export const runEndToEnd = new Trend('run_end_to_end', true);

/** Time to first byte of a live stream: how long before the run visibly started for its caller. */
export const streamFirstByte = new Trend('stream_first_byte', true);

/** Whole live-stream duration: open to the final event. */
export const streamDuration = new Trend('stream_duration', true);

/**
 * Classifies one response and records it. Returns true when the status was one of `expected`.
 *
 * @param {object} res      k6 response
 * @param {string} name     operation name, used to tag the check
 * @param {number[]} expected  statuses that mean success for this call
 * @param {number[]} refusals  documented capacity refusals for this call (not errors, not success)
 */
export function classify(res, name, expected, refusals = []) {
  const status = res.status;
  if (status === 429) {
    rateLimited.add(1, { op: name });
  } else if (refusals.includes(status)) {
    capacityRefusals.add(1, { op: name, status: String(status) });
  } else if (status >= 500 || status === 0) {
    // status 0 is a transport failure: timeout, reset, refused connection. Treated as a server error,
    // because from the caller's side the host failed to answer.
    serverErrors.add(1, { op: name, status: String(status) });
  } else if (!expected.includes(status)) {
    unexpectedStatus.add(1, { op: name, status: String(status) });
  }

  const ok = expected.includes(status);
  check(res, { [`${name} ${expected.join('/')}`]: () => ok });
  return ok;
}

/** Milliseconds between two ISO-8601 timestamps from a run record; null when either is missing. */
export function between(fromIso, toIso) {
  if (!fromIso || !toIso) {
    return null;
  }
  const elapsed = parseIso(toIso) - parseIso(fromIso);
  return Number.isNaN(elapsed) ? null : elapsed;
}

// .NET writes seven fractional digits (22:28:06.2474122+00:00); ECMAScript's date format allows three,
// and k6's JavaScript runtime returns NaN for more. Truncating to milliseconds loses nothing we report.
function parseIso(iso) {
  return Date.parse(iso.replace(/(\.\d{3})\d+/, '$1'));
}
