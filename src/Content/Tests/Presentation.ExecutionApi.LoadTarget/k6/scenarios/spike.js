// spike: load jumps from almost nothing to three times the profile's ramp level (at most 300 VUs),
// then drops back.
//
// Proves: recovery. A host that survives a spike but stays slow afterwards (a backed-up dispatch
// queue, a thread pool that grew and never settled, leaked streams) shows it in the recovery phase.
// Runs started after the spike are tagged phase:recovery; compare their end-to-end time with the
// phase:before runs at the same light load.

import exec from 'k6/execution';
import { CALLER_COUNT, callerForVu, load } from '../lib/config.js';
import { awaitBundleRun, registerBundle, startBundleRun, paceIteration } from '../lib/api.js';
import { CORRECTNESS, NO_THROTTLING, thresholds } from '../lib/thresholds.js';

const level = load();
const LIGHT = Math.min(5, CALLER_COUNT);
const PEAK = Math.min(level.rampTo * 3, 300, CALLER_COUNT); // 300 caps the jump on this one machine

// Phase boundaries in seconds from the start of the test, matching the stages below.
const SPIKE_STARTS = 30;
const SPIKE_ENDS = 30 + 10 + 60 + 10;

export const options = {
  scenarios: {
    spike: {
      executor: 'ramping-vus',
      startVUs: LIGHT,
      stages: [
        { duration: '30s', target: LIGHT }, // before
        { duration: '10s', target: PEAK }, // the jump
        { duration: '60s', target: PEAK }, // hold
        { duration: '10s', target: LIGHT }, // the drop
        { duration: '90s', target: LIGHT }, // recovery
      ],
      gracefulRampDown: '5m',
    },
  },
  thresholds: thresholds(CORRECTNESS, NO_THROTTLING, {
    run_succeeded: ['rate>0.99'],
    // Reported so the two phases can be compared; the bound only catches a host that never recovers.
    'run_end_to_end{phase:before}': ['p(95)<60000'],
    'run_end_to_end{phase:recovery}': ['p(95)<60000'],
  }),
};

function phase() {
  const elapsed = exec.instance.currentTestRunDuration / 1000;
  if (elapsed < SPIKE_STARTS) return 'before';
  return elapsed < SPIKE_ENDS ? 'spike' : 'recovery';
}

let handle = null;

export default function () {
  const caller = callerForVu(__VU);
  handle = handle || registerBundle(caller);
  if (!handle) {
    return;
  }
  exec.vu.metrics.tags.phase = phase();
  const startedAt = Date.now();
  const run = startBundleRun(caller, handle, false);
  if (run) {
    awaitBundleRun(caller, handle, run.jobId, startedAt);
  }
  paceIteration(startedAt);
}
