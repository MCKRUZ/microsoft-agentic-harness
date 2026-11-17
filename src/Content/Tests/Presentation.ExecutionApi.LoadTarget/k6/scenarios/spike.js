// spike: load jumps from almost nothing to three times the profile's ramp level (at most 300 VUs),
// then drops back.
//
// Proves: recovery. A host that survives a spike but stays slow afterwards (a thread pool that grew
// and never settled, leaked streams, a queue that never empties) shows it in the recovery phase.
//
// The spike leaves a backlog: about PEAK runs queued on DISPATCH_SLOTS slots. Runs started while it
// drains are tagged phase:draining and are expected to wait. Only runs started after the estimated
// drain are tagged phase:recovery; compare their end-to-end time with the phase:before runs at the same
// light load. A recovery phase with no finished runs fails the scenario rather than passing empty.

import exec from 'k6/execution';
import { CALLER_COUNT, DISPATCH_SLOTS, GRACEFUL_STOP, callerForVu, load, modelAnswerMs } from '../lib/config.js';
import { awaitBundleRun, registerBundle, startBundleRun, paceIteration } from '../lib/api.js';
import { CORRECTNESS, NO_THROTTLING, thresholds } from '../lib/thresholds.js';

const level = load();
const LIGHT = Math.min(5, CALLER_COUNT);
const PEAK = Math.min(level.rampTo * 3, 300, CALLER_COUNT); // 300 caps the jump on this one machine

// Phase boundaries in seconds from the start of the test, matching the stages below. The drain estimate
// is padded by half, because runs started during the hold join the backlog too.
const SPIKE_STARTS = 30;
const SPIKE_ENDS = 30 + 10 + 60 + 10;
const DRAIN_SECONDS = Math.ceil(((PEAK * modelAnswerMs()) / 1000 / DISPATCH_SLOTS) * 1.5);
const RECOVERY_STARTS = SPIKE_ENDS + DRAIN_SECONDS;
const RECOVERY_SECONDS = 90;

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
        { duration: `${DRAIN_SECONDS}s`, target: LIGHT }, // the backlog drains
        { duration: `${RECOVERY_SECONDS}s`, target: LIGHT }, // recovery
      ],
      gracefulRampDown: '5m',
      gracefulStop: GRACEFUL_STOP,
    },
  },
  thresholds: thresholds(CORRECTNESS, NO_THROTTLING, {
    run_succeeded: ['rate>0.99'],
    // Reported so the two phases can be compared; the bound only catches a host that never recovers.
    'run_end_to_end{phase:before}': ['p(95)<60000'],
    'run_end_to_end{phase:recovery}': ['p(95)<60000'],
    // Without this, a recovery phase whose runs never finished would pass the bound above with no data.
    'runs_recorded{phase:recovery}': ['count>0'],
  }),
};

function phase() {
  const elapsed = exec.instance.currentTestRunDuration / 1000;
  if (elapsed < SPIKE_STARTS) return 'before';
  if (elapsed < SPIKE_ENDS) return 'spike';
  return elapsed < RECOVERY_STARTS ? 'draining' : 'recovery';
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
