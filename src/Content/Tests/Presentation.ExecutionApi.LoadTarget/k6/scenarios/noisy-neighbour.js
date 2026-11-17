// noisy-neighbour: one caller floods the run queue while many light callers start one run at a time.
//
// Proves: whether one heavy caller degrades everyone else. Background dispatch is first in, first out
// across all callers (BundleExecutionConfig documents that it is not a fairness mechanism), so the
// heavy caller's queued runs sit ahead of the light callers'. Compare run_queue_wait{tier:light} here
// against submit-burst at the same light load. The heavy caller will hit its own per-owner cap (400)
// and rate window (429); both are expected and are counted, not failed.

import { sleep } from 'k6';
import { callerAt, load } from '../lib/config.js';
import { awaitBundleRun, registerBundle, startBundleRun, paceIteration } from '../lib/api.js';
import { CORRECTNESS, latency, thresholds } from '../lib/thresholds.js';

const level = load();
const HEAVY = 0; // caller index 0 is the noisy neighbour; light callers start at index 1

export const options = {
  scenarios: {
    heavy: {
      executor: 'constant-vus',
      vus: 1,
      duration: level.duration,
      exec: 'heavy',
      tags: { tier: 'heavy' },
    },
    light: {
      executor: 'constant-vus',
      vus: level.vus,
      duration: level.duration,
      exec: 'light',
      tags: { tier: 'light' },
      startTime: '10s', // let the heavy caller fill the queue first
    },
  },
  thresholds: thresholds(CORRECTNESS, latency(['start-run', 'poll']), {
    'run_succeeded{tier:light}': ['rate>0.99'],
  }),
};

let handle = null;

/** Starts runs back to back without waiting, up to the per-owner cap, then keeps the queue topped up. */
export function heavy() {
  const caller = callerAt(HEAVY);
  handle = handle || registerBundle(caller);
  if (handle) {
    startBundleRun(caller, handle, false);
  }
  sleep(1); // one start a second stays inside the 60-per-minute window
}

export function light() {
  const caller = callerAt(__VU); // VU ids start at 1, so light callers never collide with HEAVY
  handle = handle || registerBundle(caller);
  if (!handle) {
    return;
  }
  const startedAt = Date.now();
  const run = startBundleRun(caller, handle, false);
  if (run) {
    awaitBundleRun(caller, handle, run.jobId, startedAt);
  }
  paceIteration(startedAt);
}
