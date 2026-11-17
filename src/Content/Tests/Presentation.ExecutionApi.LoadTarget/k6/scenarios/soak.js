// soak: a steady mixed load of background runs and live streams for the whole profile duration.
//
// Proves: nothing accumulates. Run records, stream state, staging directories and conversation history
// all live on one in-memory instance with TTL sweeps; if a sweep misses something, memory and latency
// creep upward over the hour. Runs in the first and last fifth of the test are tagged phase:early and
// phase:late; a late p95 well above the early one is the signal. Pair this with the runner's
// dotnet-counters output (GC heap size, working set) to see the memory side.
//
// Meant for PROFILE=soak (60 minutes). Shorter profiles run it as a quick check of the scenario itself.

import exec from 'k6/execution';
import { callerForVu, load } from '../lib/config.js';
import { awaitBundleRun, readBundleStream, registerBundle, startBundleRun, paceIteration } from '../lib/api.js';
import { CORRECTNESS, NO_THROTTLING, latency, thresholds } from '../lib/thresholds.js';

const level = load();
const BACKGROUND_VUS = Math.ceil(level.vus / 2);
const STREAM_VUS = Math.max(1, Math.floor(level.vus / 2));

export const options = {
  scenarios: {
    background: { executor: 'constant-vus', vus: BACKGROUND_VUS, duration: level.duration, exec: 'background' },
    streams: {
      executor: 'constant-vus', vus: STREAM_VUS, duration: level.duration, exec: 'streams', gracefulStop: '5m',
    },
  },
  thresholds: thresholds(CORRECTNESS, NO_THROTTLING, latency(['start-run', 'start-stream-run', 'poll']), {
    run_succeeded: ['rate>0.99'],
    // Reported so early and late can be compared; the bounds only catch an outright collapse.
    'run_end_to_end{phase:early}': ['p(95)<60000'],
    'run_end_to_end{phase:late}': ['p(95)<60000'],
    'stream_duration{phase:early}': ['p(95)<60000'],
    'stream_duration{phase:late}': ['p(95)<60000'],
  }),
};

const totalMs = durationMs(level.duration);

/** k6 duration strings used by the profiles: '30s', '3m', '60m', '1h'. */
function durationMs(text) {
  const match = /^(\d+)(s|m|h)$/.exec(text);
  if (!match) {
    throw new Error(`soak: cannot read duration '${text}'`);
  }
  return Number(match[1]) * { s: 1000, m: 60000, h: 3600000 }[match[2]];
}

function tagPhase() {
  const elapsed = exec.instance.currentTestRunDuration;
  if (elapsed < totalMs * 0.2) {
    exec.vu.metrics.tags.phase = 'early';
  } else if (elapsed > totalMs * 0.8) {
    exec.vu.metrics.tags.phase = 'late';
  } else {
    delete exec.vu.metrics.tags.phase;
  }
}

let handle = null;

function ensureBundle(caller) {
  handle = handle || registerBundle(caller);
  return handle;
}

export function background() {
  const caller = callerForVu(__VU);
  if (!ensureBundle(caller)) {
    return;
  }
  tagPhase();
  const startedAt = Date.now();
  const run = startBundleRun(caller, handle, false);
  if (run) {
    awaitBundleRun(caller, handle, run.jobId, startedAt);
  }
  paceIteration(startedAt);
}

export function streams() {
  const caller = callerForVu(__VU);
  if (!ensureBundle(caller)) {
    return;
  }
  tagPhase();
  const startedAt = Date.now();
  const run = startBundleRun(caller, handle, true);
  if (run) {
    readBundleStream(caller, run.streamUrl);
  }
  paceIteration(startedAt);
}
