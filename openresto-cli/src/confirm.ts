import { createInterface } from "node:readline/promises";
import { t } from "./i18n.js";
import { neutralizeControl } from "./output.js";

/** Both languages' yes, whichever one the prompt was drawn in: `y`/`yes`, `s`/`si`/`sí`. */
const AFFIRMATIVE = /^(y(es)?|s[ií]?)$/i;

export class ConfirmationRequiredError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ConfirmationRequiredError";
  }
}

export interface ConfirmDeps {
  isTTY?: boolean;
  stdin?: NodeJS.ReadableStream;
  stdout?: NodeJS.WritableStream;
}

/**
 * Gates a destructive command: `--yes` always proceeds; otherwise an interactive TTY is prompted
 * y/N; a non-interactive session (piped input, CI) with no `--yes` is refused rather than
 * silently blocking on a prompt nobody can answer. The message routinely carries a name from the
 * API (a location's), so it is neutralized here, where every caller's prompt and refusal pass.
 *
 * @see [confirm.test.ts](./confirm.test.ts) — pins that no raw escape or BEL reaches the prompt.
 */
export async function confirmOrExit(
  rawMessage: string,
  yes: boolean,
  deps: ConfirmDeps = {},
): Promise<void> {
  if (yes) {
    return;
  }
  const message = neutralizeControl(rawMessage, true);

  const isTTY = deps.isTTY ?? Boolean(process.stdin.isTTY);
  if (!isTTY) {
    throw new ConfirmationRequiredError(t("confirm.notTerminal", { message }));
  }

  const rl = createInterface({
    input: deps.stdin ?? process.stdin,
    output: deps.stdout ?? process.stdout,
  });
  try {
    const answer = await rl.question(`${message} ${t("confirm.choices")} `);
    if (!AFFIRMATIVE.test(answer.trim())) {
      throw new ConfirmationRequiredError(t("confirm.declined"));
    }
  } finally {
    rl.close();
  }
}
