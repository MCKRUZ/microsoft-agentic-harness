/**
 * Stands in for a saved widget this app can no longer render — a widget type it no longer has, or one
 * saved at an older argument-shape version. Shown so the transcript visibly marks where content was,
 * rather than silently dropping it.
 */
export function UnsupportedWidget() {
  return (
    <div
      data-testid="agent-widget-unsupported"
      className="rounded-lg border border-border/50 bg-muted/40 px-3 py-2 text-xs text-muted-foreground"
    >
      This item was created by an earlier version of the app and can no longer be shown.
    </div>
  );
}
