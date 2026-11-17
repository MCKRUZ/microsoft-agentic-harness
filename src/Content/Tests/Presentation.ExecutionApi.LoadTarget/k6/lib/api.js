// One function per Execution API call, each recording its outcome through metrics.classify.
//
// Requests carry a `name` tag so k6 groups them by operation rather than by URL; every handle and job
// id is unique, and grouping by URL would create a metric series per run.

import http from 'k6/http';
import { sleep } from 'k6';
import { BASE_URL, BUNDLE_ZIP, TARGET } from './config.js';
import {
  classify, between, runEndToEnd, runExecution, runQueueWait, runSucceeded, streamDuration, streamFirstByte,
} from './metrics.js';

const TERMINAL = ['Succeeded', 'Failed', 'Cancelled', 'Blocked'];

function params(caller, name, extra = {}) {
  return {
    headers: { Authorization: `Bearer ${caller.token}`, ...(extra.headers || {}) },
    tags: { name },
    timeout: extra.timeout || '60s',
  };
}

/** Registers the fixture bundle. Returns its handle, or null when refused. */
export function registerBundle(caller) {
  const body = { file: http.file(BUNDLE_ZIP, 'bundle.zip', 'application/zip') };
  const res = http.post(`${BASE_URL}/api/bundles`, body, params(caller, 'register'));
  return classify(res, 'register', [201]) ? res.json('handle') : null;
}

/** Deletes a bundle, releasing its staging directory. */
export function deleteBundle(caller, handle) {
  const res = http.del(`${BASE_URL}/api/bundles/${handle}`, null, params(caller, 'delete'));
  return classify(res, 'delete', [204]);
}

/**
 * The shortest time one iteration of a looping scenario may take. Each iteration makes about three calls
 * against a per-caller window of 60 a minute; without a floor, an iteration that ends early (a refused
 * start, a fast failure, the instant model) repeats at once and the scenario measures its own throttling.
 */
export const MIN_ITERATION_SECONDS = 4;

/** Sleeps out whatever is left of `minSeconds` since `startedAtMs`. */
export function paceIteration(startedAtMs, minSeconds = MIN_ITERATION_SECONDS) {
  const left = minSeconds - (Date.now() - startedAtMs) / 1000;
  if (left > 0) {
    sleep(left);
  }
}

// Module state is per VU: each VU runs its own copy of this file.
let questionsAsked = 0;

/**
 * A question this VU has not asked before. The chat response cache answers a repeated question from
 * memory, so a fixed question makes every run after a caller's first measure the cache, not the harness.
 */
function freshQuestion() {
  questionsAsked += 1;
  return `Give a one-line answer. (vu ${__VU}, question ${questionsAsked})`;
}

/**
 * Starts a run. Returns { jobId, streamUrl } or null. 400 is the documented per-owner active-run cap,
 * so it is a capacity refusal rather than an error.
 */
export function startBundleRun(caller, handle, stream) {
  const res = http.post(
    `${BASE_URL}/api/bundles/${handle}/runs`,
    JSON.stringify({ userMessages: [freshQuestion()], maxTurns: 1, stream }),
    params(caller, stream ? 'start-stream-run' : 'start-run', { headers: { 'Content-Type': 'application/json' } }));
  return classify(res, stream ? 'start-stream-run' : 'start-run', [202], [400])
    ? { jobId: res.json('jobId'), streamUrl: res.json('streamUrl') }
    : null;
}

/** One status poll. Returns the run record or null. */
export function pollBundleRun(caller, handle, jobId) {
  const res = http.get(`${BASE_URL}/api/bundles/${handle}/runs/${jobId}`, params(caller, 'poll'));
  return classify(res, 'poll', [200]) ? res.json() : null;
}

/**
 * Polls until the run finishes or `maxSeconds` pass, then records queue wait, execution time and
 * outcome. Polling every `intervalSeconds` keeps one caller well under the 60-per-minute window.
 */
export function awaitBundleRun(caller, handle, jobId, startedAtMs, intervalSeconds = 2, maxSeconds = 300) {
  const deadline = Date.now() + maxSeconds * 1000;
  while (Date.now() < deadline) {
    sleep(intervalSeconds);
    const record = pollBundleRun(caller, handle, jobId);
    if (record && TERMINAL.includes(record.status)) {
      recordRun(record, startedAtMs);
      return record;
    }
  }
  runSucceeded.add(false);
  return null;
}

