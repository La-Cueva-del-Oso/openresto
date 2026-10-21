import { neutralizeControl } from "./output.js";

/**
 * What the CLI says on its own, in English and Spanish. Only that: command names, flags, `--help`,
 * `--json` output and any text that came from the server (`message` of an API error) stay as they
 * are. Keys are named after the concept, never the English text, and `es` is checked against `en`
 * by the compiler (`satisfies Record<MessageKey, string>` rejects a missing and an extra key), so
 * a message added in one language and not the other fails `tsc` rather than reaching a user.
 *
 * @see [i18n.test.ts](./i18n.test.ts) — pins the language resolution order, the key and
 * placeholder parity of the two tables, and that hostile parameters are printed escaped.
 */
const en = {
  "error.api": "Error ({status}){code}: {message}",
  "error.generic": "Error: {message}",
  "api.requestFailed": "Request failed with status {status}",

  "auth.urlRequired": "A server URL is required.",
  "auth.keyRequired": "An API key is required.",
  "auth.verifyFailed": "Could not verify the API key against {url}: {message}",
  "auth.saved": 'Saved profile "{profile}" for {url}.',
  "auth.keyRemoved": 'Removed the API key for profile "{profile}".',
  "prompt.serverUrl": "Server URL",
  "prompt.apiKey": "API key",
  "prompt.aborted": "Aborted.",

  "confirm.choices": "[y/N]",
  "confirm.notTerminal":
    "{message} Re-run with --yes to confirm (required when not running in a terminal).",
  "confirm.declined": "Aborted: not confirmed.",

  "transport.invalidUrl":
    '"{url}" is not a valid server address. Use a full address such as https://booking.example.com.',
  "transport.notEncrypted":
    "{origin} is not encrypted, so your API key would travel in the clear. Use an https:// address, or set {envVar}=1 if you really want to send your credentials unencrypted.",
  "transport.noServerUrl":
    "No server URL configured. Run `openresto auth login` or set OPENRESTO_URL.",
  "transport.unreachable": "Could not reach {origin}: {detail}",
  "transport.redirectWithTarget":
    "{origin} redirected to {href} (HTTP {status}). The CLI does not follow redirects, because that would send your admin API key to the redirect target. If {targetOrigin} is your server's real address, configure that instead — run `openresto auth login` or set OPENRESTO_URL.",
  "transport.redirectNoTarget":
    "{origin} redirected without a usable Location header (HTTP {status}). The CLI does not follow redirects, because that would send your admin API key to the redirect target. Configure the server's real address — run `openresto auth login` or set OPENRESTO_URL.",

  "version.unknown":
    "warning: server version is unknown (older than {introducedIn}, /api/version not found) — commands may not match the server's API",
  "version.mismatch":
    "warning: server is {server}, CLI is {cli} — commands may not match the server's API",

  "config.invalidFile": "Config file at {file} is not valid JSON.",
  "io.jsonUnreadable": "Could not parse JSON from {source}: {detail}",
  "io.stdin": "stdin",

  "output.noResults": "(no results)",
  "output.noContent": "(no content)",

  "booking.cancelled": "Booking {id} cancelled.",
  "booking.restored": "Booking {id} restored.",
  "booking.purged": "Booking {id} purged.",
  "booking.purgeConfirm": "This will permanently delete booking {id}.",
  "booking.emailEmpty": "The email body is empty.",
  "booking.emailNoBody":
    "No message body. Pass --body-file <path>, or pipe the body into stdin.",

  "brand.nothingToUpdate":
    "Nothing to update — pass at least one flag or --from-json.",

  "location.paused": "Location {id} paused for {minutes} minute(s).",
  "location.unpaused": "Location {id} unpaused.",
  "location.extended":
    "Extended {count} booking(s) at location {id} by {minutes} minute(s).",
  "location.extendSkipped":
    "{count} booking(s) left as they were because another booking follows on the same table:",
  "location.archived": "Location {id} archived.",
  "location.restored": "Location {id} restored.",
  "location.deleted": "Location {id} deleted.",
  "location.deleteConfirmGeneric":
    "This will permanently delete location {id} and everything under it.",
  "location.deleteConfirm":
    'This will permanently delete "{name}" ({sections} section(s), {tables} table(s), {bookings} booking(s)).',
  "location.archiveFirst":
    '{message} Archive the location first with "locations archive {id}".',

  "section.removeConfirm": "This will remove section {id} and its tables.",
  "section.removeImpact":
    "{count} upcoming booking(s) will lose their section reference.",
  "section.removed": "Section {id} removed.",

  "table.removeConfirm": "This will remove table {id}.",
  "table.removeImpact":
    "{count} upcoming booking(s) will lose their table reference.",
  "table.removed": "Table {id} removed.",
} as const;

export type MessageKey = keyof typeof en;

