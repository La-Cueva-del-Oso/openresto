/** Table/JSON rendering shared by every command. */

const CONTROL_CHARS = /[\u0000-\u001f\u007f-\u009f]/g;

/**
 * Makes text that came from the API safe to write to a terminal. C0 controls, DEL and the C1
 * range (U+0080-U+009F, where U+009B is a one-character CSI) are what let a guest's name or
 * special request clear the screen, retitle the window or overwrite earlier lines for whoever
 * runs `bookings list`. Each is replaced by a visible `\xNN` escape rather than dropped, so the
 * operator can see something was there. A line break or tab is shown as `\n`/`\t`, or left as is
 * when `keepLineBreaks` is set (error messages, which are not laid out in columns).
 *
 * @see [output.test.ts](./output.test.ts) — pins that no raw control byte reaches the terminal
 * while ordinary text (accents, emoji) is untouched.
 */
export function neutralizeControl(
  text: string,
  keepLineBreaks = false,
): string {
  return text.replace(CONTROL_CHARS, (c) => {
    if (c === "\n" || c === "\t") {
      if (keepLineBreaks) return c;
      return c === "\n" ? "\\n" : "\\t";
    }
    return "\\x" + c.charCodeAt(0).toString(16).padStart(2, "0");
  });
}

/**
 * JSON.stringify escapes C0 itself but passes DEL and C1 through raw. Escaping those as `\uXXXX`
 * keeps the output valid JSON that parses back to the same data; they can only appear inside
 * strings here, so the substitution cannot touch structure.
 */
export function printJson(data: unknown): void {
  console.log(
    JSON.stringify(data, null, 2).replace(
      /[\u007f-\u009f]/g,
      (c) => "\\u" + c.charCodeAt(0).toString(16).padStart(4, "0"),
    ),
  );
}

/** Renders an array of flat-ish objects as a padded, column-aligned table. Falls back to JSON for
 * anything that isn't a non-empty array of objects (a single record, a scalar, an empty list). */
export function printTable(data: unknown, columns?: string[]): void {
  if (!Array.isArray(data)) {
    printRecord(data);
    return;
  }
  if (data.length === 0) {
    console.log("(no results)");
    return;
  }

  const rows = data as Record<string, unknown>[];
  const keys = columns ?? inferColumns(rows);
  const cols = keys.map((k) => neutralizeControl(k));
  const cells = rows.map((row) => keys.map((k) => formatCell(row[k])));
  const widths = cols.map((c, i) =>
    Math.max(c.length, ...cells.map((r) => r[i].length)),
  );

  const renderRow = (values: string[]) =>
    values
      .map((v, i) => v.padEnd(widths[i]))
      .join("  ")
      .trimEnd();

  console.log(renderRow(cols));
  console.log(widths.map((w) => "-".repeat(w)).join("  "));
  for (const row of cells) {
    console.log(renderRow(row));
  }
}

function printRecord(data: unknown): void {
  if (data === undefined || data === null) {
    console.log("(no content)");
    return;
  }
  if (typeof data !== "object" || Array.isArray(data)) {
    console.log(neutralizeControl(String(data)));
    return;
  }
  const entries = Object.entries(data as Record<string, unknown>).map(
    ([k, v]) => [neutralizeControl(k), formatCell(v)] as const,
  );
  const width = Math.max(...entries.map(([k]) => k.length));
  for (const [key, value] of entries) {
    console.log(`${key.padEnd(width)}  ${value}`);
  }
}

function inferColumns(rows: Record<string, unknown>[]): string[] {
  const seen = new Set<string>();
  for (const row of rows) {
    for (const key of Object.keys(row)) {
      seen.add(key);
    }
  }
  return [...seen];
}

function formatCell(value: unknown): string {
  if (value === undefined || value === null) return "";
  return neutralizeControl(
    typeof value === "object" ? JSON.stringify(value) : String(value),
  );
}

/** Dispatches to JSON or table rendering based on the shared `--json` flag. */
export function printResult(
  data: unknown,
  json: boolean,
  columns?: string[],
): void {
  if (json) {
    printJson(data);
  } else {
    printTable(data, columns);
  }
}