function recordRun(record, startedAtMs) {
  runEndToEnd.add(Date.now() - startedAtMs);
  const wait = between(record.createdAt, record.startedAt);
  const exec = between(record.startedAt, record.completedAt);
  if (wait !== null) runQueueWait.add(wait);
  if (exec !== null) runExecution.add(exec);
  // A bundle run whose agent turn failed still reports Succeeded at the run level, with the failure in
  // result.conversationSucceeded, so success has to read both.
  const conversationOk = !record.result || record.result.conversationSucceeded !== false;
  runSucceeded.add(record.status === 'Succeeded' && conversationOk);
}

/**
 * Opens a live stream and reads it to the end. 429 is the per-caller concurrency limiter. Returns true
 * when the stream closed on RUN_FINISHED.
 */
export function readBundleStream(caller, streamUrl) {
  const res = http.get(`${BASE_URL}${streamUrl}`,
    params(caller, 'stream', { headers: { Accept: 'text/event-stream' }, timeout: '300s' }));
  if (!classify(res, 'stream', [200])) {
    return false;
  }
  streamFirstByte.add(res.timings.waiting);
  streamDuration.add(res.timings.duration);
  const finished = typeof res.body === 'string' && res.body.includes('RUN_FINISHED');
  runSucceeded.add(finished);
  return finished;
}

/**
 * Submits a one-step workflow whose only step is an LLM call. Returns the workflow id or null. The
 * step names the deployment the target provides a skill for (see LoadTargetEnvironment.WorkflowAgent).
 */
export function submitWorkflow(caller) {
  const definition = {
    name: 'load-workflow',
    steps: [{
      name: 'answer',
      type: 'LlmCall',
      configuration: { type: 'llm_call', systemPrompt: 'Answer briefly.', modelDeploymentKey: TARGET.workflowAgent },
    }],
    edges: [],
  };
  const res = http.post(`${BASE_URL}/api/workflows`, JSON.stringify(definition),
    params(caller, 'submit-workflow', { headers: { 'Content-Type': 'application/json' } }));
  return classify(res, 'submit-workflow', [201]) ? res.json('workflowId') : null;
}

/** Starts a workflow run. 400 is the per-owner cap, 409 an already-active conflict. */
export function startWorkflowRun(caller, workflowId) {
  const res = http.post(`${BASE_URL}/api/workflows/${workflowId}/runs`, null, params(caller, 'start-workflow-run'));
  return classify(res, 'start-workflow-run', [202], [400, 409]) ? res.json('jobId') : null;
}

/** Opens a workflow progress stream and reads it to the end. 503 is the documented host-wide cap. */
export function readWorkflowStream(caller, workflowId, jobId) {
  const res = http.get(`${BASE_URL}/api/workflows/${workflowId}/runs/${jobId}/stream`,
    params(caller, 'workflow-stream', { headers: { Accept: 'text/event-stream' }, timeout: '300s' }));
  if (classify(res, 'workflow-stream', [200], [503])) {
    streamFirstByte.add(res.timings.waiting, { kind: 'workflow' });
    streamDuration.add(res.timings.duration, { kind: 'workflow' });
  }
  return res.status;
}

/** Polls a workflow run until it finishes; records the same run metrics as bundle runs. */
export function awaitWorkflowRun(caller, workflowId, jobId, startedAtMs, intervalSeconds = 2, maxSeconds = 300) {
  const deadline = Date.now() + maxSeconds * 1000;
  while (Date.now() < deadline) {
    const res = http.get(`${BASE_URL}/api/workflows/${workflowId}/runs/${jobId}`, params(caller, 'poll-workflow'));
    if (classify(res, 'poll-workflow', [200]) && TERMINAL.includes(res.json('status'))) {
      recordRun(res.json(), startedAtMs);
      return res.json();
    }
    sleep(intervalSeconds);
  }
  runSucceeded.add(false);
  return null;
}

/** Invokes the echo_calculate demo tool directly. 429 is the per-caller concurrency cap. */
export function invokeEchoTool(caller) {
  const res = http.post(`${BASE_URL}/api/tools/echo_calculate/invoke`,
    JSON.stringify({ operation: 'calculate', parameters: { expression: '6*7' } }),
    params(caller, 'invoke-tool', { headers: { 'Content-Type': 'application/json' } }));
  return classify(res, 'invoke-tool', [200]);
}
