import { chmodSync, existsSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { createHash, pbkdf2Sync } from "node:crypto";
import { tmpdir } from "node:os";
import { delimiter, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import assert from "node:assert/strict";
import { describe, it } from "node:test";

const script = join(dirname(fileURLToPath(import.meta.url)), "reset-admin.sh");

const probe = (cmd, args) => !spawnSync(cmd, args, { encoding: "utf8" }).error;
const python = ["python3", "python"].find((p) => probe(p, ["--version"]));
const missing = !probe("bash", ["-c", "true"])
  ? "no bash on this machine"
  : !probe("sqlite3", ["--version"])
    ? "no sqlite3 on this machine"
    : !python && "no python3 on this machine";

/**
 * scripts/reset-admin.sh runs against `docker exec <container> sqlite3 ...`; a stand-in `docker`
 * that runs the same sqlite3 against a temp database lets the real script execute end to end.
 * A `python3` stand-in records its argv, because the hash is computed there and the password
 * must not reach it (or anything else) as an argument.
 */
function sandbox({ pushTable = true } = {}) {
  const dir = mkdtempSync(join(tmpdir(), "reset-admin-"));
  const db = join(dir, "openresto.db").replaceAll("\\", "/");
  const log = join(dir, "argv.log").replaceAll("\\", "/");
  const realPython = spawnSync(python, ["-c", "import sys;print(sys.executable)"], { encoding: "utf8" })
    .stdout.trim().replaceAll("\\", "/");

  const shim = (name, body) => {
    const file = join(dir, name);
    writeFileSync(file, `#!/usr/bin/env bash\nprintf '%s\\n' "${name} $*" >> "$SHIM_LOG"\n${body}\n`);
    chmodSync(file, 0o755);
  };
  shim("docker", [
    'if [[ "$1" == compose ]]; then echo fakecid; exit 0; fi',
    "shift",
    '[[ "$1" == -u ]] && shift 2',
    "shift",
    '[[ "$1" == sh ]] && exit 0',
    "shift 2",
    'exec sqlite3 "$TEST_DB" "$@"',
  ].join("\n"));
  shim("python3", `exec "${realPython}" "$@"`);

  const sql = (statement) => spawnSync("sqlite3", [db, statement], { encoding: "utf8" });
  sql(`CREATE TABLE AdminCredentials (
    Id INTEGER PRIMARY KEY, Email TEXT, PasswordHash TEXT, PasswordSalt TEXT, Role TEXT,
    IsActive INTEGER, SessionVersion INTEGER, PvqQuestion TEXT, PvqAnswerHash TEXT, PvqAnswerSalt TEXT,
    PvqFailedAttempts INTEGER, PvqLockedUntil TEXT, ResetToken TEXT, ResetTokenExpiry TEXT);
    INSERT INTO AdminCredentials VALUES
      (1,'a@b.com','old','old','Manager',0,4,'q','h','s',3,'2999-01-01','t','2999-01-01'),
      (2,'other@b.com','keep','keep','Manager',1,7,'q','h','s',2,'2999-01-01',NULL,NULL);`);
  if (pushTable)
    sql(`CREATE TABLE AdminPushSubscriptions (Id INTEGER PRIMARY KEY, UserId INTEGER, Endpoint TEXT);
      INSERT INTO AdminPushSubscriptions VALUES (1,1,'https://push/intruder'),(2,1,'https://push/second'),(3,2,'https://push/other');`);

  const run = (args, input = "") =>
    spawnSync("bash", [script, ...args], {
      input,
      encoding: "utf8",
      env: {
        ...process.env,
        PATH: `${dir}${delimiter}${process.env.PATH}`,
        TEST_DB: db,
        SHIM_LOG: log,
      },
    });
  const row = (id) =>
    sql(`SELECT json_object('hash',PasswordHash,'salt',PasswordSalt,'role',Role,'active',IsActive,
      'session',SessionVersion,'pvq',PvqQuestion,'failed',PvqFailedAttempts,'locked',PvqLockedUntil,
      'token',ResetToken) FROM AdminCredentials WHERE Id=${id};`).stdout.trim();
  const argv = () => (existsSync(log) ? readFileSync(log, "utf8") : "");
  const pushOwners = () => sql("SELECT group_concat(UserId) FROM AdminPushSubscriptions ORDER BY Id;").stdout.trim();
  return { run, row: (id) => JSON.parse(row(id)), argv, pushOwners };
}

const pbkdf2 = (password, saltB64) =>
  pbkdf2Sync(password, Buffer.from(saltB64, "base64"), 100_000, 32, "sha256").toString("base64");

describe("scripts/reset-admin.sh", { skip: missing }, () => {
  const fresh = "n3w-" + createHash("sha1").update("fixture").digest("hex").slice(0, 8);

  it("takes the password from stdin and resets the account, its PVQ lockout and its sessions", () => {
    const box = sandbox();
    const { status, stderr } = box.run(["--email", "a@b.com", "--password-stdin"], `${fresh}\n`);
    assert.equal(status, 0, stderr);

    const reset = box.row(1);
    assert.equal(reset.hash, pbkdf2(fresh, reset.salt), "the stored hash is of the stdin value");
    assert.equal(reset.role, "Owner");
    assert.equal(reset.active, 1);
    assert.equal(reset.session, 5, "SessionVersion goes up so old sessions stop working");
    assert.equal(reset.pvq, null);
    assert.equal(reset.failed, 0, "a failed-attempt count would keep the recovery question locked");
    assert.equal(reset.locked, null);
    assert.equal(reset.token, null);

    assert.deepEqual(
      [box.row(2).hash, box.row(2).failed, box.row(2).session],
      ["keep", 2, 7],
      "other accounts are left alone",
    );
  });

  it("drops the recovered account's push subscriptions and keeps other accounts'", () => {
    const box = sandbox();
    assert.equal(box.pushOwners(), "1,1,2");
    const { status, stderr } = box.run(["--email", "a@b.com", "--password-stdin"], `${fresh}\n`);
    assert.equal(status, 0, stderr);
    assert.equal(box.pushOwners(), "2", "a browser registered on the recovered account must stop receiving bookings");
  });

  it("rolls the whole reset back when the subscriptions cannot be dropped", () => {
    const box = sandbox({ pushTable: false });
    const { status } = box.run(["--email", "a@b.com", "--password-stdin"], `${fresh}\n`);
    assert.notEqual(status, 0, "a recovery that left the push rows in place reported success");
    assert.equal(box.row(1).hash, "old", "the credential change was committed without the push drop");
    assert.equal(box.row(1).session, 4);
  });

  it("never lets the password reach a command line", () => {
    const box = sandbox();
    assert.equal(box.run(["--email", "a@b.com", "--password-stdin"], `${fresh}\n`).status, 0);

    const argv = box.argv();
    assert.match(argv, /python3 -c /, "the hashing step ran through the stand-in");
    assert.ok(!argv.includes(fresh), "the password appeared in a recorded argv");
  });

  it("refuses --password in both spellings, and touches nothing", () => {
    for (const flag of [["--password", fresh], [`--password=${fresh}`]]) {
      const box = sandbox();
      const { status, stderr } = box.run(["--email", "a@b.com", ...flag]);
      assert.notEqual(status, 0, `${flag[0]} was accepted`);
      assert.match(stderr, /--password-stdin/);
      assert.equal(box.argv(), "", "nothing ran before the refusal");
      assert.equal(box.row(1).hash, "old");
    }
  });

  it("refuses --password-stdin with nothing on stdin, and touches nothing", () => {
    const box = sandbox();
    const { status, stdout, stderr } = box.run(["--email", "a@b.com", "--password-stdin"], "");
    assert.notEqual(status, 0);
    assert.match(stdout + stderr, /no password was read from stdin/);
    assert.equal(box.row(1).hash, "old");
    assert.equal(box.row(1).failed, 3);
  });
});
