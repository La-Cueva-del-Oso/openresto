/**
 * Test helper, named `.test.ts` so the package's `files` rule keeps it out of the tarball. Runs
 * `fn` with `OPENRESTO_LANG` forced to `lang` and puts the previous value back afterwards.
 */
export async function withLang<T>(
  lang: "en" | "es",
  fn: () => T | Promise<T>,
): Promise<T> {
  const previous = process.env.OPENRESTO_LANG;
  process.env.OPENRESTO_LANG = lang;
  try {
    return await fn();
  } finally {
    if (previous === undefined) delete process.env.OPENRESTO_LANG;
    else process.env.OPENRESTO_LANG = previous;
  }
}
