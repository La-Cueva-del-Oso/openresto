import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { after, before, describe, it } from "node:test";

const hook = join(dirname(fileURLToPath(import.meta.url)), "pre-commit.js");

let repo;
const git = (...args) => execFileSync("git", args, { cwd: repo, encoding: "utf8" });
const runHook = () => spawnSync(process.execPath, [hook], { cwd: repo, encoding: "utf8" });

/**
 * scripts/pre-commit.js hands staged file names to git, prettier and dotnet. They are argv
 * entries, never part of a shell string, so a name carrying shell syntax is only a name.
 */
describe("scripts/pre-commit.js", () => {
  before(() => {
    repo = mkdtempSync(join(tmpdir(), "openresto-precommit-"));
    git("init", "-q");
  });
  after(() => rmSync(repo, { recursive: true, force: true }));

  it("does not run commands smuggled into a staged file name", () => {
    // Windows forbids `"` in names, so the payloads use characters every platform allows.
    const names = ["a$(touch PWNED1).json", "b;touch PWNED2;.md", "c`touch PWNED3`.json"];
    for (const name of names) writeFileSync(join(repo, name), "{}\n");
    git("add", "--", ...names);

    const { status, stderr } = runHook();

    assert.equal(status, 0, stderr);
    for (const marker of ["PWNED1", "PWNED2", "PWNED3"]) {
      assert.equal(existsSync(join(repo, marker)), false, `${marker} was created by the hook`);
    }
  });

  it("converts CRLF to LF in the staged copy and keeps names with spaces and accents", () => {
    const name = "con espacio y ñandú.json";
    writeFileSync(join(repo, name), '{\r\n  "a": 1\r\n}\r\n');
    git("add", "--", name);

    const { status, stderr } = runHook();

    assert.equal(status, 0, stderr);
    assert.doesNotMatch(git("show", `:${name}`), /\r/);
  });

  const frontendModules = join(dirname(hook), "..", "openresto-frontend", "node_modules");
  it(
    "lints a staged frontend file with the frontend's own oxlint",
    { skip: !existsSync(join(frontendModules, "oxlint")) && "frontend dependencies not installed" },
    () => {
      mkdirSync(join(repo, "openresto-frontend"));
      symlinkSync(frontendModules, join(repo, "openresto-frontend", "node_modules"), "junction");
      const file = "openresto-frontend/clean.ts";
      writeFileSync(join(repo, file), "export const answer = 42;\n");
      git("add", "--", file);

      const { status, stderr } = runHook();

      assert.equal(status, 0, stderr);
      git("rm", "-q", "--cached", "--", file);
    },
  );

  it("succeeds with nothing staged", () => {
    git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "x");
    const { status, stdout } = runHook();
    assert.equal(status, 0);
    assert.match(stdout, /No staged files/);
  });
});
