// workflow-runs: many callers run a one-step LLM workflow and watch it through its progress stream.
//
// Proves: the workflow path under load. Workflow runs go through the shared run substrate and its own
// dispatcher (MaxConcurrentDispatchedRuns, default 4), and their progress streams are capped both
// host-wide (MaxConcurrentProgressStreams, 64) and per owner (8). Above 64 VUs, capacity_refusals
// should start counting 503s on the stream: that is the host-wide cap working, not an error. Compare
// with stream-soak, where bundle streams have no host-wide cap.

import { callerForVu, load } from '../lib/config.js';
import { awaitWorkflowRun, readWorkflowStream, startWorkflowRun, submitWorkflow } from '../lib/api.js';
import { CORRECTNESS, NO_THROTTLING, latency, thresholds } from '../lib/thresholds.js';

const level = load();

export const options = {
  scenarios: {
    workflow_runs: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '20s', target: level.rampTo },
        { duration: level.duration, target: level.rampTo },
        { duration: '20s', target: 0 },
      ],
      gracefulRampDown: '5m',
    },
  },
  thresholds: thresholds(
    CORRECTNESS, NO_THROTTLING, latency(['submit-workflow', 'start-workflow-run', 'poll-workflow']),
    { run_succeeded: ['rate>0.99'] }),
};

let workflowId = null;

export default function () {
  const caller = callerForVu(__VU);
  workflowId = workflowId || submitWorkflow(caller);
  if (!workflowId) {
    return;
  }

  const startedAt = Date.now();
  const jobId = startWorkflowRun(caller, workflowId);
  if (!jobId) {
    return;
  }

  // Watch the run live when the host has stream capacity; either way, confirm the outcome by polling.
  readWorkflowStream(caller, workflowId, jobId);
  awaitWorkflowRun(caller, workflowId, jobId, startedAt);
}
