import { useState, type ReactNode, isValidElement } from 'react';
import { Check, Copy } from 'lucide-react';
import { cn } from '@/lib/utils';

interface CodeBlockProps {
  children?: ReactNode;
  className?: string;
}

/**
 * Wraps a rendered `<pre>` with a hover-revealed copy button and language badge.
 * Children come from react-markdown — typically a `<code>` element whose own
 * children are the raw source text.
 */
export function CodeBlock({ children, className }: CodeBlockProps) {
  const [copied, setCopied] = useState(false);
  const source = extractText(children);
  const language = extractLanguage(children);

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
    <div className={cn('group/code relative my-3 rounded-lg border border-border/50 overflow-hidden', className)}>
      <div className="flex items-center justify-between px-3 py-1.5 bg-muted/80 border-b border-border/50">
        <span className="text-[11px] font-medium text-muted-foreground uppercase tracking-wide">
          {language || 'code'}
        </span>
        <button
          type="button"
          onClick={() => { void handleCopy(); }}
          aria-label={copied ? 'Copied' : 'Copy code'}
          title={copied ? 'Copied' : 'Copy code'}
          className={cn(
            'inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px] text-muted-foreground transition-all',
            'opacity-0 group-hover/code:opacity-100 focus:opacity-100',
            'hover:bg-accent hover:text-foreground',
            copied && 'opacity-100 text-emerald-400',
          )}
        >
          {copied ? <Check size={12} /> : <Copy size={12} />}
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      <pre className="overflow-auto text-sm leading-relaxed p-4 bg-[var(--code-bg)] m-0">
        {children}
      </pre>
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

function extractLanguage(node: ReactNode): string | null {
  if (!isValidElement<{ className?: string; children?: ReactNode }>(node)) return null;
  const className = node.props.className ?? '';
  const match = className.match(/language-(\w+)/);
  if (match) return match[1];
  // Check children (react-markdown wraps code in pre > code)
  if (isValidElement<{ className?: string }>(node.props.children)) {
    const childClass = node.props.children.props.className ?? '';
    const childMatch = childClass.match(/language-(\w+)/);
    if (childMatch) return childMatch[1];
  }
  return null;
}
