// stream-soak: many callers each hold a live stream open for a whole run, back to back.
//
// Proves: what many simultaneous live bundle streams cost the host. A streamed run executes inline on
// its connection and is never queued, so at N VUs the host is running N agent conversations at once.
// The host refuses streams past MaxConcurrentStreams (64), so the VUs stop at that limit: this measures
// the cost of a full host, not the refusal. Watch stream_first_byte (does the run start promptly?),
// stream_duration against the model's own time, and the host's memory and thread-pool counters.

import { GRACEFUL_STOP, STREAM_HOST_LIMIT, callerForVu, load } from '../lib/config.js';
import { readBundleStream, registerBundle, startBundleRun, paceIteration } from '../lib/api.js';
import { CORRECTNESS, NO_THROTTLING, latency, thresholds } from '../lib/thresholds.js';

const level = load();
const streams = Math.min(level.rampTo, STREAM_HOST_LIMIT);

export const options = {
  scenarios: {
    stream_soak: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '20s', target: streams },
        { duration: level.duration, target: streams },
        { duration: '20s', target: 0 },
      ],
      gracefulRampDown: '5m',
      gracefulStop: GRACEFUL_STOP,
    },
  },
  thresholds: thresholds(CORRECTNESS, NO_THROTTLING, latency(['register', 'start-stream-run']), {
    run_succeeded: ['rate>0.99'],
    // Collapse detector: a run should begin streaming within seconds, not wait behind other streams.
    stream_first_byte: ['p(95)<3000'],
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
  const run = startBundleRun(caller, handle, true);
  if (run) {
    readBundleStream(caller, run.streamUrl);
  }
  paceIteration(startedAt);
}
