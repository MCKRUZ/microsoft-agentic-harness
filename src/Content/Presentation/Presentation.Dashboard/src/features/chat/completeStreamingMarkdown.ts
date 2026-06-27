/**
 * Stabilises partially-streamed markdown so an in-progress fenced code block
 * renders as a (growing) code block instead of flickering as raw ``` / ~~~
 * delimiters until its closing fence finally arrives.
 *
 * While a response streams in, the markdown is re-parsed on every chunk. An
 * opening code fence with no matching close parses as literal backticks, then
 * snaps into a styled block once the close arrives — a visible flicker on any
 * code-heavy answer. Closing the dangling fence ourselves makes the parser
 * treat the in-progress text as a code block immediately.
 *
 * Only the unclosed-fence case is handled — that is the dominant visible
 * offender during streaming. When all fences are balanced (well-formed or
 * completed content) the input is returned unchanged, so this is a no-op for
 * finished messages.
 *
 * Inline code spans (single backticks) are intentionally out of scope: they
 * almost always open and close within a single streamed chunk, so they don't
 * produce a sustained flicker.
 *
 * @param content The accumulated markdown received so far.
 * @returns The content with any dangling code fence closed; unchanged when balanced.
 */
export function completeStreamingMarkdown(content: string): string {
  if (!content) return content;

  // A fence is a line whose first non-whitespace characters are ``` or ~~~.
  // Counting line-anchored fences (rather than every ``` occurrence) avoids
  // miscounting triple backticks that appear mid-line inside prose. The capture
  // group is the full run of markers so we know the fence's length.
  const fencePattern = /^[ \t]*(`{3,}|~{3,})/;

  // The currently open fence's marker family (` or ~) and length, or null when no
  // fence is open. Per CommonMark a fence is closed only by a later line using the
  // same marker family with a run at least as long as the opening fence; a shorter
  // (or different-family) run is content inside the block.
  let open: { marker: string; length: number } | null = null;

  for (const line of content.split('\n')) {
    const match = fencePattern.exec(line);
    if (match === null) continue;

    const run = match[1];
    const marker = run[0]; // ` or ~
    if (open === null) {
      open = { marker, length: run.length };
    } else if (marker === open.marker && run.length >= open.length) {
      open = null;
    }
  }

  if (open === null) return content;

  // Close with a run matching the opening fence's length so a 4+-marker fence is
  // actually closed (a fixed 3-char closer would not satisfy CommonMark).
  const closer = open.marker.repeat(open.length);
  const separator = content.endsWith('\n') ? '' : '\n';
  return `${content}${separator}${closer}`;
}
