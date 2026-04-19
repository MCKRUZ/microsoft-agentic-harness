import { useState, type ReactNode, isValidElement } from 'react';
import { Check, Copy } from 'lucide-react';

interface CodeBlockProps {
  children?: ReactNode;
}

/**
 * Wraps a rendered `<pre>` with a hover-revealed copy button. The children
 * come from react-markdown — typically a `<code>` element whose own children
 * are the raw source text.
 */
export function CodeBlock({ children }: CodeBlockProps) {
  const [copied, setCopied] = useState(false);
  const source = extractText(children);

  const handleCopy = async (): Promise<void> => {
    try {
      await navigator.clipboard.writeText(source);
      setCopied(true);
      window.setTimeout(() => { setCopied(false); }, 1500);
    } catch {
      /* clipboard unavailable (e.g. insecure context) — swallow. */
    }
  };

  return (
    <div className="group relative my-2">
      <pre className="rounded overflow-auto text-sm">{children}</pre>
      <button
        type="button"
        onClick={() => { void handleCopy(); }}
        aria-label={copied ? 'Copied' : 'Copy code'}
        title={copied ? 'Copied' : 'Copy code'}
        className="absolute top-1 right-1 rounded p-1 bg-background/70 text-muted-foreground opacity-0 group-hover:opacity-100 focus:opacity-100 hover:text-foreground transition-opacity"
      >
        {copied ? <Check size={14} /> : <Copy size={14} />}
      </button>
    </div>
  );
}

function extractText(node: ReactNode): string {
  if (node == null || typeof node === 'boolean') return '';
  if (typeof node === 'string' || typeof node === 'number') return String(node);
  if (Array.isArray(node)) return node.map(extractText).join('');
  if (isValidElement<{ children?: ReactNode }>(node)) {
    return extractText(node.props.children);
  }
  return '';
}
