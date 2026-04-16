import { useResourcesQuery } from './useMcpQuery';

export function ResourcesList() {
  const { data: resources, isLoading, isError } = useResourcesQuery();

  if (isLoading) {
    return <div className="p-4 text-sm text-muted-foreground">Loading resources…</div>;
  }

  if (isError) {
    return <div className="p-4 text-sm text-destructive">Failed to load resources.</div>;
  }

  if (!resources?.length) {
    return <div className="p-4 text-sm text-muted-foreground">No resources available.</div>;
  }

  return (
    <ul className="divide-y divide-border">
      {resources.map((r) => (
        <li key={r.uri} className="px-3 py-2">
          <p className="font-semibold text-sm">{r.name}</p>
          <p className="font-mono text-xs text-muted-foreground">{r.uri}</p>
          {r.description && <p className="text-xs text-muted-foreground mt-0.5">{r.description}</p>}
        </li>
      ))}
    </ul>
  );
}
