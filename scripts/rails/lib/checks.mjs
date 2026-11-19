// Required-vs-declared check comparison. A required context that nothing emits leaves every PR
// pending forever; a gate that emits but is not required can go red and still merge. Three sources:
//   required  — the live ruleset (`rules/branches/main`) and the committed ruleset JSON
//   declared  — a static read of the workflow files (job display names, matrix expanded, and
//               commit-status contexts posted by name); best-effort, not a YAML parser
//   observed  — the check runs and statuses actually reported on PR head commits in the window;
//               authoritative for "does anything produce it", but only sees what ran

/** Job ids at two-space indent under `jobs:`, each with its `name:` and inline matrix lists. */
export function parseWorkflow(text) {
  const lines = text.split(/\r?\n/);
  const triggers = parseTriggers(lines);
  const jobs = [];
  let inJobs = false;
  let job = null;
  let inMatrix = false;
  for (const line of lines) {
    if (/^\S/.test(line)) {
      inJobs = /^jobs:\s*$/.test(line);
      job = null;
      continue;
    }
    if (!inJobs) continue;
    const jobId = /^ {2}([A-Za-z0-9_-]+):\s*$/.exec(line);
    if (jobId) {
      job = { id: jobId[1], name: null, matrix: {} };
      jobs.push(job);
      inMatrix = false;
      continue;
    }
    if (!job) continue;
    const name = /^ {4}name:\s*(.+?)\s*$/.exec(line);
    if (name) job.name = unquote(name[1]);
    if (/^ {6}matrix:\s*$/.test(line)) inMatrix = true;
    else if (/^ {0,6}\S/.test(line)) inMatrix = false;
    const axis = inMatrix && /^ {8}([A-Za-z0-9_-]+):\s*\[(.*)\]\s*$/.exec(line);
    if (axis) job.matrix[axis[1]] = axis[2].split(',').map((value) => unquote(value.trim())).filter(Boolean);
  }
  return { triggers, jobs, contexts: jobs.flatMap(expandJobNames), statusContexts: parseStatusContexts(text) };
}

function parseTriggers(lines) {
  const triggers = new Set();
  let inOn = false;
  for (const line of lines) {
    const inline = /^(?:on|"on"|'on'):\s*(.+?)\s*$/.exec(line);
    if (inline) {
      inline[1].replace(/[[\]]/g, '').split(',').map((event) => event.trim()).filter(Boolean).forEach((event) => triggers.add(event));
      inOn = false;
      continue;
    }
    if (/^(?:on|"on"|'on'):\s*$/.test(line)) { inOn = true; continue; }
    if (/^\S/.test(line)) inOn = false;
    const event = inOn && /^ {2}([a-z_]+):/.exec(line);
    if (event) triggers.add(event[1]);
  }
  return [...triggers];
}

function unquote(value) {
  return value.replace(/^(['"])(.*)\1$/, '$2');
}

/** The check-run names a job reports: its `name:` (matrix-expanded) or, with no name, its id. */
export function expandJobNames(job) {
  const template = job.name ?? job.id;
  const axes = Object.entries(job.matrix).filter(([axis]) => template.includes(`matrix.${axis}`));
  let names = [template];
  for (const [axis, values] of axes) {
    const expression = new RegExp(String.raw`\$\{\{\s*matrix\.${axis}\s*\}\}`, 'g');
    names = names.flatMap((name) => values.map((value) => name.replace(expression, value)));
  }
  return names;
}

/** Commit-status contexts a workflow posts by literal name (`-f context=x`, `context: 'x'`, `"context": "x"`). */
export function parseStatusContexts(text) {
  const found = new Set();
  const patterns = [/-f\s+context=["']?([\w:./ -]+?)["']?(?:\s|$)/g, /["']?context["']?\s*[:=]\s*["']([^"'$]+)["']/g];
  for (const pattern of patterns) {
    for (const match of text.matchAll(pattern)) found.add(match[1].trim());
  }
  return [...found];
}

const minus = (left, right) => [...left].filter((item) => !right.has(item)).sort();

/**
 * Compare required contexts with what the workflows declare and what PRs actually reported.
 * Every argument is an array of context names.
 */
export function compareChecks({ requiredLive, requiredCommitted, declaredOnPullRequest, observedOnPullRequests }) {
  const live = new Set(requiredLive);
  const committed = new Set(requiredCommitted);
  const declared = new Set(declaredOnPullRequest);
  const observed = new Set(observedOnPullRequests);
  return {
    required_live: [...live].sort(),
    required_committed: [...committed].sort(),
    required_not_declared: minus(live, declared),
    required_not_observed: observed.size === 0 ? 'no data' : minus(live, observed),
    committed_not_live: minus(committed, live),
    live_not_committed: minus(live, committed),
    committed_not_declared: minus(committed, declared),
    observed_not_required: minus(observed, live),
  };
}
