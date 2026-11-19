// poll-storm: a high, steady rate of status polls spread across many callers.
//
// Proves: the per-request cost of the pipeline itself (JWT validation, knowledge-scope middleware,
// rate limiter, controller, MediatR, store lookup) with almost no work behind it. A poll reads one
// in-memory record, so whatever latency this shows is overhead every other request pays too.
//
// Requests are spread round-robin across callers so no caller exceeds its 60-per-minute window; if
// rate_limited is non-zero, the target needs more --callers for this arrival rate.

import exec from 'k6/execution';
import { CALLER_COUNT, callerAt, load } from '../lib/config.js';
import { pollBundleRun, registerBundle, startBundleRun } from '../lib/api.js';
import { CORRECTNESS, NO_THROTTLING, latency, thresholds } from '../lib/thresholds.js';

const level = load();

// Each caller may make 60 requests a minute. Keep each pooled caller at 80% of that (0.8 a second) and
// pool only as many callers as the rate needs, so setup stays proportional to the profile.
const PER_CALLER_RATE = 0.8;
const rate = Math.min(level.arrivalRate, Math.floor(CALLER_COUNT * PER_CALLER_RATE));
const POOL = Math.min(CALLER_COUNT, Math.ceil(rate / PER_CALLER_RATE));

export const options = {
  setupTimeout: '5m',
  scenarios: {
    poll_storm: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: Math.min(50, rate),
      maxVUs: Math.max(50, rate * 2),
      stages: [
        { duration: '20s', target: rate },
        { duration: level.duration, target: rate },
      ],
    },
  },
  thresholds: thresholds(CORRECTNESS, NO_THROTTLING, latency(['poll'])),
};

/**
 * One run per pooled caller to poll. Each is started as a live-stream run whose stream is never opened:
 * a streamed run executes only on its stream's connection, so it stays reserved and does no agent work,
 * and the storm measures the poll path alone. (Background runs would execute on the dispatch slots for
 * the whole storm.) A poll costs the same whatever the run's status. The load target keeps unopened
 * reservations for 30 minutes, longer than any storm.
 */
export function setup() {
  const runs = [];
  for (let i = 0; i < POOL; i++) {
    const caller = callerAt(i);
    const handle = registerBundle(caller);
    const run = handle && startBundleRun(caller, handle, true);
    runs.push(run ? { handle, jobId: run.jobId } : null);
  }
  return { runs };
}

export default function (data) {
  const index = exec.scenario.iterationInTest % POOL;
  const run = data.runs[index];
  if (run) {
    pollBundleRun(callerAt(index), run.handle, run.jobId);
  }
}
