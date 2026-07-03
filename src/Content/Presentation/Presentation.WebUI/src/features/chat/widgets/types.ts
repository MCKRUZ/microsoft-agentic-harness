/**
 * A renderable inline widget the agent summoned mid-run via a client tool call. Stored on a
 * {@link import('../useChatStore').ChatMessage} and rendered by the widget registry.
 */
export interface AgentWidget {
  /** The client tool name that produced it, e.g. `render_image`. Keys into the widget registry. */
  type: string;
  /** Raw arguments the agent supplied. Each renderer re-validates these before use (untrusted input). */
  args: Record<string, unknown>;
}

/** Validated arguments for the `render_image` widget. */
export interface AgentImageArgs {
  url: string;
  alt?: string;
  caption?: string;
}

/** Outcome of validating raw `render_image` arguments at the client trust boundary. */
export type ImageArgsResult =
  | { ok: true; value: AgentImageArgs }
  | { ok: false; reason: string };

/**
 * Validates raw agent-supplied image arguments. The URL must be an absolute `https` URL — this is the
 * client-side trust boundary that keeps unsafe references (`javascript:`, `data:`, `http:`) out of the
 * DOM, mirroring the server-side check in `RenderImageTool` (defense in depth). Agent output is
 * untrusted, so this runs both when deciding the tool-result acknowledgement and again at render time.
 */
export function parseImageArgs(args: Record<string, unknown>): ImageArgsResult {
  const url = typeof args.url === 'string' ? args.url.trim() : '';
  if (!url) return { ok: false, reason: 'No image url was provided.' };

  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return { ok: false, reason: 'The image url is not a valid URL.' };
  }

  if (parsed.protocol !== 'https:') return { ok: false, reason: 'The image url must use https.' };

  return {
    ok: true,
    value: {
      url,
      alt: typeof args.alt === 'string' ? args.alt : undefined,
      caption: typeof args.caption === 'string' ? args.caption : undefined,
    },
  };
}
