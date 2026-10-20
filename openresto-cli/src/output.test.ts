import { test, describe } from "node:test";
import assert from "node:assert/strict";
import { printResult } from "./output.js";

function captureConsoleLog(fn: () => void): string[] {
  const lines: string[] = [];
  const original = console.log;
  console.log = (...args: unknown[]) => {
    lines.push(args.map(String).join(" "));
  };
  try {
    fn();
  } finally {
    console.log = original;
  }
  return lines;
}

describe("printResult", () => {
  test("json mode prints valid, pretty-printed JSON of the exact data", () => {
    const lines = captureConsoleLog(() =>
      printResult({ id: 1, name: "A" }, true),
    );

    const parsed = JSON.parse(lines.join("\n"));
    assert.deepEqual(parsed, { id: 1, name: "A" });
  });

  test("table mode on an array renders a header row and one row per item", () => {
    const lines = captureConsoleLog(() =>
      printResult(
        [
          { id: 1, name: "A" },
          { id: 2, name: "B" },
        ],
        false,
      ),
    );

    assert.match(lines[0], /id/);
    assert.match(lines[0], /name/);
    assert.match(lines[2], /1/);
    assert.match(lines[2], /A/);
    assert.match(lines[3], /2/);
    assert.match(lines[3], /B/);
  });

  test("table mode on an empty array prints a placeholder instead of an empty table", () => {
    const lines = captureConsoleLog(() => printResult([], false));

    assert.deepEqual(lines, ["(no results)"]);
  });

  test("table mode restricts to explicit columns when given", () => {
    const lines = captureConsoleLog(() =>
      printResult([{ id: 1, name: "A", secret: "shh" }], false, ["id", "name"]),
    );

    assert.doesNotMatch(lines[0], /secret/);
    assert.doesNotMatch(lines[2], /shh/);
  });

  test("table mode on a single object (not an array) prints a key/value record", () => {
    const lines = captureConsoleLog(() =>
      printResult({ id: 1, name: "A" }, false),
    );

    assert.ok(lines.some((l) => l.includes("id") && l.includes("1")));
    assert.ok(lines.some((l) => l.includes("name") && l.includes("A")));
  });

  test("table mode on undefined prints a no-content placeholder rather than throwing", () => {
    const lines = captureConsoleLog(() => printResult(undefined, false));

    assert.deepEqual(lines, ["(no content)"]);
  });

  test("nested object values are rendered as inline JSON, not [object Object]", () => {
    const lines = captureConsoleLog(() =>
      printResult([{ id: 1, meta: { a: 1 } }], false),
    );

    assert.match(lines[2], /\{"a":1\}/);
  });
});

const RAW_CONTROL = /[\u0000-\u001f\u007f-\u009f]/;

describe("terminal control sequences in API data", () => {
  const hostile: [string, string, string][] = [
    ["CSI clear screen", "a\x1b[2Jb", "a\\x1b[2Jb"],
    ["OSC window title", "a\x1b]0;pwned\x07b", "a\\x1b]0;pwned\\x07b"],
    ["carriage return", "real\rfake", "real\\x0dfake"],
    ["backspace", "ab\x08\x08cd", "ab\\x08\\x08cd"],
    ["C1 CSI (U+009B)", "a\u009b2Jb", "a\\x9b2Jb"],
    ["DEL", "a\x7fb", "a\\x7fb"],
    ["NUL", "a\x00b", "a\\x00b"],
  ];

  for (const [name, input, visible] of hostile) {
    test(`table cell: ${name} is shown escaped, never raw`, () => {
      const lines = captureConsoleLog(() =>
        printResult([{ customerName: input }], false),
      );

      for (const line of lines) assert.doesNotMatch(line, RAW_CONTROL);
      assert.ok(lines[2].includes(visible), lines[2]);
    });

    test(`record value: ${name} is shown escaped, never raw`, () => {
      const lines = captureConsoleLog(() =>
        printResult({ customerName: input }, false),
      );

      for (const line of lines) assert.doesNotMatch(line, RAW_CONTROL);
      assert.ok(lines[0].includes(visible), lines[0]);
    });

    test(`record key and column header: ${name} is shown escaped, never raw`, () => {
      const record = captureConsoleLog(() =>
        printResult({ [input]: "v" }, false),
      );
      const table = captureConsoleLog(() =>
        printResult([{ [input]: "v" }], false),
      );

      for (const line of [...record, ...table]) {
        assert.doesNotMatch(line, RAW_CONTROL);
      }
      assert.ok(record[0].includes(visible), record[0]);
      assert.ok(table[0].includes(visible), table[0]);
    });
  }

  test("a newline or tab in a cell stays on its row as \\n and \\t", () => {
    const lines = captureConsoleLog(() =>
      printResult([{ note: "line one\nline two\tend" }], false),
    );

    assert.equal(lines.length, 3);
    assert.ok(lines[2].includes("line one\\nline two\\tend"));
  });

  test("a control character inside a nested object is escaped too", () => {
    const lines = captureConsoleLog(() =>
      printResult([{ meta: { who: "x\u009by" } }], false),
    );

    for (const line of lines) assert.doesNotMatch(line, RAW_CONTROL);
  });

  test("ordinary text (accents, emoji, CJK) is printed untouched", () => {
    const text = "José Núñez 🐻 田中 — «hola»";
    const lines = captureConsoleLog(() =>
      printResult([{ customerName: text }], false),
    );

    assert.ok(lines[2].includes(text));
  });

  test("json mode stays valid JSON and loses no data, with DEL and C1 escaped", () => {
    const data = {
      name: "a\x1b[2J\x07b\r\n\u009b\x7f c 🐻",
      nested: [{ note: "é\u0085" }],
    };

    const out = captureConsoleLog(() => printResult(data, true)).join("\n");

    assert.doesNotMatch(out.replace(/\n/g, ""), RAW_CONTROL);
    assert.deepEqual(JSON.parse(out), data);
  });
});
