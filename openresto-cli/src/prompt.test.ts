import { test, describe } from "node:test";
import assert from "node:assert/strict";
import { PassThrough } from "node:stream";
import { promptHidden } from "./prompt.js";
import { withLang } from "./withLang.test.js";

process.env.OPENRESTO_LANG = "en";

function fakeTty(): { stdin: NodeJS.ReadStream; stdout: NodeJS.WriteStream } {
  const stdin = Object.assign(new PassThrough(), {
    isRaw: false,
    setRawMode() {
      return this;
    },
  });
  const stdout = new PassThrough();
  return {
    stdin: stdin as unknown as NodeJS.ReadStream,
    stdout: stdout as unknown as NodeJS.WriteStream,
  };
}

describe("promptHidden", () => {
  test("returns the typed value on Enter", async () => {
    const { stdin, stdout } = fakeTty();
    const pending = promptHidden("API key", { stdin, stdout });
    stdin.write("secret\r");

    assert.equal(await pending, "secret");
  });

  test("Ctrl+C aborts with the English message", async () => {
    const { stdin, stdout } = fakeTty();
    const pending = promptHidden("API key", { stdin, stdout });
    stdin.write("\u0003");

    await assert.rejects(pending, /^Error: Aborted\.$/);
  });

  test("Ctrl+C aborts with the Spanish message under OPENRESTO_LANG=es", async () => {
    await withLang("es", async () => {
      const { stdin, stdout } = fakeTty();
      const pending = promptHidden("Clave de API", { stdin, stdout });
      stdin.write("\u0003");

      await assert.rejects(pending, /^Error: Operación cancelada\.$/);
    });
  });
});
