// limits-hold: single callers deliberately exceed each documented limit.
//
// Proves: every limit refuses with its documented status (429 for the rate limiters, 400 for the
// per-owner run cap) and never with a 5xx, an unexpected status, or a hang. Each check uses its own
// caller from the end of the caller list, so the checks cannot trip each other's limits.
//
// The checks run once each; the profile does not change this scenario.

import http from 'k6/http';
import { check } from 'k6';
import { BASE_URL, BUNDLE_ZIP, CALLER_COUNT, callerAt } from '../lib/config.js';
import { classify } from '../lib/metrics.js';
import { registerBundle, startBundleRun } from '../lib/api.js';
import { CORRECTNESS, thresholds } from '../lib/thresholds.js';

const once = (exec, limit) => ({
  executor: 'per-vu-iterations', vus: 1, iterations: 1, maxDuration: '3m', exec, tags: { limit },
});

export const options = {
  batchPerHost: 20,
  scenarios: {
    fixed_window: once('fixedWindow', 'fixed-window'),
    register_window: once('registerWindow', 'register-window'),
    stream_concurrency: once('streamConcurrency', 'stream-concurrency'),
    owner_run_cap: once('ownerRunCap', 'owner-run-cap'),
  },
  thresholds: thresholds(CORRECTNESS, {
    'checks{limit:fixed-window}': ['rate==1'],
    'checks{limit:register-window}': ['rate==1'],
    'checks{limit:stream-concurrency}': ['rate==1'],
    'checks{limit:owner-run-cap}': ['rate==1'],
  }),
};

const caller = (offset) => callerAt(CALLER_COUNT - offset);

function authed(c, name, headers = {}) {
  return { headers: { Authorization: `Bearer ${c.token}`, ...headers }, tags: { name }, timeout: '120s' };
}

function count(responses, status) {
  return responses.filter((r) => r.status === status).length;
}

/** 70 polls at once against the 60-per-minute window: the overflow must be 429, the rest 200. */
export function fixedWindow() {
  const c = caller(1);
  const handle = registerBundle(c);
  const run = handle && startBundleRun(c, handle, false);
  if (!check(run, { 'fixed-window: setup run started': (r) => r !== null })) {
    return;
  }
  const url = `${BASE_URL}/api/bundles/${handle}/runs/${run.jobId}`;
  const responses = http.batch(Array.from({ length: 70 }, () => ['GET', url, null, authed(c, 'poll')]));
  responses.forEach((r) => classify(r, 'poll', [200, 429]));
  check(responses, { 'fixed-window: overflow refused with 429': (rs) => count(rs, 429) >= 1 });
}

/** 12 registers in a row against the 10-per-minute register window. */
export function registerWindow() {
  const c = caller(2);
  const responses = [];
  for (let i = 0; i < 12; i++) {
    const body = { file: http.file(BUNDLE_ZIP, 'bundle.zip', 'application/zip') };
    const res = http.post(`${BASE_URL}/api/bundles`, body, authed(c, 'register'));
    classify(res, 'register', [201, 429]);
    responses.push(res);
  }
  check(responses, {
    'register-window: at most 10 accepted': (rs) => count(rs, 201) <= 10,
    'register-window: overflow refused with 429': (rs) => count(rs, 429) >= 2,
  });
}

/** Six live streams opened at once by one caller against a per-caller cap of four. */
export function streamConcurrency() {
  const c = caller(3);
  const handle = registerBundle(c);
  if (!check(handle, { 'stream-concurrency: bundle registered': (h) => h !== null })) {
    return;
  }
  const runs = Array.from({ length: 6 }, () => startBundleRun(c, handle, true)).filter((r) => r !== null);
  const responses = http.batch(runs.map((r) => [
    'GET', `${BASE_URL}${r.streamUrl}`, null, authed(c, 'stream', { Accept: 'text/event-stream' }),
  ]));
  responses.forEach((r) => classify(r, 'stream', [200, 429]));
  check(responses, {
    'stream-concurrency: six runs started': () => runs.length === 6,
    'stream-concurrency: at most 4 streams admitted': (rs) => count(rs, 200) <= 4,
    'stream-concurrency: overflow refused with 429': (rs) => count(rs, 429) >= 2,
  });
}

/** Fourteen background runs started at once by one owner against an active-run cap of ten. */
export function ownerRunCap() {
  const c = caller(4);
  const handle = registerBundle(c);
  if (!check(handle, { 'owner-run-cap: bundle registered': (h) => h !== null })) {
    return;
  }
  const body = JSON.stringify({ userMessages: ['Give a one-line answer.'], maxTurns: 1, stream: false });
  const responses = http.batch(Array.from({ length: 14 }, () => [
    'POST', `${BASE_URL}/api/bundles/${handle}/runs`, body,
    authed(c, 'start-run', { 'Content-Type': 'application/json' }),
  ]));
  responses.forEach((r) => classify(r, 'start-run', [202, 400], [400]));
  // Not "exactly 10": a run that finishes inside the burst frees its slot. The exact count is pinned
  // in-process by the Concurrency tests; here the point is that the cap answers 400, never 5xx.
  check(responses, { 'owner-run-cap: overflow refused with 400': (rs) => count(rs, 400) >= 1 });
}
