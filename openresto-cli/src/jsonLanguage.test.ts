import { test, describe, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { Readable } from "node:stream";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { Command } from "commander";
import { registerAuthCommands } from "./commands/auth.js";
import { registerBookingsCommands } from "./commands/bookings.js";
import { registerLocationsCommands } from "./commands/locations.js";
import { registerSectionsCommands } from "./commands/sections.js";
import { registerTablesCommands } from "./commands/tables.js";
import { upsertProfile } from "./config.js";
import { withLang } from "./withLang.test.js";

process.env.OPENRESTO_LANG = "en";

function buildProgram(): Command {
  const program = new Command();
  program
    .exitOverride()
    .option("--profile <name>")
    .option("--json")
    .option("--yes");
  registerAuthCommands(program);
  registerBookingsCommands(program);
  registerLocationsCommands(program);
  registerSectionsCommands(program);
  registerTablesCommands(program);
  return program;
}

async function stdoutOf(
  lang: "en" | "es",
  args: string[],
  stdinText = "",
): Promise<string[]> {
  const lines: string[] = [];
  const originalLog = console.log;
  const originalError = console.error;
  const originalStdin = Object.getOwnPropertyDescriptor(process, "stdin");
  console.log = (...a: unknown[]) => lines.push(a.map(String).join(" "));
  console.error = () => {};
  Object.defineProperty(process, "stdin", {
    value: Readable.from([stdinText]),
    configurable: true,
  });
  try {
    await withLang(lang, () =>
      buildProgram().parseAsync(args, { from: "user" }),
    );
  } finally {
    console.log = originalLog;
    console.error = originalError;
    if (originalStdin) Object.defineProperty(process, "stdin", originalStdin);
  }
  return lines;
}

let originalFetch: typeof fetch;
let scratch: string;
const saved: Record<string, string | undefined> = {};
const ENV_NAMES = ["HOME", "USERPROFILE", "OPENRESTO_URL", "OPENRESTO_API_KEY"];

beforeEach(() => {
  originalFetch = globalThis.fetch;
  scratch = mkdtempSync(path.join(tmpdir(), "openresto-cli-json-test-"));
  for (const name of ENV_NAMES) saved[name] = process.env[name];
  process.env.HOME = scratch;
  process.env.USERPROFILE = scratch;
  process.env.OPENRESTO_URL = "https://cli.example";
  process.env.OPENRESTO_API_KEY = "test-key";
  globalThis.fetch = (async () =>
    new Response("{}", {
      status: 200,
      headers: { "Content-Type": "application/json" },
    })) as typeof fetch;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  for (const name of ENV_NAMES) {
    if (saved[name] === undefined) delete process.env[name];
    else process.env[name] = saved[name];
  }
  rmSync(scratch, { recursive: true, force: true });
  process.exitCode = 0;
});

// [area, args, English line, Spanish line]
const CASES: [string, string[], string, string][] = [
  [
    "bookings",
    ["bookings", "cancel", "7"],
    "Booking 7 cancelled.",
    "Reserva 7 cancelada.",
  ],
  [
    "bookings",
    ["bookings", "restore", "7"],
    "Booking 7 restored.",
    "Reserva 7 restaurada.",
  ],
  [
    "bookings",
    ["bookings", "purge", "7", "--yes"],
    "Booking 7 purged.",
    "Reserva 7 eliminada de forma permanente.",
  ],
  [
    "locations",
    ["locations", "pause", "3", "--minutes", "30"],
    "Location 3 paused for 30 minute(s).",
    "Ubicación 3 en pausa por 30 minuto(s).",
  ],
  [
    "locations",
    ["locations", "unpause", "3"],
    "Location 3 unpaused.",
    "Ubicación 3 reanudada.",
  ],
  [
    "locations",
    ["locations", "archive", "3"],
    "Location 3 archived.",
    "Ubicación 3 archivada.",
  ],
  [
    "locations",
    ["locations", "restore", "3"],
    "Location 3 restored.",
    "Ubicación 3 restaurada.",
  ],
  [
    "locations",
    ["locations", "delete", "3", "--yes"],
    "Location 3 deleted.",
    "Ubicación 3 eliminada.",
  ],
  [
    "sections",
    ["sections", "delete", "9", "--location", "7", "--yes"],
    "Section 9 removed.",
    "Sección 9 quitada.",
  ],
  [
    "tables",
    ["tables", "delete", "5", "--location", "7", "--section", "9", "--yes"],
    "Table 5 removed.",
    "Mesa 5 quitada.",
  ],
  [
    "auth",
    ["auth", "logout"],
    'Removed the API key for profile "default".',
    'Se eliminó la clave de API del perfil "default".',
  ],
];

describe("confirmation lines under --json", () => {
  for (const [area, args, english, spanish] of CASES) {
    test(`${area}: ${args.slice(0, 2).join(" ")} prints English under --json whatever the language, and follows the language without it`, async () => {
      upsertProfile("default", { url: "https://a.example", apiKey: "k" });

      const jsonEn = await stdoutOf("en", [...args, "--json"]);
      upsertProfile("default", { url: "https://a.example", apiKey: "k" });
      const jsonEs = await stdoutOf("es", [...args, "--json"]);
      upsertProfile("default", { url: "https://a.example", apiKey: "k" });
      const plainEs = await stdoutOf("es", args);
      upsertProfile("default", { url: "https://a.example", apiKey: "k" });
      const plainEn = await stdoutOf("en", args);

      assert.deepEqual(jsonEn, [english]);
      assert.deepEqual(jsonEs, jsonEn);
      assert.deepEqual(plainEs, [spanish]);
      assert.deepEqual(plainEn, [english]);
    });
  }

  test("auth: login prints English under --json in Spanish, Spanish without it", async () => {
    const args = ["auth", "login", "--url", "https://a.example"];

    const jsonEs = await stdoutOf("es", [...args, "--json"], "orst_1_secret\n");
    const plainEs = await stdoutOf("es", args, "orst_1_secret\n");

    assert.deepEqual(jsonEs, [
      'Saved profile "default" for https://a.example.',
    ]);
    assert.deepEqual(plainEs, [
      'Se guardó el perfil "default" para https://a.example.',
    ]);
  });
});
