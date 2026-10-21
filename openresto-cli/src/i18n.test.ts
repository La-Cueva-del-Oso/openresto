import { test, describe } from "node:test";
import assert from "node:assert/strict";
import { LANG_ENV, messageTables, resolveLanguage, t } from "./i18n.js";

describe("resolveLanguage", () => {
  test("OPENRESTO_LANG=es wins over an English system", () => {
    assert.equal(resolveLanguage({ [LANG_ENV]: "es" }, "en-US"), "es");
  });

  test("OPENRESTO_LANG=en wins over a Spanish system", () => {
    assert.equal(resolveLanguage({ [LANG_ENV]: "en" }, "es-CL"), "en");
  });

  test("the value is read case-insensitively and trimmed", () => {
    assert.equal(resolveLanguage({ [LANG_ENV]: " ES " }, "en-US"), "es");
  });

  for (const bad of ["", "   ", "fr", "es-CL", "klingon"]) {
    test(`an empty or unsupported OPENRESTO_LANG (${JSON.stringify(bad)}) falls back to the system`, () => {
      assert.equal(resolveLanguage({ [LANG_ENV]: bad }, "es-CL"), "es");
      assert.equal(resolveLanguage({ [LANG_ENV]: bad }, "en-US"), "en");
    });
  }

  test("an unset OPENRESTO_LANG uses the system language", () => {
    assert.equal(resolveLanguage({}, "es-ES"), "es");
    assert.equal(resolveLanguage({}, "en-US"), "en");
  });

  for (const locale of ["es", "es-CL", "es-ES", "es_MX", "ES-ar"]) {
    test(`system locale ${locale} is Spanish`, () => {
      assert.equal(resolveLanguage({}, locale), "es");
    });
  }

  for (const locale of ["en-US", "en", "fr", "fr-FR", "de-DE", "est", ""]) {
    test(`system locale ${JSON.stringify(locale)} is English`, () => {
      assert.equal(resolveLanguage({}, locale), "en");
    });
  }
});

describe("message tables", () => {
  test("es has exactly the keys en has", () => {
    assert.deepEqual(
      Object.keys(messageTables.es).sort(),
      Object.keys(messageTables.en).sort(),
    );
  });

  test("every message uses the same placeholders in both languages", () => {
    const names = (text: string) =>
      [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
    for (const key of Object.keys(messageTables.en) as Array<
      keyof typeof messageTables.en
    >) {
      assert.deepEqual(
        names(messageTables.es[key]),
        names(messageTables.en[key]),
        key,
      );
    }
  });

  test("no Spanish message is a copy of the English one, except the shared error prefixes", () => {
    const shared = new Set(["error.api", "error.generic", "io.stdin"]);
    for (const key of Object.keys(messageTables.en) as Array<
      keyof typeof messageTables.en
    >) {
      if (shared.has(key)) continue;
      assert.notEqual(messageTables.es[key], messageTables.en[key], key);
    }
  });
});

describe("t", () => {
  test("interpolates every placeholder, in either language", () => {
    assert.equal(
      t("location.paused", { id: 7, minutes: 30 }, "en"),
      "Location 7 paused for 30 minute(s).",
    );
    assert.equal(
      t("location.paused", { id: 7, minutes: 30 }, "es"),
      "Ubicación 7 en pausa por 30 minuto(s).",
    );
  });

  test("a value cannot expand into another placeholder or use $ replacement patterns", () => {
    assert.equal(
      t("booking.cancelled", { id: "{id} $& $1" }, "en"),
      "Booking {id} $& $1 cancelled.",
    );
  });

  test("a placeholder with no parameter is left visible rather than blanked", () => {
    assert.equal(t("booking.cancelled", {}, "en"), "Booking {id} cancelled.");
  });

  for (const lang of ["en", "es"] as const) {
    test(`a hostile parameter is printed escaped, never raw (${lang})`, () => {
      const out = t(
        "location.deleteConfirm",
        {
          name: "Cafe \x1b[31m\x1b]0;x\x07 Norte\u009b",
          sections: 1,
          tables: 2,
          bookings: 3,
        },
        lang,
      );

      assert.doesNotMatch(out, /[\u0000-\u001f\u007f-\u009f]/);
      assert.match(out, /Cafe \\x1b\[31m\\x1b\]0;x\\x07 Norte\\x9b/);
    });
  }

  test("a line break in a parameter survives, as in any error message", () => {
    assert.equal(
      t("error.generic", { message: "one\ntwo" }, "en"),
      "Error: one\ntwo",
    );
  });

  test("defaults to the language resolved from the environment", () => {
    const original = process.env[LANG_ENV];
    try {
      process.env[LANG_ENV] = "es";
      assert.equal(t("output.noResults"), "(sin resultados)");
      process.env[LANG_ENV] = "en";
      assert.equal(t("output.noResults"), "(no results)");
    } finally {
      if (original === undefined) delete process.env[LANG_ENV];
      else process.env[LANG_ENV] = original;
    }
  });
});
