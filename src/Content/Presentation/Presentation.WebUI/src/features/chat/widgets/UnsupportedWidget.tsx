/**
 * Stands in for a saved widget this app cannot render — a widget type it does not have, or one saved at a
 * different argument-shape version (older, or newer when an open tab predates a server update). Shown so
 * the transcript visibly marks where content was, rather than silently dropping it.
 */
export function UnsupportedWidget() {
  return (
    <div
      data-testid="agent-widget-unsupported"
      className="rounded-lg border border-border/50 bg-muted/40 px-3 py-2 text-xs text-muted-foreground"
    >
      This item can&apos;t be shown in this version of the app.
    </div>
  );
}
