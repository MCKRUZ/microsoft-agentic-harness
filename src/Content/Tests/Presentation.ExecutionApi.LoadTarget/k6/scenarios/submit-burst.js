// submit-burst: many callers each start background runs and wait for them.
//
// Proves: how the host behaves when demand exceeds its background dispatch slots
// (MaxConcurrentDispatchedBundleRuns, default 16). Watch run_queue_wait climb as VUs exceed the slots;
// run_execution should stay flat if the slots, not the runs, are the constraint. Every VU is its own
// caller with one run in flight, so neither the rate limiter nor the per-owner cap should ever fire.

import { GRACEFUL_STOP, callerForVu, load } from '../lib/config.js';
import { awaitBundleRun, registerBundle, startBundleRun, paceIteration } from '../lib/api.js';
import { CORRECTNESS, NO_THROTTLING, latency, thresholds } from '../lib/thresholds.js';

const level = load();

export const options = {
  scenarios: {
    submit_burst: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '20s', target: level.rampTo },
        { duration: level.duration, target: level.rampTo },
        { duration: '20s', target: 0 },
      ],
      gracefulRampDown: '5m',
      gracefulStop: GRACEFUL_STOP,
    },
  },
  thresholds: thresholds(CORRECTNESS, NO_THROTTLING, latency(['register', 'start-run', 'poll']), {
    run_succeeded: ['rate>0.99'],
  }),
};

let handle = null;

export default function () {
  const caller = callerForVu(__VU);
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
