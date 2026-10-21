import { test, describe } from "node:test";
import assert from "node:assert/strict";
import { ApiError, Client } from "./transport.js";
import { withLang } from "./withLang.test.js";

process.env.OPENRESTO_LANG = "en";

function fakeFetch(
  handler: (
    url: string,
    init: RequestInit,
  ) => { status: number; body?: unknown },
): typeof fetch {
  return (async (input: string | URL | Request, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    const { status, body } = handler(url, init ?? {});
    const text = body === undefined ? null : JSON.stringify(body);
    return new Response(text, {
      status,
      headers: { "Content-Type": "application/json" },
    });
  }) as typeof fetch;
}

describe("Client server address", () => {
  const make = (baseUrl: string, env: NodeJS.ProcessEnv = {}) =>
    new Client({ baseUrl, apiKey: "orst_1_secret", env });

  test("refuses plain http to a remote host, with an actionable message", () => {
    assert.throws(
      () => make("http://example.com"),
      (err: unknown) => {
        assert.ok(err instanceof Error);
        assert.match(err.message, /https:\/\//);
        assert.match(err.message, /OPENRESTO_ALLOW_HTTP=1/);
        return true;
      },
    );
  });

  test("accepts https to a remote host", () => {
    assert.doesNotThrow(() => make("https://example.com"));
  });

  for (const url of [
    "http://localhost:8080",
    "http://127.0.0.1",
    "http://127.5.6.7:8080",
    "http://[::1]",
    "http://[::1]:8080",
  ]) {
    test(`accepts plain http to loopback ${url}`, () => {
      assert.doesNotThrow(() => make(url));
    });
  }

  for (const url of [
    "http://localhost.evil.com",
    "http://127.0.0.1.evil.com",
    "http://evil.com/localhost",
    "http://user@evil.com#@localhost",
    "http://128.0.0.1",
    "ftp://example.com",
  ]) {
    test(`refuses ${url}, which only looks like loopback or https`, () => {
      assert.throws(() => make(url));
    });
  }

  test("OPENRESTO_ALLOW_HTTP=1 lets plain http through to a remote host", () => {
    assert.doesNotThrow(() =>
      make("http://example.com", { OPENRESTO_ALLOW_HTTP: "1" }),
    );
  });

  test("OPENRESTO_ALLOW_HTTP set to anything but 1 does not open the gate", () => {
    assert.throws(() =>
      make("http://example.com", { OPENRESTO_ALLOW_HTTP: "true" }),
    );
  });

  test("reports an unparseable address instead of a raw URL error", () => {
    assert.throws(() => make("not a url"), /not a valid server address/);
  });
});

describe("Client", () => {
  test("throws immediately when no baseUrl is configured", () => {
    assert.throws(
      () => new Client({ baseUrl: "" }),
      /No server URL configured/,
    );
  });

  test("injects the X-API-Key header when a key is configured", async () => {
    let seenHeaders: Headers | undefined;
    const client = new Client({
      baseUrl: "https://api.example",
      apiKey: "orst_1_secret",
      fetchImpl: (async (_input, init) => {
        seenHeaders = new Headers(init?.headers);
        return new Response("{}", { status: 200 });
      }) as typeof fetch,
    });

    await client.get("/api/admin/overview");

    assert.equal(seenHeaders?.get("X-API-Key"), "orst_1_secret");
  });

  test("omits the X-API-Key header when no key is configured", async () => {
    let seenHeaders: Headers | undefined;
    const client = new Client({
      baseUrl: "https://api.example",
      fetchImpl: (async (_input, init) => {
        seenHeaders = new Headers(init?.headers);
        return new Response("{}", { status: 200 });
      }) as typeof fetch,
    });

    await client.get("/api/brand");

    assert.equal(seenHeaders?.has("X-API-Key"), false);
  });

  test("builds query strings from defined values only, skipping undefined", async () => {
    let seenUrl = "";
    const client = new Client({
      baseUrl: "https://api.example",
      fetchImpl: (async (input) => {
        seenUrl = input.toString();
        return new Response("[]", { status: 200 });
      }) as typeof fetch,
    });

    await client.get("/api/admin/bookings", {
      query: { date: "2026-01-01", email: undefined, seats: 4 },
    });

    const url = new URL(seenUrl);
    assert.equal(url.searchParams.get("date"), "2026-01-01");
    assert.equal(url.searchParams.has("email"), false);
    assert.equal(url.searchParams.get("seats"), "4");
  });

  test("sends a JSON body with Content-Type when a body is given", async () => {
    let seenBody = "";
    let seenHeaders: Headers | undefined;
    const client = new Client({
      baseUrl: "https://api.example",
      fetchImpl: (async (_input, init) => {
        seenBody = String(init?.body ?? "");
        seenHeaders = new Headers(init?.headers);
        return new Response("{}", { status: 201 });
      }) as typeof fetch,
    });

    await client.post("/api/admin/restaurants", { body: { name: "Bistro" } });

    assert.deepEqual(JSON.parse(seenBody), { name: "Bistro" });
    assert.equal(seenHeaders?.get("Content-Type"), "application/json");
  });

  test("204 No Content resolves to undefined rather than a parse error", async () => {
    const client = new Client({
      baseUrl: "https://api.example",
      fetchImpl: fakeFetch(() => ({ status: 204 })),
    });

    const result = await client.delete("/api/admin/bookings/1");

    assert.equal(result, undefined);
  });

  test("a 2xx response returns the parsed JSON body", async () => {
    const client = new Client({
      baseUrl: "https://api.example",
      fetchImpl: fakeFetch(() => ({ status: 200, body: { id: 1 } })),
    });

    const result = await client.get<{ id: number }>("/api/admin/bookings/1");

    assert.deepEqual(result, { id: 1 });
  });

  test("a non-2xx response throws ApiError carrying the server's message and code", async () => {
    const client = new Client({
      baseUrl: "https://api.example",
      fetchImpl: fakeFetch(() => ({
        status: 403,
        body: {
          message: "This API key is missing the 'bookings:write' scope.",
          code: "api_key.scope_missing",
        },
      })),
    });

    await assert.rejects(
      () => client.post("/api/admin/bookings"),
      (err: unknown) => {
        assert.ok(err instanceof ApiError);
        assert.equal(err.status, 403);
        assert.equal(err.code, "api_key.scope_missing");
        assert.match(err.message, /bookings:write/);
        return true;
      },
    );
  });

  test("a ProblemDetails-shaped error body (bare status code) still yields a readable message", async () => {
    const client = new Client({
      baseUrl: "https://api.example",
      fetchImpl: fakeFetch(() => ({
        status: 404,
        body: {
          title: "Not Found",
          detail: "The requested API endpoint does not exist.",
        },
      })),
    });

    await assert.rejects(
      () => client.get("/api/admin/nope"),
      (err: unknown) => {
        assert.ok(err instanceof ApiError);
        assert.match(err.message, /does not exist/);
        return true;
      },
    );
  });

  test("an unrecognized non-JSON error body still throws with a generic status-coded message", async () => {
    const client = new Client({
      baseUrl: "https://api.example",
      fetchImpl: (async () =>
        new Response("", { status: 500 })) as typeof fetch,
    });

    await assert.rejects(
      () => client.get("/api/admin/overview"),
      (err: unknown) => {
        assert.ok(err instanceof ApiError);
        assert.equal(err.status, 500);
        assert.match(err.message, /500/);
        return true;
      },
    );
  });

  test("a trailing slash on baseUrl does not produce a double slash in the request URL", async () => {
    let seenUrl = "";
    const client = new Client({
      baseUrl: "https://api.example/",
      fetchImpl: (async (input) => {
        seenUrl = input.toString();
        return new Response("{}", { status: 200 });
      }) as typeof fetch,
    });

    await client.get("/api/brand");

    assert.equal(seenUrl, "https://api.example/api/brand");
  });
});

describe("redirects", () => {
  function redirectingFetch(
    seen: {
      url: string;
      apiKey: string | null;
      redirect?: RequestInit["redirect"];
    }[],
    location: string | null,
    status = 301,
  ): typeof fetch {
    return (async (input: string | URL | Request, init?: RequestInit) => {
      seen.push({
        url: input.toString(),
        apiKey: new Headers(init?.headers).get("X-API-Key"),
        redirect: init?.redirect,
      });
      return new Response(null, {
        status,
        headers: location ? { Location: location } : {},
      });
    }) as typeof fetch;
  }

  test("a redirect is refused, so the API key is never sent to the redirect target", async () => {
    const seen: {
      url: string;
      apiKey: string | null;
      redirect?: RequestInit["redirect"];
    }[] = [];
    const client = new Client({
      baseUrl: "https://api.example",
      apiKey: "orst_1_secret",
      fetchImpl: redirectingFetch(seen, "https://evil.example/api/brand"),
    });

    await assert.rejects(() => client.get("/api/brand"));

    assert.equal(seen.length, 1);
    assert.equal(seen[0].url, "https://api.example/api/brand");
    assert.equal(seen[0].apiKey, "orst_1_secret");
    assert.equal(seen[0].redirect, "manual");
  });

  test("the refusal names the redirect target and the origin to configure instead", async () => {
    const client = new Client({
      baseUrl: "http://openresto.example",
      apiKey: "orst_1_secret",
      env: { OPENRESTO_ALLOW_HTTP: "1" },
      fetchImpl: redirectingFetch(
        [],
        "https://openresto.example/api/brand",
        308,
      ),
    });

    await assert.rejects(
      () => client.get("/api/brand"),
      (err: unknown) => {
        assert.ok(err instanceof Error);
        assert.match(err.message, /https:\/\/openresto\.example\/api\/brand/);
        assert.match(err.message, /308/);
        assert.match(err.message, /OPENRESTO_URL/);
        return true;
      },
    );
  });

  test("a redirect carrying no usable Location header is still refused", async () => {
    const client = new Client({
      baseUrl: "https://api.example",
      apiKey: "orst_1_secret",
      fetchImpl: redirectingFetch([], null, 302),
    });

    await assert.rejects(
      () => client.get("/api/brand"),
      (err: unknown) => {
        assert.ok(err instanceof Error);
        assert.match(err.message, /Location header/);
        return true;
      },
    );
  });

  test("a malformed Location is reported, not thrown from URL parsing", async () => {
    const client = new Client({
      baseUrl: "https://api.example",
      apiKey: "orst_1_secret",
      fetchImpl: redirectingFetch([], "http://", 307),
    });

    await assert.rejects(
      () => client.get("/api/brand"),
      (err: unknown) => {
        assert.ok(err instanceof Error);
        assert.match(err.message, /Location header/);
        return true;
      },
    );
  });
});

describe("connection failures", () => {
  test("an unreachable server names the origin and the underlying cause, not bare fetch failed", async () => {
    const client = new Client({
      baseUrl: "https://gone.example",
      fetchImpl: (async () => {
        throw new Error("fetch failed", {
          cause: new Error("getaddrinfo ENOTFOUND gone.example"),
        });
      }) as typeof fetch,
    });

    await assert.rejects(
      () => client.get("/api/brand"),
      (err: unknown) => {
        assert.ok(err instanceof Error);
        assert.match(err.message, /https:\/\/gone\.example/);
        assert.match(err.message, /ENOTFOUND/);
        return true;
      },
    );
  });
});

describe("messages in Spanish", () => {
  const redirectingFetch = (location: string | null, status: number) =>
    (async () =>
      new Response(null, {
        status,
        headers: location ? { Location: location } : {},
      })) as unknown as typeof fetch;
  const EXCEPTION_NAMES =
    /TypeError|ReferenceError|SyntaxError|ApiError|\bError:|at .*\.js/;

  test("plain http to a remote host is refused in Spanish, without exception names", async () => {
    await withLang("es", () => {
      assert.throws(
        () => new Client({ baseUrl: "http://example.com", env: {} }),
        (err: unknown) => {
          assert.ok(err instanceof Error);
          assert.equal(
            err.message,
            "http://example.com no está cifrada, así que tu clave de API viajaría sin protección. " +
              "Usa una dirección https://, o define OPENRESTO_ALLOW_HTTP=1 si de verdad quieres enviar tus credenciales sin cifrar.",
          );
          assert.doesNotMatch(err.message, EXCEPTION_NAMES);
          return true;
        },
      );
    });
  });

  test("the same refusal stays in English under OPENRESTO_LANG=en", async () => {
    await withLang("en", () => {
      assert.throws(
        () => new Client({ baseUrl: "http://example.com", env: {} }),
        /is not encrypted, so your API key would travel in the clear/,
      );
    });
  });

  test("an invalid address and a missing address are explained in Spanish", async () => {
    await withLang("es", () => {
      assert.throws(
        () => new Client({ baseUrl: "no es una url", env: {} }),
        /^Error: "no es una url" no es una dirección de servidor válida\. Usa una dirección completa/,
      );
      assert.throws(
        () => new Client({ baseUrl: "", env: {} }),
        /^Error: No hay una dirección de servidor configurada\. Ejecuta `openresto auth login`/,
      );
    });
  });

  test("a redirect and an unreachable server are reported in Spanish", async () => {
    await withLang("es", async () => {
      const redirected = new Client({
        baseUrl: "https://api.example",
        fetchImpl: redirectingFetch("https://otro.example/api/brand", 308),
      });
      await assert.rejects(
        () => redirected.get("/api/brand"),
        /^Error: https:\/\/api\.example redirigió a https:\/\/otro\.example\/api\/brand \(HTTP 308\)\. openresto no sigue redirecciones/,
      );

      const noTarget = new Client({
        baseUrl: "https://api.example",
        fetchImpl: redirectingFetch(null, 302),
      });
      await assert.rejects(
        () => noTarget.get("/api/brand"),
        /redirigió sin indicar a qué dirección ir \(HTTP 302\)/,
      );

      const gone = new Client({
        baseUrl: "https://gone.example",
        fetchImpl: (async () => {
          throw new Error("fetch failed", { cause: new Error("ENOTFOUND") });
        }) as typeof fetch,
      });
      await assert.rejects(
        () => gone.get("/api/brand"),
        /^Error: No se pudo conectar con https:\/\/gone\.example: ENOTFOUND$/,
      );
    });
  });

  test("a failure with no server message gets the generic text in Spanish; the server's own text stays raw", async () => {
    await withLang("es", async () => {
      const bare = new Client({
        baseUrl: "https://api.example",
        fetchImpl: fakeFetch(() => ({ status: 500 })),
      });
      await assert.rejects(
        () => bare.get("/api/brand"),
        (err: unknown) =>
          err instanceof ApiError &&
          err.message === "La solicitud falló con el estado 500",
      );

      const withMessage = new Client({
        baseUrl: "https://api.example",
        fetchImpl: fakeFetch(() => ({
          status: 400,
          body: { message: "Table 3 does not exist.", code: "table.missing" },
        })),
      });
      await assert.rejects(
        () => withMessage.get("/api/brand"),
        (err: unknown) =>
          err instanceof ApiError && err.message === "Table 3 does not exist.",
      );
    });
  });
});
