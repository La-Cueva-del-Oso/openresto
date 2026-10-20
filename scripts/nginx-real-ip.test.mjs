import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import assert from "node:assert/strict";
import { describe, it } from "node:test";

const nginxDir = join(dirname(fileURLToPath(import.meta.url)), "..", "nginx");

const sh = (args, env = {}) =>
  spawnSync("sh", args, { cwd: nginxDir, env: { ...process.env, ...env }, encoding: "utf8" });

const hasSh = !sh(["-c", "true"]).error;
const realIp = (trusted) => sh(["real-ip.sh"], { OPENRESTO_TRUSTED_PROXIES: trusted });

/**
 * nginx/real-ip.sh turns OPENRESTO_TRUSTED_PROXIES into the directives that let the stack's
 * nginx read the visitor's address out of X-Forwarded-For. Trusting nothing is the default, and
 * a bad value must stop the container rather than reach the config.
 */
describe("nginx/real-ip.sh", { skip: !hasSh && "no POSIX sh on this machine" }, () => {
  it("trusts nothing when the variable is unset or blank", () => {
    for (const value of ["", "  ", ","]) {
      const { status, stdout } = realIp(value);
      assert.equal(status, 0);
      assert.equal(stdout, "", `'${value}' must not enable real_ip`);
    }
  });

  it("emits one set_real_ip_from per entry, then the header and recursion", () => {
    const { status, stdout } = realIp("127.0.0.1, 172.18.0.0/16,fd00::/8");
    assert.equal(status, 0);
    assert.deepEqual(
      stdout.split("\n").filter(Boolean).map((line) => line.trim()),
      [
        "set_real_ip_from 127.0.0.1;",
        "set_real_ip_from 172.18.0.0/16;",
        "set_real_ip_from fd00::/8;",
        "real_ip_header X-Forwarded-For;",
        "real_ip_recursive on;",
      ],
    );
  });

  it("rejects an entry that is not an address, so nothing is injected into the config", () => {
    for (const bad of ["1.2.3.4; evil", "10.0.0.0/8,$(id)", "*", "any host"]) {
      const { status, stdout, stderr } = realIp(bad);
      assert.notEqual(status, 0, `'${bad}' was accepted`);
      assert.match(stderr, /OPENRESTO_TRUSTED_PROXIES/);
      assert.ok(!stdout.includes("evil"), `'${bad}' reached the output`);
    }
  });
});

/**
 * envsubst only replaces the variables start.sh names, so a template placeholder missing from
 * that list ships literally into nginx.conf and the feature silently does nothing.
 */
describe("nginx real_ip wiring", () => {
  const read = (file) => readFileSync(join(nginxDir, file), "utf8");

  it("renders REAL_IP_CONFIG into the server block and substitutes it", () => {
    assert.match(read("default.conf.template"), /^\$\{REAL_IP_CONFIG\}$/m);
    assert.match(read("start.sh"), /envsubst '[^']*\$\{REAL_IP_CONFIG\}/);
  });

  it("ships real-ip.sh in the image", () => {
    assert.match(read("Dockerfile"), /COPY real-ip\.sh \/real-ip\.sh/);
  });
});
