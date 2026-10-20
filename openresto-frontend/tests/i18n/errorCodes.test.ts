import fs from "fs";
import path from "path";
import en from "@/locales/en.json";

const ERROR_CODES_FILE = path.resolve(
  __dirname,
  "../../../OpenRestoApi.Core/Application/Utilities/ErrorCodes.cs"
);

/**
 * The `ErrorCodes` constants a C# source declares ("booking.past_date"). Only a
 * `public const string Name = "x.y";` declaration counts, so a dotted string inside a comment,
 * a message or another expression is not mistaken for a code.
 */
function extractErrorCodes(source: string): string[] {
  return [
    ...source.matchAll(/^\s*public const string \w+\s*=\s*"([a-z_]+\.[a-z0-9_]+)"\s*;/gm),
  ].map((match) => match[1]);
}

function hasErrorCopy(code: string): boolean {
  const [group, key] = code.split(".");
  const copy = (en.errors as Record<string, Record<string, string> | string>)[group];
  return typeof copy === "object" && typeof copy[key] === "string";
}

/**
 * @see [errors.ts](../../api/errors.ts) — a code with no `errors.<code>` copy shows the server's
 * English message to a viewer in any other language.
 */
describe("every backend error code has copy in the UI language", () => {
  it("lists the codes the backend declares", () => {
    const codes = extractErrorCodes(fs.readFileSync(ERROR_CODES_FILE, "utf8"));

    expect(codes.length).toBeGreaterThan(100);
    expect(codes).toContain("booking.past_date");
  });

  it("has an errors.<code> entry in en.json for every one of them", () => {
    const codes = extractErrorCodes(fs.readFileSync(ERROR_CODES_FILE, "utf8"));

    // fr/es/de follow through the key parity test.
    expect(codes.filter((code) => !hasErrorCopy(code))).toEqual([]);
  });
});

describe("extractErrorCodes", () => {
  const source = `
    // Does not cover "e.g. brand.x_invalid" or the ~35 [StringLength] messages.
    /// <c>"booking.in_a_comment"</c>
    public const string BookingPastDate = "booking.past_date";
    public const string ApiKeyNotFound   =   "api_key.not_found" ;
    public const string Greeting = "Hello. World";
    public const string Capitalised = "Booking.Past_Date";
    private static readonly string Other = "booking.not_a_const";
    var message = "Cannot cancel a booking. Try again";
  `;

  it("reads a declared code", () => {
    expect(extractErrorCodes(source)).toEqual(
      expect.arrayContaining(["booking.past_date", "api_key.not_found"])
    );
  });

  it("ignores comments, sentences, wrong casing and non-const strings", () => {
    expect(extractErrorCodes(source)).toEqual(["booking.past_date", "api_key.not_found"]);
  });
});
