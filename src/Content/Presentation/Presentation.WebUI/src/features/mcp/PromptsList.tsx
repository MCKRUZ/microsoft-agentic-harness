import { usePromptsQuery } from './useMcpQuery';

export function PromptsList() {
  const { data: prompts, isLoading, isError } = usePromptsQuery();

  if (isLoading) {
    return <div className="p-4 text-sm text-muted-foreground">Loading prompts…</div>;
  }

  if (isError) {
    return <div className="p-4 text-sm text-destructive">Failed to load prompts.</div>;
  }

  if (!prompts?.length) {
    return <div className="p-4 text-sm text-muted-foreground">No prompts available.</div>;
  }

  return (
    <ul className="divide-y divide-border">
      {prompts.map((p) => (
        <li key={p.name} className="px-3 py-2">
          <p className="font-semibold text-sm">{p.name}</p>
          {p.description && <p className="text-xs text-muted-foreground mt-0.5">{p.description}</p>}
          {p.arguments && p.arguments.length > 0 && (
            <p className="text-xs text-muted-foreground mt-0.5">
              Args: {p.arguments.map((a) => a.name).join(', ')}
            </p>
          )}
        </li>
      ))}
    </ul>
  );
}
