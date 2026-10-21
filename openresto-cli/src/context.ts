import type { Command } from "commander";
import { resolveProfile, type ResolvedProfile } from "./config.js";
import { ApiError, Client } from "./transport.js";
import { t } from "./i18n.js";
import { neutralizeControl } from "./output.js";

export interface GlobalOptions {
  json?: boolean;
  profile?: string;
  yes?: boolean;
}

export function getGlobalOptions(cmd: Command): GlobalOptions {
  return cmd.optsWithGlobals() as GlobalOptions;
}

export function clientFor(cmd: Command): {
  client: Client;
  profile: ResolvedProfile;
} {
  const opts = getGlobalOptions(cmd);
  const profile = resolveProfile({ profileName: opts.profile });
  const client = new Client({ baseUrl: profile.url, apiKey: profile.apiKey });
  return { client, profile };
}

/**
 * Wraps a command action so a thrown error becomes a readable one-line message on stderr and
 * exit code 1, instead of a raw stack trace — the contract every command in the tree relies on.
 */
export function handle<TArgs extends unknown[]>(
  action: (...args: TArgs) => Promise<void>,
): (...args: TArgs) => Promise<void> {
  return async (...args: TArgs) => {
    try {
      await action(...args);
    } catch (err) {
      printError(err);
      process.exitCode = 1;
    }
  };
}

/** Messages can carry API-supplied text (an ApiError's body, a redirect's Location), so every
 * branch goes through `neutralizeControl`. */
export function printError(err: unknown): void {
  const say = (line: string) => console.error(neutralizeControl(line, true));
  if (err instanceof ApiError) {
    const code = err.code ? ` [${err.code}]` : "";
    say(t("error.api", { status: err.status, code, message: err.message }));
  } else if (err instanceof Error) {
    say(t("error.generic", { message: err.message }));
  } else {
    say(t("error.generic", { message: String(err) }));
  }
}
