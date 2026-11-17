// Pass/fail rules shared by the scenarios.
//
// Correctness rules are strict from day one: the host must never answer with an undocumented 5xx or an
// unexpected status, however hard it is pushed. Latency rules start generous on purpose. They exist to
// catch a collapse, not to grade performance; once a baseline run has produced real numbers, tighten
// them here to just above that baseline so a regression fails the run.

/** Rules every scenario carries. */
export const CORRECTNESS = {
  server_errors: ['count==0'],
  unexpected_status: ['count==0'],
};

/** Added by scenarios that stay inside every limit, where any 429 means a limit misfired. */
export const NO_THROTTLING = {
  rate_limited: ['count==0'],
};

/** Collapse detectors for request latency, by operation tag. */
export function latency(ops) {
  const rules = {
    poll: ['p(95)<500', 'p(99)<1500'],
    'start-run': ['p(95)<1000'],
    'start-stream-run': ['p(95)<1000'],
    register: ['p(95)<2000'],
    delete: ['p(95)<1000'],
    'submit-workflow': ['p(95)<1000'],
    'start-workflow-run': ['p(95)<1000'],
    'poll-workflow': ['p(95)<500'],
    'invoke-tool': ['p(95)<1000'],
  };
  return Object.fromEntries(ops.map((op) => [`http_req_duration{name:${op}}`, rules[op]]));
}

/** Merges rule sets into one k6 thresholds object. */
export function thresholds(...sets) {
  return Object.assign({}, ...sets);
}
