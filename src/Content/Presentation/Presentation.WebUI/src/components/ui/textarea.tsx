import type { Ref, TextareaHTMLAttributes } from 'react';

// React 19 delivers `ref` to a function component as an ordinary prop, and the spread below
// forwards it to the DOM node — which is why ChatInput's mention insertion has always
// worked at runtime. TextareaHTMLAttributes does not declare `ref`, so only the TYPE was
// wrong, and nothing was typechecking this file to say so.
type TextareaProps = TextareaHTMLAttributes<HTMLTextAreaElement> & {
  ref?: Ref<HTMLTextAreaElement>;
};

export function Textarea({ className = '', ...props }: TextareaProps) {
  return (
    <textarea
      className={[
        'flex min-h-[60px] w-full rounded-md border border-input bg-transparent px-3 py-2 text-sm',
        'shadow-sm placeholder:text-muted-foreground',
        'focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
        'disabled:cursor-not-allowed disabled:opacity-50',
        className,
      ].join(' ')}
      {...props}
    />
  );
}
