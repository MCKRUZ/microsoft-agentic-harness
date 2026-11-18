// Shared k6 configuration: where the fixtures are, who the simulated callers are, and how hard each
// load profile pushes. Every scenario imports this; nothing here sends a request.
//
// The load target writes the fixtures (tokens.json, bundle.zip, target.json) into one folder and the
// runner passes that folder as FIXTURES. Reading them in k6's init context means each VU loads them
// once, at start, never on the request path.

const fixtures = __ENV.FIXTURES;
if (!fixtures) {
  throw new Error('FIXTURES is not set: point it at the folder the load target wrote (the runner does this).');
}

const target = JSON.parse(open(`${fixtures}/target.json`));
const callers = JSON.parse(open(`${fixtures}/tokens.json`));

export const BASE_URL = target.baseUrl;
export const TARGET = target;
export const BUNDLE_ZIP = open(`${fixtures}/bundle.zip`, 'b');
export const PROFILE = (__ENV.PROFILE || 'smoke').toLowerCase();

/**
 * The caller a VU acts as. One caller per VU keeps every VU in its own rate-limit partition, so a
 * scenario measures the host rather than the per-caller limiter. Scenarios that want to share a
 * caller (limits-hold, noisy-neighbour) pick one explicitly with callerAt().
 */
export function callerForVu(vu) {
  if (vu > callers.length) {
    throw new Error(
      `VU ${vu} has no caller of its own: the target minted ${callers.length}. Raise the target's --callers.`);
  }
  return callers[vu - 1];
}

/** A specific caller by index, for scenarios that deliberately share or single out callers. */
export function callerAt(index) {
  return callers[index % callers.length];
}

/** How many distinct callers the target minted tokens for. */
export const CALLER_COUNT = callers.length;

/**
 * How long a scenario waits, after its last stage, for runs already started to finish. k6 hard-stops a
 * scenario at the end of its stages plus gracefulStop (30 s by default); a run still queued then is cut
 * off and never recorded, which drops the longest waits and biases every percentile low. Scenarios whose
 * iterations wait on a run set it to this.
 */
export const GRACEFUL_STOP = '5m';

/**
 * Background runs the host executes at once (BundleExecutionConfig.MaxConcurrentDispatchedBundleRuns,
 * whose default the load target does not change). Scenarios that build a queue use it to estimate how
 * long the queue takes to drain.
 */
export const DISPATCH_SLOTS = 16;

/** Roughly how long one model answer takes on the target, in milliseconds. */
export function modelAnswerMs() {
  const m = TARGET.model;
  const [h, min, sec] = m.timeToFirstToken.split(':').map(Number);
  const firstToken = ((h * 60 + min) * 60 + sec) * 1000;
  return firstToken + (m.tokensPerSecond > 0 ? (m.responseTokens / m.tokensPerSecond) * 1000 : 0);
}

/**
 * Load levels per profile. smoke proves every scenario works end to end in about a minute; baseline is
 * the reference measurement; stress ramps far past it to find where the host bends; soak holds a
 * moderate load for an hour to expose leaks. Scenarios read the numbers they need from here, so a
 * profile means the same thing in every scenario.
 */
const PROFILES = {
  smoke: { vus: 5, duration: '30s', rampTo: 10, arrivalRate: 5 },
  baseline: { vus: 50, duration: '3m', rampTo: 100, arrivalRate: 40 },
  stress: { vus: 100, duration: '5m', rampTo: 400, arrivalRate: 300 },
  soak: { vus: 50, duration: '60m', rampTo: 50, arrivalRate: 40 },
};

export function load() {
  const level = PROFILES[PROFILE];
  if (!level) {
    throw new Error(`Unknown PROFILE '${PROFILE}'. Use one of: ${Object.keys(PROFILES).join(', ')}.`);
  }
  return level;
}
