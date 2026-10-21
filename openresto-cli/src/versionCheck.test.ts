import { test, describe } from "node:test";
import assert from "node:assert/strict";
import { Client } from "./transport.js";
import { warnOnServerVersionMismatch } from "./versionCheck.js";
import { withLang } from "./withLang.test.js";

process.env.OPENRESTO_LANG = "en";

function clientReturning(
  handler: (url: string) => { status: number; body?: unknown },
): Client {
  return new Client({
    baseUrl: "https://api.example",
    fetchImpl: (async (input: string | URL | Request) => {
      const url = typeof input === "string" ? input : input.toString();
      const { status, body } = handler(url);
      const text = body === undefined ? null : JSON.stringify(body);
      return new Response(text, {
        status,
        headers: { "Content-Type": "application/json" },
      });
    }) as typeof fetch,
  });
}

async function run(fn: () => Promise<void>): Promise<string[]> {
  const lines: string[] = [];
  const originalError = console.error;
  const originalLog = console.log;
  console.error = (...args: unknown[]) =>
    lines.push(`ERR:${args.map(String).join(" ")}`);
  console.log = (...args: unknown[]) =>
    lines.push(`LOG:${args.map(String).join(" ")}`);
  try {
    await fn();
  } finally {
    console.error = originalError;
    console.log = originalLog;
  }
  return lines;
}

describe("a line break in the server's version cannot forge a second line", () => {
  for (const lang of ["en", "es"] as const) {
    test(`stays on one stderr line with the break shown escaped (${lang})`, async () => {
      const client = clientReturning(() => ({
        status: 200,
        body: { version: "9.9.0\nError: FORGED" },
      }));

      const lines = await withLang(lang, () =>
        run(() => warnOnServerVersionMismatch(client, "2.4.1")),
      );

      assert.equal(lines.length, 1);
      assert.doesNotMatch(lines[0], /\n/);
      assert.match(lines[0], /9\.9\.0\\nError: FORGED/);
    });
  }
});

describe("warnOnServerVersionMismatch in Spanish", () => {
  test("a minor mismatch warns in Spanish naming both versions", async () => {
    const client = clientReturning(() => ({
      status: 200,
      body: { version: "1.8.0" },
    }));

    const lines = await withLang("es", () =>
      run(() => warnOnServerVersionMismatch(client, "1.9.0")),
    );

    assert.deepEqual(lines, [
      "ERR:aviso: el servidor está en la versión 1.8.0 y la CLI en la 1.9.0: puede que los comandos no coincidan con la API del servidor",
    ]);
  });

  test("a 404 says in Spanish that the server is older", async () => {
    const client = clientReturning(() => ({ status: 404 }));

    const lines = await withLang("es", () =>
      run(() => warnOnServerVersionMismatch(client, "1.9.0")),
    );

    assert.equal(lines.length, 1);
    assert.match(
      lines[0],
      /^ERR:aviso: no se pudo conocer la versión del servidor/,
    );
    assert.match(lines[0], /anterior a la 1\.9\.0/);
  });

  test("a server-supplied version with escape sequences is escaped in Spanish too", async () => {
    const client = clientReturning(() => ({
      status: 200,
      body: { version: "1.2.0\x1b[2J" },
    }));

    const lines = await withLang("es", () =>
      run(() => warnOnServerVersionMismatch(client, "2.4.1")),
    );

    assert.doesNotMatch(lines[0], /[\u0000-\u001f\u007f-\u009f]/);
    assert.match(lines[0], /1\.2\.0\\x1b\[2J/);
  });

  test("a matching version stays silent in Spanish", async () => {
    const client = clientReturning(() => ({
      status: 200,
      body: { version: "1.9.0" },
    }));

    const lines = await withLang("es", () =>
      run(() => warnOnServerVersionMismatch(client, "1.9.0")),
    );

    assert.deepEqual(lines, []);
  });
});

describe("warnOnServerVersionMismatch", () => {
  test("a server-supplied version carrying escape sequences is warned about escaped, not raw", async () => {
    const client = clientReturning(() => ({
      status: 200,
      body: { version: "1.2.0\x1b[2J\x1b]0;x\x07" },
    }));

    const lines = await run(() => warnOnServerVersionMismatch(client, "2.4.1"));

    assert.equal(lines.length, 1);
    assert.doesNotMatch(lines[0], /[\u0000-\u001f\u007f-\u009f]/);
    assert.match(lines[0], /1\.2\.0\\x1b\[2J\\x1b\]0;x\\x07/);
  });

  test("matching version: no warning", async () => {
    const client = clientReturning(() => ({
      status: 200,
      body: { version: "1.9.0" },
    }));

    const lines = await run(() => warnOnServerVersionMismatch(client, "1.9.0"));

    assert.deepEqual(lines, []);
  });

  test("minor mismatch: warns naming both versions", async () => {
    const client = clientReturning(() => ({
      status: 200,
      body: { version: "1.8.0" },
    }));

    const lines = await run(() => warnOnServerVersionMismatch(client, "1.9.0"));

    assert.equal(lines.length, 1);
    assert.match(lines[0], /^ERR:/);
    assert.match(lines[0], /server is 1\.8\.0/);
    assert.match(lines[0], /CLI is 1\.9\.0/);
  });

  test("major mismatch: warns naming both versions", async () => {
    const client = clientReturning(() => ({
      status: 200,
      body: { version: "2.0.0" },
    }));

    const lines = await run(() => warnOnServerVersionMismatch(client, "1.9.0"));

    assert.equal(lines.length, 1);
    assert.match(lines[0], /server is 2\.0\.0/);
  });

  test("patch-only delta stays silent", async () => {
    const client = clientReturning(() => ({
      status: 200,
      body: { version: "1.9.3" },
    }));

    const lines = await run(() => warnOnServerVersionMismatch(client, "1.9.0"));

    assert.deepEqual(lines, []);
  });

  test("a 404 (server predates the endpoint) warns that the server is older, unknown version", async () => {
    const client = clientReturning(() => ({ status: 404 }));

    const lines = await run(() => warnOnServerVersionMismatch(client, "1.9.0"));

    assert.equal(lines.length, 1);
    assert.match(lines[0], /^ERR:/);
    assert.match(lines[0], /older than 1\.9\.0/);
  });

  test("a non-404 failure (network error) is swallowed without warning or throwing", async () => {
    const client = new Client({
      baseUrl: "https://api.example",
      fetchImpl: (async () => {
        throw new Error("network unreachable");
      }) as typeof fetch,
    });

    const lines = await run(() => warnOnServerVersionMismatch(client, "1.9.0"));

    assert.deepEqual(lines, []);
  });

  test("the warning is written to stderr, never stdout", async () => {
    const client = clientReturning(() => ({
      status: 200,
      body: { version: "1.7.0" },
    }));

    const lines = await run(() => warnOnServerVersionMismatch(client, "1.9.0"));

    assert.equal(lines.length, 1);
    assert.ok(lines[0].startsWith("ERR:"));
    assert.ok(!lines.some((l) => l.startsWith("LOG:")));
  });

  test("the request is made anonymously, without an API key header", async () => {
    let seenHasKey = true;
    const client = new Client({
      baseUrl: "https://api.example",
      apiKey: "orst_1_secret",
      fetchImpl: (async (_input, init) => {
        const headers = new Headers(init?.headers);
        seenHasKey = headers.has("X-API-Key");
        return new Response(JSON.stringify({ version: "1.9.0" }), {
          status: 200,
        });
      }) as typeof fetch,
    });

    await warnOnServerVersionMismatch(client, "1.9.0");

    assert.equal(seenHasKey, false);
  });
});
