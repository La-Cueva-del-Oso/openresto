import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { describe, it } from "node:test";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const lines = (file) =>
  readFileSync(join(repo, file), "utf8")
    .split(/\r?\n/)
    .filter((l) => l && !l.startsWith("#"));

const LOCAL_SETTINGS = "appsettings.Local.json";

describe("backend build context", () => {
  it("is still read by the API, which is what makes it worth excluding", () => {
    assert.match(readFileSync(join(repo, "OpenRestoApi/Program.cs"), "utf8"), /"appsettings\.Local\.json"/);
  });

  it("leaves appsettings.Local.json out of the image build", () => {
    const patterns = lines("OpenRestoApi/Dockerfile.dockerignore");
    const exclusion = patterns.lastIndexOf(`**/${LOCAL_SETTINGS}`);
    assert.notEqual(exclusion, -1, "COPY . . would bake the file into the image");
    // The last matching pattern wins, so the exclusion has to come after the folder re-includes.
    assert.ok(exclusion > patterns.lastIndexOf("!OpenRestoApi/"), "the re-include of OpenRestoApi/ overrides it");
  });

  it("is excluded from the repo-wide fallback ignore file as well", () => {
    assert.ok(lines(".dockerignore").includes(`**/${LOCAL_SETTINGS}`));
  });
});
