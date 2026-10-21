import { test, describe, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { Readable } from "node:stream";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { Command } from "commander";
import { registerAuthCommands } from "./auth.js";
import { upsertProfile } from "../config.js";
import { withLang } from "../withLang.test.js";

process.env.OPENRESTO_LANG = "en";

function buildProgram(): Command {
  const program = new Command();
  program
    .exitOverride()
    .option("--profile <name>")
    .option("--json")
    .option("--yes");
  registerAuthCommands(program);
  return program;
}

/** `auth login` reads the key from `process.stdin` when it is not a terminal. */
async function run(
  lang: "en" | "es",
  args: string[],
  stdinText: string,
): Promise<string[]> {
  const lines: string[] = [];
  const originalLog = console.log;
  const originalError = console.error;
  const originalStdin = Object.getOwnPropertyDescriptor(process, "stdin");
  console.log = (...a: unknown[]) => lines.push(a.map(String).join(" "));
  console.error = (...a: unknown[]) => lines.push(a.map(String).join(" "));
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

beforeEach(() => {
  originalFetch = globalThis.fetch;
  scratch = mkdtempSync(path.join(tmpdir(), "openresto-cli-auth-test-"));
  for (const name of [
    "HOME",
    "USERPROFILE",
    "OPENRESTO_URL",
    "OPENRESTO_API_KEY",
  ]) {
    saved[name] = process.env[name];
  }
  process.env.HOME = scratch;
  process.env.USERPROFILE = scratch;
  delete process.env.OPENRESTO_URL;
  delete process.env.OPENRESTO_API_KEY;
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  for (const [name, value] of Object.entries(saved)) {
    if (value === undefined) delete process.env[name];
    else process.env[name] = value;
  }
  rmSync(scratch, { recursive: true, force: true });
  process.exitCode = 0;
});

describe("auth messages", () => {
  test("logout confirms in both languages", async () => {
    upsertProfile("default", { url: "https://a.example", apiKey: "k" });

    assert.deepEqual(await run("en", ["auth", "logout"], ""), [
      'Removed the API key for profile "default".',
    ]);
    assert.deepEqual(await run("es", ["auth", "logout"], ""), [
      'Se eliminó la clave de API del perfil "default".',
    ]);
  });

  test("login with no key on stdin asks for one in Spanish", async () => {
    const lines = await run(
      "es",
      ["auth", "login", "--url", "https://a.example"],
      "",
    );

    assert.equal(process.exitCode, 1);
    assert.deepEqual(lines, ["Error: Falta la clave de API."]);
  });

  test("login with a remote http address is refused in Spanish before any request", async () => {
    let requests = 0;
    globalThis.fetch = (async () => {
      requests++;
      return new Response(null, { status: 204 });
    }) as typeof fetch;

    const lines = await run(
      "es",
      ["auth", "login", "--url", "http://example.com"],
      "orst_1_secret\n",
    );

    assert.equal(requests, 0);
    assert.equal(process.exitCode, 1);
    assert.match(lines[0], /^Error: http:\/\/example\.com no está cifrada/);
    assert.doesNotMatch(lines[0], /TypeError|Exception|\bat /);
  });

  test("a key the server rejects is reported in Spanish with the server's own text kept", async () => {
    globalThis.fetch = (async () =>
      new Response(JSON.stringify({ message: "Invalid API key." }), {
        status: 401,
        headers: { "Content-Type": "application/json" },
      })) as typeof fetch;

    const es = await run(
      "es",
      ["auth", "login", "--url", "https://a.example"],
      "bad\n",
    );
    const en = await run(
      "en",
      ["auth", "login", "--url", "https://a.example"],
      "bad\n",
    );

    assert.deepEqual(es, [
      "Error: No se pudo verificar la clave de API en https://a.example: Invalid API key.",
    ]);
    assert.deepEqual(en, [
      "Error: Could not verify the API key against https://a.example: Invalid API key.",
    ]);
  });

  test("a successful login saves the profile and says so in Spanish", async () => {
    globalThis.fetch = (async (input: string | URL | Request) =>
      String(input).endsWith("/api/version")
        ? new Response(JSON.stringify({ version: "0.0.1" }), { status: 200 })
        : new Response(JSON.stringify({ name: "ci" }), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          })) as typeof fetch;

    const lines = await run(
      "es",
      ["auth", "login", "--url", "https://a.example"],
      "orst_1_secret\n",
    );

    assert.equal(
      lines[0],
      'Se guardó el perfil "default" para https://a.example.',
    );
    assert.match(
      lines[1],
      /^aviso: el servidor está en la versión 0\.0\.1 y la CLI en la /,
    );
  });
});