const es = {
  "error.api": "Error ({status}){code}: {message}",
  "error.generic": "Error: {message}",
  "api.requestFailed": "La solicitud falló con el estado {status}",

  "auth.urlRequired": "Falta la dirección del servidor.",
  "auth.keyRequired": "Falta la clave de API.",
  "auth.verifyFailed":
    "No se pudo verificar la clave de API en {url}: {message}",
  "auth.saved": 'Se guardó el perfil "{profile}" para {url}.',
  "auth.keyRemoved": 'Se eliminó la clave de API del perfil "{profile}".',
  "prompt.serverUrl": "Dirección del servidor",
  "prompt.apiKey": "Clave de API",
  "prompt.aborted": "Operación cancelada.",

  "confirm.choices": "[s/N]",
  "confirm.notTerminal":
    "{message} Vuelve a ejecutar el comando con --yes para confirmar (es obligatorio cuando no se usa una terminal).",
  "confirm.declined": "Cancelado: no se confirmó.",

  "transport.invalidUrl":
    '"{url}" no es una dirección de servidor válida. Usa una dirección completa, como https://booking.example.com.',
  "transport.notEncrypted":
    "{origin} no está cifrada, así que tu clave de API viajaría sin protección. Usa una dirección https://, o define {envVar}=1 si de verdad quieres enviar tus credenciales sin cifrar.",
  "transport.noServerUrl":
    "No hay una dirección de servidor configurada. Ejecuta `openresto auth login` o define OPENRESTO_URL.",
  "transport.unreachable": "No se pudo conectar con {origin}: {detail}",
  "transport.redirectWithTarget":
    "{origin} redirigió a {href} (HTTP {status}). openresto no sigue redirecciones, porque enviaría tu clave de API de administración a esa otra dirección. Si {targetOrigin} es la dirección real de tu servidor, configura esa: ejecuta `openresto auth login` o define OPENRESTO_URL.",
  "transport.redirectNoTarget":
    "{origin} redirigió sin indicar a qué dirección ir (HTTP {status}). openresto no sigue redirecciones, porque enviaría tu clave de API de administración a esa otra dirección. Configura la dirección real de tu servidor: ejecuta `openresto auth login` o define OPENRESTO_URL.",

  "version.unknown":
    "aviso: no se pudo conocer la versión del servidor (es anterior a la {introducedIn} y no tiene /api/version): puede que los comandos no coincidan con la API del servidor",
  "version.mismatch":
    "aviso: el servidor está en la versión {server} y la CLI en la {cli}: puede que los comandos no coincidan con la API del servidor",

  "config.invalidFile":
    "El archivo de configuración en {file} está dañado (no es JSON válido). Corrígelo o bórralo y vuelve a ejecutar `openresto auth login`.",
  "io.jsonUnreadable": "No se pudo leer el JSON de {source}: {detail}",
  "io.stdin": "la entrada estándar",

  "output.noResults": "(sin resultados)",
  "output.noContent": "(sin contenido)",

  "booking.cancelled": "Reserva {id} cancelada.",
  "booking.restored": "Reserva {id} restaurada.",
  "booking.purged": "Reserva {id} eliminada de forma permanente.",
  "booking.purgeConfirm": "Se eliminará de forma permanente la reserva {id}.",
  "booking.emailEmpty": "El cuerpo del correo está vacío.",
  "booking.emailNoBody":
    "Falta el cuerpo del mensaje. Indica --body-file <ruta> o envía el texto por la entrada estándar.",

  "brand.nothingToUpdate":
    "No hay nada que actualizar: indica al menos una opción o usa --from-json.",

  "location.paused": "Ubicación {id} en pausa por {minutes} minuto(s).",
  "location.unpaused": "Ubicación {id} reanudada.",
  "location.extended":
    "Se alargaron {count} reserva(s) de la ubicación {id} en {minutes} minuto(s).",
  "location.extendSkipped":
    "{count} reserva(s) quedaron como estaban porque otra reserva sigue en la misma mesa:",
  "location.archived": "Ubicación {id} archivada.",
  "location.restored": "Ubicación {id} restaurada.",
  "location.deleted": "Ubicación {id} eliminada.",
  "location.deleteConfirmGeneric":
    "Se eliminará de forma permanente la ubicación {id} y todo lo que contiene.",
  "location.deleteConfirm":
    'Se eliminará de forma permanente "{name}" ({sections} sección(es), {tables} mesa(s), {bookings} reserva(s)).',
  "location.archiveFirst":
    '{message} Primero archiva la ubicación con "locations archive {id}".',

  "section.removeConfirm": "Se quitará la sección {id} junto con sus mesas.",
  "section.removeImpact":
    "{count} reserva(s) próxima(s) perderá(n) la referencia a su sección.",
  "section.removed": "Sección {id} quitada.",

  "table.removeConfirm": "Se quitará la mesa {id}.",
  "table.removeImpact":
    "{count} reserva(s) próxima(s) perderá(n) la referencia a su mesa.",
  "table.removed": "Mesa {id} quitada.",
} as const satisfies Record<MessageKey, string>;

export type Lang = "en" | "es";

const TABLES: Record<Lang, Record<MessageKey, string>> = { en, es };

export const LANG_ENV = "OPENRESTO_LANG";

/** The tables, exposed so the tests can hold their keys and placeholders in step. */
export const messageTables: Readonly<Record<Lang, Record<MessageKey, string>>> =
  TABLES;

function systemLocale(): string {
  return Intl.DateTimeFormat().resolvedOptions().locale;
}

/**
 * `OPENRESTO_LANG` (`es` or `en`) wins; anything else, including empty or an unsupported value,
 * falls to the system language, where any `es*` locale is Spanish and every other is English.
 * Pure on purpose: the environment and the locale come in as arguments.
 */
export function resolveLanguage(
  env: NodeJS.ProcessEnv = process.env,
  locale: string = systemLocale(),
): Lang {
  const forced = env[LANG_ENV]?.trim().toLowerCase();
  if (forced === "en" || forced === "es") {
    return forced;
  }
  return /^es([-_]|$)/i.test(locale) ? "es" : "en";
}

/**
 * Looks a message up in the current language and fills its `{name}` placeholders. Values pass
 * through `neutralizeControl`, so a guest name or a server-supplied version can never put a raw
 * escape sequence on the operator's terminal; line breaks are kept, as in error messages.
 */
export function t(
  key: MessageKey,
  params: Record<string, string | number> = {},
  lang: Lang = resolveLanguage(),
): string {
  return TABLES[lang][key].replace(/\{(\w+)\}/g, (placeholder, name: string) =>
    name in params
      ? neutralizeControl(String(params[name]), true)
      : placeholder,
  );
}
