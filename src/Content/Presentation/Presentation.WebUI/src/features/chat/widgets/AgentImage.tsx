import { useState } from 'react';
import { ImageOff } from 'lucide-react';
import { parseImageArgs } from './types';

/**
 * Renders an agent-supplied image inline in the transcript. Re-validates the arguments at render time
 * (the agent's output is untrusted) and shows a safe fallback for an invalid URL or a load failure
 * rather than a broken image. This component is the only thing the `render_image` tool can summon —
 * the agent chooses the URL and captions, never the markup.
 */
export function AgentImage({ args }: { args: Record<string, unknown> }) {
  const [failed, setFailed] = useState(false);
  const result = parseImageArgs(args);

  if (!result.ok || failed) {
    const reason = result.ok ? 'The image could not be loaded.' : result.reason;
    return (
      <div
        data-testid="agent-image-fallback"
        className="flex items-center gap-2 rounded-lg border border-border/50 bg-muted/40 px-3 py-2 text-xs text-muted-foreground"
      >
        <ImageOff size={14} aria-hidden /> {reason}
      </div>
    );
  }

  const { url, alt, caption } = result.value;
  return (
    <figure
      data-testid="agent-image"
      className="rounded-lg border border-border/50 bg-card/50 overflow-hidden max-w-md"
    >
      <img
        src={url}
        alt={alt ?? caption ?? 'Image provided by the assistant'}
        className="max-h-96 w-full object-contain bg-background"
        loading="lazy"
        onError={() => { setFailed(true); }}
      />
      {caption && (
        <figcaption className="px-3 py-2 text-xs text-muted-foreground border-t border-border/50">
          {caption}
        </figcaption>
      )}
    </figure>
  );
}
