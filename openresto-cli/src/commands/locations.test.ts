import { test, describe, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { Command } from "commander";
import { registerLocationsCommands } from "./locations.js";
import { withLang } from "../withLang.test.js";

process.env.OPENRESTO_LANG = "en";

function buildProgram(): Command {
  const program = new Command();
  program
    .exitOverride()
    .option("--profile <name>")
    .option("--json")
    .option("--yes");
  registerLocationsCommands(program);
  return program;
}

function respond(
  responder: (method: string, url: string) => Response,
): typeof fetch {
  return (async (input: string | URL | Request, init?: RequestInit) =>
    responder(init?.method ?? "GET", String(input))) as typeof fetch;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

async function run(lang: "en" | "es", args: string[]): Promise<string[]> {
  const lines: string[] = [];
  const originalLog = console.log;
  const originalError = console.error;
  console.log = (...a: unknown[]) => lines.push(a.map(String).join(" "));
  console.error = (...a: unknown[]) => lines.push(a.map(String).join(" "));
  try {
    await withLang(lang, () =>
      buildProgram().parseAsync(args, { from: "user" }),
    );
  } finally {
    console.log = originalLog;
    console.error = originalError;
  }
  return lines;
}

let originalFetch: typeof fetch;
let originalUrl: string | undefined;
let originalKey: string | undefined;

beforeEach(() => {
  originalFetch = globalThis.fetch;
  originalUrl = process.env.OPENRESTO_URL;
  originalKey = process.env.OPENRESTO_API_KEY;
  process.env.OPENRESTO_URL = "https://cli.example";
  process.env.OPENRESTO_API_KEY = "test-key";
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  if (originalUrl === undefined) delete process.env.OPENRESTO_URL;
  else process.env.OPENRESTO_URL = originalUrl;
  if (originalKey === undefined) delete process.env.OPENRESTO_API_KEY;
  else process.env.OPENRESTO_API_KEY = originalKey;
  process.exitCode = 0;
});

describe("locations confirmations", () => {
  const cases: [string[], string, string][] = [
    [
      ["pause", "3", "--minutes", "30"],
      "Location 3 paused for 30 minute(s).",
      "Ubicación 3 en pausa por 30 minuto(s).",
    ],
    [["unpause", "3"], "Location 3 unpaused.", "Ubicación 3 reanudada."],
    [["archive", "3"], "Location 3 archived.", "Ubicación 3 archivada."],
    [["restore", "3"], "Location 3 restored.", "Ubicación 3 restaurada."],
  ];

  for (const [args, english, spanish] of cases) {
    test(`locations ${args[0]} reports in both languages`, async () => {
      globalThis.fetch = respond(() => new Response(null, { status: 204 }));

      assert.deepEqual(await run("en", ["locations", ...args]), [english]);
      assert.deepEqual(await run("es", ["locations", ...args]), [spanish]);
    });
  }
});

describe("locations extend", () => {
  const withSkipped = {
    message: "Bookings extended, except those with another booking right after them.",
    extendedBookings: [{ id: 1 }, { id: 2 }],
    skippedBookings: [
      { id: 7, bookingRef: "R7", tableId: 3, tableName: "T\x1b[31m3" },
    ],
  };

  test("lists the bookings left as they were, with the API's text escaped", async () => {
    globalThis.fetch = respond(() => json(200, withSkipped));

    const lines = await run("en", ["locations", "extend", "4", "--minutes", "30"]);

    assert.equal(lines[0], "Extended 2 booking(s) at location 4 by 30 minute(s).");
    assert.equal(
      lines[1],
      "1 booking(s) left as they were because another booking follows on the same table:",
    );
    assert.match(lines[2], /^id\s+bookingRef\s+tableName$/);
    assert.match(lines[4], /^7\s+R7\s+T\\x1b\[31m3$/);
    assert.ok(lines.every((l) => !l.includes("\u001b")));
  });

  test("says nothing about skipped bookings when none were", async () => {
    globalThis.fetch = respond(() =>
      json(200, { extendedBookings: [{ id: 1 }], skippedBookings: [] }),
    );

    const lines = await run("es", ["locations", "extend", "4", "--minutes", "30"]);

    assert.deepEqual(lines, [
      "Se alargaron 1 reserva(s) de la ubicación 4 en 30 minuto(s).",
    ]);
  });

  test("--json prints the response untouched", async () => {
    globalThis.fetch = respond(() => json(200, withSkipped));

    const lines = await run("es", [
      "--json",
      "locations",
      "extend",
      "4",
      "--minutes",
      "30",
    ]);

    assert.deepEqual(JSON.parse(lines.join("\n")), withSkipped);
  });
});

describe("locations delete", () => {
  const preview = {
    name: "Cafe \x1b[31mNorte",
    sectionCount: 2,
    tableCount: 9,
    bookingCount: 14,
  };

  test("the Spanish confirmation names the location and its contents, with the name escaped", async () => {
    globalThis.fetch = respond(() => json(200, preview));
    const originalIsTTY = process.stdin.isTTY;
    process.stdin.isTTY = false;
    let lines: string[];
    try {
      lines = await run("es", ["locations", "delete", "3"]);
    } finally {
      process.stdin.isTTY = originalIsTTY;
    }

    assert.equal(process.exitCode, 1);
    assert.equal(lines.length, 1);
    assert.doesNotMatch(lines[0], /\u001b/);
    assert.match(
      lines[0],
      /^Error: Se eliminará de forma permanente "Cafe \\x1b\[31mNorte" \(2 sección\(es\), 9 mesa\(s\), 14 reserva\(s\)\)\. Vuelve a ejecutar el comando con --yes/,
    );
  });

  test("a delete the server refuses because the location is not archived points to the fix, in Spanish", async () => {
    globalThis.fetch = respond((method) =>
      method === "DELETE"
        ? json(400, { message: "Archive it first." })
        : json(200, preview),
    );

    const lines = await run("es", ["locations", "delete", "3", "--yes"]);

    assert.deepEqual(lines, [
      'Error: Archive it first. Primero archiva la ubicación con "locations archive 3".',
    ]);
  });

  test("the same refusal hint is English under OPENRESTO_LANG=en", async () => {
    globalThis.fetch = respond((method) =>
      method === "DELETE"
        ? json(400, { message: "Archive it first." })
        : json(200, preview),
    );

    const lines = await run("en", ["locations", "delete", "3", "--yes"]);

    assert.deepEqual(lines, [
      'Error: Archive it first. Archive the location first with "locations archive 3".',
    ]);
  });

  test("a delete still completes, in Spanish, when the preview is unavailable", async () => {
    globalThis.fetch = respond((method) =>
      method === "DELETE" ? new Response(null, { status: 204 }) : json(500, {}),
    );

    const lines = await run("es", ["locations", "delete", "3", "--yes"]);

    assert.deepEqual(lines, ["Ubicación 3 eliminada."]);
  });
});
