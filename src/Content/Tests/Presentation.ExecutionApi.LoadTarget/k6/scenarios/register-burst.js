// register-burst: many callers stage and delete bundles continuously.
//
// Proves: the cost of archive staging under concurrency. Each register is a multipart upload that is
// validated (entry count, compression ratio), extracted to a staging directory and parsed; each delete
// removes it. This is the API's only file-I/O-heavy path. Each caller paces itself under the
// 10-per-minute register window, so throughput comes from the number of callers.

import { sleep } from 'k6';
import { callerForVu, load } from '../lib/config.js';
import { deleteBundle, registerBundle } from '../lib/api.js';
import { CORRECTNESS, NO_THROTTLING, latency, thresholds } from '../lib/thresholds.js';

const level = load();

export const options = {
  scenarios: {
    register_burst: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '20s', target: level.rampTo },
        { duration: level.duration, target: level.rampTo },
        { duration: '20s', target: 0 },
      ],
    },
  },
  thresholds: thresholds(CORRECTNESS, NO_THROTTLING, latency(['register', 'delete'])),
};

export default function () {
  const caller = callerForVu(__VU);
  const handle = registerBundle(caller);
  if (handle) {
    deleteBundle(caller, handle);
  }
  // 10 registers a minute per caller: one every 6 s, plus margin for the window's edges.
  sleep(7);
}
