import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { describe, it } from "node:test";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");

/**
 * A guest's email is the second half of the booking lookup and travels in the query string, so
 * an access log that records the request line (or the raw Referer, which carries the page's
 * query) records the email. Every access_log in the shipped nginx configs names a format that
 * logs the path only.
 */
for (const dir of ["nginx", "nginx-vps"]) {
  describe(`${dir}/default.conf.template access log`, () => {
    const conf = readFileSync(join(root, dir, "default.conf.template"), "utf8");
    const format = conf.match(/log_format\s+no_query\s+((?:'[^']*'\s*)+);/)?.[1] ?? "";
    const logs = [...conf.matchAll(/^\s*access_log\s+([^;]+);/gm)].map((m) => m[1].trim());

    it("defines a format without the request line, query string or raw referer", () => {
      assert.ok(format, "log_format no_query is missing");
      assert.doesNotMatch(format, /\$(request|request_uri|args|query_string|http_referer)\b/);
      assert.match(format, /\$uri\b/);
    });

    it("sends every enabled access_log through that format", () => {
      const enabled = logs.filter((l) => l !== "off");
      assert.ok(enabled.length > 0);
      for (const log of enabled) assert.match(log, /\bno_query$/, `access_log ${log}`);
    });

    it("strips the query from the referer it does log", () => {
      assert.match(conf, /map \$http_referer \$referer_path \{[^}]*\[\^\?\]\+/);
      assert.match(format, /\$referer_path/);
    });
  });
}
