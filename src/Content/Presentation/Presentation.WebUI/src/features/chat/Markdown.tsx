import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize, { defaultSchema } from 'rehype-sanitize';
import rehypeHighlight from 'rehype-highlight';
import 'highlight.js/styles/github-dark.css';
import { CodeBlock } from './CodeBlock';

const sanitizeSchema = {
  ...defaultSchema,
  attributes: {
    ...defaultSchema.attributes,
    code: [...(defaultSchema.attributes?.code ?? []), ['className', /^language-./]],
    span: [...(defaultSchema.attributes?.span ?? []), ['className', /^hljs/]],
  },
};

interface MarkdownProps {
  content: string;
}

export function Markdown({ content }: MarkdownProps) {
  return (
    <div className="markdown-body prose prose-sm prose-invert max-w-none break-words text-foreground">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[[rehypeSanitize, sanitizeSchema], rehypeHighlight]}
        components={{
          p: ({ children }) => (
            <p className="whitespace-pre-wrap my-2 first:mt-0 last:mb-0 leading-relaxed">{children}</p>
          ),
          ul: ({ children }) => <ul className="list-disc pl-5 my-2 space-y-1">{children}</ul>,
          ol: ({ children }) => <ol className="list-decimal pl-5 my-2 space-y-1">{children}</ol>,
          li: ({ children }) => <li className="leading-relaxed">{children}</li>,
          a: ({ children, href }) => (
            <a
              href={href}
              target="_blank"
              rel="noreferrer noopener"
              className="text-blue-400 hover:text-blue-300 underline underline-offset-2 transition-colors"
            >
              {children}
            </a>
          ),
          pre: ({ children }) => <CodeBlock>{children}</CodeBlock>,
          code: ({ className, children }) => {
            const isBlock = /language-/.test(className ?? '');
            if (isBlock) {
              return <code className={className}>{children}</code>;
            }
            return (
              <code className="px-1.5 py-0.5 rounded-md bg-muted/80 text-[13px] font-mono text-foreground/90">
                {children}
              </code>
            );
          },
          table: ({ children }) => (
            <div className="overflow-auto my-3 rounded-lg border border-border/50">
              <table className="min-w-full border-collapse text-sm">{children}</table>
            </div>
          ),
          th: ({ children }) => (
            <th className="border-b border-border/50 bg-muted/40 px-3 py-2 text-left font-semibold text-xs uppercase tracking-wide">
              {children}
            </th>
          ),
          td: ({ children }) => (
            <td className="border-b border-border/30 px-3 py-2">{children}</td>
          ),
          blockquote: ({ children }) => (
            <blockquote className="border-l-3 border-muted-foreground/40 pl-4 my-3 italic text-muted-foreground">
              {children}
            </blockquote>
          ),
          h1: ({ children }) => (
            <h1 className="text-xl font-semibold my-3 text-foreground">{children}</h1>
          ),
          h2: ({ children }) => (
            <h2 className="text-lg font-semibold my-2.5 text-foreground">{children}</h2>
          ),
          h3: ({ children }) => (
            <h3 className="text-base font-semibold my-2 text-foreground">{children}</h3>
          ),
          hr: () => <hr className="my-4 border-border/50" />,
        }}
      >
        {content}
      </ReactMarkdown>
    </div>
  );
}
