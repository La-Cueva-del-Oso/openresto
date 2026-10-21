const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");

// Every command below gets its file names as argv, never spliced into a shell
// string: a staged file called `x$(cmd).md` must stay a file name. The node
// tools run through process.execPath because `npx` is a .cmd shim on Windows
// that only a shell can launch.
// The bin comes from the package's own manifest: a deep `pkg/bin/x` path is
// refused by packages whose `exports` map does not list it (oxlint's).
function toolBin(pkg, from) {
  const manifest = require.resolve(`${pkg}/package.json`, { paths: [from] });
  const { bin } = require(manifest);
  return path.join(
    path.dirname(manifest),
    typeof bin === "string" ? bin : bin[pkg],
  );
}

// A leading "-" would be read as an option.
const asArg = (f) => (f.startsWith("-") ? `./${f}` : f);

// git check-ignore returns 0 (and prints the file) if the path is ignored,
// non-zero otherwise. Used to avoid `git add` aborting on ignored files.
function isIgnored(file) {
  try {
    execFileSync("git", ["check-ignore", "--", file], {
      encoding: "utf8",
      stdio: "pipe",
    });
    return true;
  } catch {
    return false;
  }
}

// Get staged files (-z: names come back raw, not quoted/escaped)
let stagedFiles = [];
try {
  stagedFiles = execFileSync("git", ["diff", "--cached", "--name-only", "-z"], {
    encoding: "utf8",
  })
    .split("\0")
    .filter((f) => f && fs.existsSync(f));
} catch (e) {
  console.error("Failed to get staged files:", e.message);
  process.exit(1);
}

if (stagedFiles.length === 0) {
  console.log("No staged files to process.");
  process.exit(0);
}

console.log(`Processing ${stagedFiles.length} staged files...`);

// 1. Fix line endings (convert to LF)
let modified = false;
stagedFiles.forEach((file) => {
  if (fs.lstatSync(file).isFile()) {
    try {
      const buffer = fs.readFileSync(file);
      const content = buffer.toString("utf8");
      // Simple check for CRLF
      if (content.includes("\r\n")) {
        const lfContent = content.replace(/\r\n/g, "\n");
        fs.writeFileSync(file, lfContent, "utf8");
        console.log(`Fixed line endings (LF): ${file}`);
        modified = true;
      }
    } catch (e) {
      console.warn(`Could not process line endings for ${file}: ${e.message}`);
    }
  }
});

// 2. Prettier
const prettierFiles = stagedFiles.filter((f) =>
  /\.(js|jsx|ts|tsx|json|css|md|yml|yaml)$/.test(f),
);
if (prettierFiles.length > 0) {
  console.log("Running Prettier...");
  try {
    execFileSync(
      process.execPath,
      [toolBin("prettier", __dirname), "--write", ...prettierFiles.map(asArg)],
      { stdio: "inherit" },
    );
    modified = true;
  } catch (e) {
    console.error("Prettier failed, but continuing...");
  }
}

// 3. Frontend Linter (oxlint)
const frontendFiles = stagedFiles.filter(
  (f) => f.startsWith("openresto-frontend/") && /\.(js|jsx|ts|tsx)$/.test(f),
);
if (frontendFiles.length > 0) {
  console.log("Running Frontend Linter (staged files)...");
  // Scope oxlint to the staged files only. Previously this ran `oxlint --fix`
  // across the whole project, which rewrote (and sometimes mangled) files the
  // developer never staged — the changes leaked into the working tree because
  // the re-add step below only re-stages originally-staged files. oxlint runs
  // with cwd=openresto-frontend, so strip that prefix from each path.
  const relFiles = frontendFiles.map((f) =>
    asArg(f.slice("openresto-frontend/".length)),
  );
  try {
    execFileSync(
      process.execPath,
      [
        toolBin("oxlint", path.resolve("openresto-frontend")),
        "--fix",
        ...relFiles,
      ],
      { cwd: "openresto-frontend", stdio: "inherit" },
    );
    modified = true;
  } catch (e) {
    console.error("Frontend linter found errors.");
    process.exit(1);
  }
}

// 4. Backend Linter (dotnet format)
const backendFiles = stagedFiles.filter(
  (f) =>
    /^OpenRestoApi(\.Core|\.Infrastructure)?\//.test(f) && f.endsWith(".cs"),
);
if (backendFiles.length > 0) {
  console.log("Running Backend Linter...");
  try {
    execFileSync(
      "dotnet",
      ["format", "openresto.sln", "--include", ...backendFiles.map(asArg)],
      { stdio: "inherit" },
    );
    modified = true;
  } catch (e) {
    console.error("Backend linter failed.");
    process.exit(1);
  }
}

// 5. Re-add modified files
// Only re-stage files that (a) still exist on disk (deleted files are already
// staged and need no re-add) and (b) are not git-ignored (git add refuses
// ignored files and aborts the commit — e.g. the local SQLite DB after it was
// untracked and added to .gitignore).
if (modified) {
  const reAddable = stagedFiles.filter(
    (f) => fs.existsSync(f) && !isIgnored(f),
  );
  if (reAddable.length > 0) {
    console.log("Re-staging modified files...");
    execFileSync("git", ["add", "--", ...reAddable]);
  }
}

console.log("Pre-commit checks passed!");
