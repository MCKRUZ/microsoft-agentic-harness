/** A validated data-table specification: header row + normalized cell rows. */
export interface TableSpec {
  title?: string;
  columns: string[];
  /** Each row is exactly `columns.length` cells — padded/truncated during parsing. */
  rows: string[][];
}

/** Outcome of validating raw `render_table` arguments at the client trust boundary. */
export type TableArgsResult =
  | { ok: true; value: TableSpec }
  | { ok: false; reason: string };

/** Coerces an agent-supplied cell/header value to display text; non-primitives render blank. */
function toText(value: unknown): string {
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  return '';
}

/** Pads or truncates a row to exactly `width` cells so every row aligns to the header. */
function normalizeRow(row: unknown[], width: number): string[] {
  const cells = row.slice(0, width).map(toText);
  while (cells.length < width) cells.push('');
  return cells;
}

/**
 * Validates raw agent-supplied table arguments and coerces them into a typed {@link TableSpec}. The
 * `columns` array is the client trust boundary (mirroring the server-side check in RenderTableTool): a
 * missing or empty one is rejected. Rows are best-effort — non-array rows are dropped and each surviving
 * row is normalized to the column count so a ragged table from the agent still renders cleanly. Cell
 * values are coerced to text (React escapes them), so the agent can never inject markup. Agent output is
 * untrusted, so this runs both when deciding the tool acknowledgement and again at render time.
 */
export function parseTableArgs(args: Record<string, unknown>): TableArgsResult {
  const rawColumns = Array.isArray(args.columns) ? args.columns : null;
  if (!rawColumns || rawColumns.length === 0) return { ok: false, reason: 'The table has no columns.' };

  const columns = rawColumns.map(toText);

  const rawRows = Array.isArray(args.rows) ? args.rows : [];
  const rows = rawRows
    .filter((r): r is unknown[] => Array.isArray(r))
    .map((r) => normalizeRow(r, columns.length));

  const spec: TableSpec = { columns, rows };
  if (typeof args.title === 'string') spec.title = args.title;
  return { ok: true, value: spec };
}
