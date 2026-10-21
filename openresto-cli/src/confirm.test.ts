import { test, describe } from "node:test";
import assert from "node:assert/strict";
import { Readable, Writable } from "node:stream";
import { confirmOrExit, ConfirmationRequiredError } from "./confirm.js";

function fakeWritable(): Writable {
  return new Writable({
    write(_chunk, _enc, callback) {
      callback();
    },
  });
}

describe("confirmOrExit", () => {
  test("--yes proceeds without touching stdin at all", async () => {
    await assert.doesNotReject(() =>
      confirmOrExit("Destroy something", true, { isTTY: false }),
    );
  });

  test("no --yes and no TTY (piped/CI) is refused rather than hanging on a prompt", async () => {
    await assert.rejects(
      () => confirmOrExit("Destroy something", false, { isTTY: false }),
      ConfirmationRequiredError,
    );
  });

  test("the refusal message mentions --yes so the caller knows how to proceed non-interactively", async () => {
    await assert.rejects(
      () => confirmOrExit("Destroy something", false, { isTTY: false }),
      /--yes/,
    );
  });

  test("an interactive 'y' answer proceeds", async () => {
    const stdin = Readable.from(["y\n"]);
    await assert.doesNotReject(() =>
      confirmOrExit("Destroy something", false, {
        isTTY: true,
        stdin,
        stdout: fakeWritable(),
      }),
    );
  });

  test("an interactive 'yes' answer proceeds", async () => {
    const stdin = Readable.from(["yes\n"]);
    await assert.doesNotReject(() =>
      confirmOrExit("Destroy something", false, {
        isTTY: true,
        stdin,
        stdout: fakeWritable(),
      }),
    );
  });

  test("an interactive empty/other answer is refused", async () => {
    const stdin = Readable.from(["\n"]);
    await assert.rejects(
      () =>
        confirmOrExit("Destroy something", false, {
          isTTY: true,
          stdin,
          stdout: fakeWritable(),
        }),
      ConfirmationRequiredError,
    );
  });

  test("an interactive 'n' answer is refused", async () => {
    const stdin = Readable.from(["n\n"]);
    await assert.rejects(
      () =>
        confirmOrExit("Destroy something", false, {
          isTTY: true,
          stdin,
          stdout: fakeWritable(),
        }),
      ConfirmationRequiredError,
    );
  });
});

describe("confirmOrExit with a message carrying API text", () => {
  // A location name is free text a Manager sets; this one clears the screen and writes to the
  // clipboard (OSC 52) if it reaches a terminal raw.
  const hostile = "Cafe \u001b[2J\u001b]52;c;ZXZpbA==\u0007 Norte\u009b31m";
  const raw = /[\u0000-\u0008\u000b-\u001f\u007f-\u009f]/;

  function recordingWritable(): { out: Writable; written: () => string } {
    const chunks: string[] = [];
    const out = new Writable({
      write(chunk, _enc, callback) {
        chunks.push(String(chunk));
        callback();
      },
    });
    return { out, written: () => chunks.join("") };
  }

  test("the prompt written to the terminal has no raw control byte", async () => {
    const { out, written } = recordingWritable();
    await confirmOrExit(`Delete "${hostile}".`, false, {
      isTTY: true,
      stdin: Readable.from(["y\n"]),
      stdout: out,
    });

    assert.doesNotMatch(written(), raw);
    assert.match(written(), /\\x1b\[2J\\x1b\]52;c;ZXZpbA==\\x07 Norte\\x9b31m/);
  });

  test("the non-interactive refusal has no raw control byte either", async () => {
    await assert.rejects(
      () => confirmOrExit(`Delete "${hostile}".`, false, { isTTY: false }),
      (err: Error) => !raw.test(err.message) && /\\x1b/.test(err.message),
    );
  });

  test("an ordinary name passes through unchanged", async () => {
    const { out, written } = recordingWritable();
    await confirmOrExit('Delete "Café Norte ☕" (3 tables).', false, {
      isTTY: true,
      stdin: Readable.from(["y\n"]),
      stdout: out,
    });

    assert.match(written(), /Delete "Café Norte ☕" \(3 tables\)\. \[y\/N\] /);
  });
});
