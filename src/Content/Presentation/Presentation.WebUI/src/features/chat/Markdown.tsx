import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize, { defaultSchema } from 'rehype-sanitize';
import rehypeHighlight from 'rehype-highlight';
import 'highlight.js/styles/github-dark.css';

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
    <div className="markdown-body prose prose-sm max-w-none break-words">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[[rehypeSanitize, sanitizeSchema], rehypeHighlight]}
        components={{
          p: ({ children }) => <p className="whitespace-pre-wrap my-1 first:mt-0 last:mb-0">{children}</p>,
          ul: ({ children }) => <ul className="list-disc pl-5 my-1">{children}</ul>,
          ol: ({ children }) => <ol className="list-decimal pl-5 my-1">{children}</ol>,
          li: ({ children }) => <li className="my-0.5">{children}</li>,
          a: ({ children, href }) => (
            <a href={href} target="_blank" rel="noreferrer noopener" className="underline text-primary">
              {children}
            </a>
          ),
          pre: ({ children }) => (
            <pre className="rounded my-2 overflow-auto text-sm">{children}</pre>
          ),
          code: ({ className, children }) => {
            const isBlock = /language-/.test(className ?? '');
            if (isBlock) {
              return <code className={className}>{children}</code>;
            }
            return <code className="px-1 py-0.5 rounded bg-muted-foreground/20 text-sm">{children}</code>;
          },
          table: ({ children }) => (
            <div className="overflow-auto my-2">
              <table className="min-w-full border-collapse text-sm">{children}</table>
            </div>
          ),
          th: ({ children }) => <th className="border border-border px-2 py-1 text-left font-semibold">{children}</th>,
          td: ({ children }) => <td className="border border-border px-2 py-1">{children}</td>,
          blockquote: ({ children }) => (
            <blockquote className="border-l-2 border-muted-foreground/40 pl-3 my-2 italic">{children}</blockquote>
          ),
          h1: ({ children }) => <h1 className="text-xl font-semibold my-2">{children}</h1>,
          h2: ({ children }) => <h2 className="text-lg font-semibold my-2">{children}</h2>,
          h3: ({ children }) => <h3 className="text-base font-semibold my-1">{children}</h3>,
        }}
      >
        {content}
      </ReactMarkdown>
    </div>
  );
}
