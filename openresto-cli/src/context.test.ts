import { test, describe } from "node:test";
import assert from "node:assert/strict";
import { printError, handle } from "./context.js";
import { ApiError } from "./transport.js";
import { withLang } from "./withLang.test.js";

process.env.OPENRESTO_LANG = "en";

function captureConsoleError(fn: () => void): string[] {
  const lines: string[] = [];
  const original = console.error;
  console.error = (...args: unknown[]) => {
    lines.push(args.map(String).join(" "));
  };
  try {
    fn();
  } finally {
    console.error = original;
  }
  return lines;
}

describe("printError", () => {
  test("renders an ApiError with its status and server message", () => {
    const lines = captureConsoleError(() =>
      printError(
        new ApiError(
          404,
          "No booking found matching that reference and email.",
          "booking.lookup_not_found",
        ),
      ),
    );

    assert.match(lines[0], /404/);
    assert.match(lines[0], /booking\.lookup_not_found/);
    assert.match(lines[0], /No booking found/);
  });

  test("renders an ApiError with no code without printing 'undefined'", () => {
    const lines = captureConsoleError(() =>
      printError(new ApiError(500, "boom")),
    );

    assert.doesNotMatch(lines[0], /undefined/);
  });

  test("renders a plain Error's message", () => {
    const lines = captureConsoleError(() =>
      printError(new Error("A server URL is required.")),
    );

    assert.match(lines[0], /A server URL is required\./);
  });

  test("an API message carrying escape sequences is printed escaped, never raw", () => {
    const lines = captureConsoleError(() =>
      printError(
        new ApiError(400, "bad \x1b[2J name\r\u009b\x08", "x.\x1b[0m"),
      ),
    );

    assert.doesNotMatch(
      lines.join(""),
      /[\u0000-\u0009\u000b-\u001f\u007f-\u009f]/,
    );
    assert.match(lines[0], /bad \\x1b\[2J name\\x0d\\x9b\\x08/);
    assert.match(lines[0], /\[x\.\\x1b\[0m\]/);
  });

  test("a plain Error's message is neutralized and keeps its line breaks", () => {
    const lines = captureConsoleError(() =>
      printError(new Error("one\ntwo\x1b]0;t\x07")),
    );

    assert.equal(lines[0], "Error: one\ntwo\\x1b]0;t\\x07");
  });

  test("renders a non-Error thrown value without crashing", () => {
    const lines = captureConsoleError(() => printError("just a string"));

    assert.ok(lines.length > 0);
  });
});

describe("printError in Spanish", () => {
  test("the prefix and the status are Spanish-neutral and the server's message is left raw", async () => {
    const lines = await withLang("es", () =>
      captureConsoleError(() =>
        printError(
          new ApiError(404, "No booking found.", "booking.lookup_not_found"),
        ),
      ),
    );

    assert.equal(
      lines[0],
      "Error (404) [booking.lookup_not_found]: No booking found.",
    );
  });

  test("a hostile server message is escaped in Spanish too", async () => {
    const lines = await withLang("es", () =>
      captureConsoleError(() =>
        printError(new ApiError(400, "bad \x1b[2J name", "x")),
      ),
    );

    assert.doesNotMatch(lines[0], /[\u0000-\u0009\u000b-\u001f\u007f-\u009f]/);
    assert.match(lines[0], /bad \\x1b\[2J name/);
  });
});

describe("handle", () => {
  test("a successful action leaves exitCode untouched", async () => {
    const original = process.exitCode;
    process.exitCode = undefined;
    const action = handle(async (..._args: unknown[]) => {});

    await action({}, {});

    assert.equal(process.exitCode, undefined);
    process.exitCode = original;
  });

  test("a thrown error is caught, printed, and sets exit code 1 instead of propagating", async () => {
    const original = process.exitCode;
    process.exitCode = undefined;
    const action = handle(async (..._args: unknown[]) => {
      throw new Error("nope");
    });

    const originalConsoleError = console.error;
    const lines: string[] = [];
    console.error = (...args: unknown[]) =>
      lines.push(args.map(String).join(" "));
    try {
      await assert.doesNotReject(() => action({}, {}));
    } finally {
      console.error = originalConsoleError;
    }

    assert.equal(process.exitCode, 1);
    assert.match(lines[0], /nope/);
    process.exitCode = original;
  });
});
