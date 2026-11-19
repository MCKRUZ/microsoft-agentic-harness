// tool-invoke: many callers invoke the echo_calculate demo tool directly, steadily.
//
// Proves: the direct tool-invocation path under load: role check, capability-envelope check, tool
// resolution from keyed DI, and the per-caller concurrency limiter, with an instant tool behind them.
// Off by default in the host, so this needs a target started with --enable-tool-invocation (the
// runner's -EnableToolInvocation switch), which also mints tokens carrying the invoke role.

import { sleep } from 'k6';
import { TARGET, callerForVu, load } from '../lib/config.js';
import { invokeEchoTool } from '../lib/api.js';
import { CORRECTNESS, NO_THROTTLING, latency, thresholds } from '../lib/thresholds.js';

if (!TARGET.toolInvocationEnabled) {
  throw new Error('tool-invoke needs a target started with --enable-tool-invocation (runner: -EnableToolInvocation).');
}

const level = load();

export const options = {
  scenarios: {
    tool_invoke: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '20s', target: level.rampTo },
        { duration: level.duration, target: level.rampTo },
        { duration: '20s', target: 0 },
      ],
    },
  },
  thresholds: thresholds(CORRECTNESS, NO_THROTTLING, latency(['invoke-tool'])),
};

export default function () {
  invokeEchoTool(callerForVu(__VU));
  sleep(1.1); // stays under a 60-per-minute window should one apply to the tools controller
}
