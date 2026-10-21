import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import assert from "node:assert/strict";
import { describe, it } from "node:test";

const script = join(dirname(fileURLToPath(import.meta.url)), "demo_data.py");

const probe = (cmd, args) => !spawnSync(cmd, args, { encoding: "utf8" }).error;
const python = ["python3", "python"].find((p) => probe(p, ["--version"]));
const missing = !python
  ? "no python3 on this machine"
  : !probe("sqlite3", ["--version"]) && "no sqlite3 on this machine";

const mediaSql = (mediaDir) =>
  spawnSync(python, [script, "media", "--media-dir", mediaDir], {
    encoding: "utf8",
    env: { ...process.env, PYTHONIOENCODING: "utf-8" },
  });

/** Applies generated SQL to an in-memory database that holds a table the SQL must never touch. */
const applyToScratchDb = (sql) =>
  spawnSync("sqlite3", [":memory:"], {
    encoding: "utf8",
    input: `CREATE TABLE x (a); CREATE TABLE BrandSettings (HeaderImageUrl TEXT);
      INSERT INTO BrandSettings VALUES (NULL);
      CREATE TABLE Restaurants (Id INTEGER, ImageUrl TEXT, MenuUrl TEXT); INSERT INTO Restaurants (Id) VALUES (1);
      ${sql}
      SELECT 'x:' || count(*) FROM sqlite_master WHERE name = 'x';
      SELECT 'hero:' || ifnull(HeaderImageUrl, '') FROM BrandSettings;`,
  });

describe("scripts/demo_data.py media section", { skip: missing }, () => {
  it("links the files found in --media-dir, and applies cleanly", () => {
    const dir = mkdtempSync(join(tmpdir(), "demo-media-"));
    writeFileSync(join(dir, "hero.jpg"), "x");
    const { status, stdout, stderr } = mediaSql(dir);
    assert.equal(status, 0, stderr);
    assert.match(stdout, /UPDATE BrandSettings SET HeaderImageUrl='\/media\/hero\.jpg\?v=\d+'/);

    const applied = applyToScratchDb(stdout);
    assert.equal(applied.status, 0, applied.stderr);
    assert.match(applied.stdout, /x:1/);
    assert.match(applied.stdout, /hero:\/media\/hero\.jpg\?v=\d+/);
  });

  it("does not copy the directory path into the SQL, so a line break in it cannot add statements", () => {
    // Neither directory exists: the comment is written before the existence check.
    for (const dir of ["missing\nDROP TABLE x;--", "missing\rDROP TABLE x;--", "a\r\nDROP TABLE x;--"]) {
      const { status, stdout, stderr } = mediaSql(join(tmpdir(), dir));
      assert.equal(status, 0, stderr);
      assert.ok(!stdout.includes("DROP TABLE"), `the path reached the SQL: ${JSON.stringify(dir)}`);

      const applied = applyToScratchDb(stdout);
      assert.equal(applied.status, 0, applied.stderr);
      assert.match(applied.stdout, /x:1/, "the injected statement dropped a table");
    }
  });
});
