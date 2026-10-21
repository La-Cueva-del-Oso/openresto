import { del, post } from "./client";
import { apiErrorMessage } from "@/api/errors";

export type ReminderChannel = "expo" | "webpush";

/** The address a reminder is delivered to: an Expo push token, or a browser's Web Push subscription. */
export interface ReminderRegistration {
  channel: ReminderChannel;
  endpoint: string;
  p256dh?: string;
  auth?: string;
}

function reminderPath(bookingRef: string): string {
  return `/bookings/ref/${encodeURIComponent(bookingRef)}/reminders`;
}

/**
 * A refusal is a result, not a throw. `message` is set only when the server refused for a rule
 * the guest can act on (a 409: the booking is out of devices), already in their language; any
 * other failure leaves it out and the caller shows its own generic copy.
 */
export type SubscribeReminderResult = { ok: true } | { ok: false; message?: string };

/**
 * Asks the server to remind this device about a booking. The reference and the email are the
 * guest's identity, as on lookup; a wrong pair is a 404 the caller reports as a plain failure.
 *
 * @see [reminders.test.ts](../tests/api/reminders.test.ts) — pins the body shape, that a
 * non-2xx or a network failure answers `ok: false` rather than throwing, and that only a 409
 * carries the server's translated reason.
 */
export async function subscribeReminder(
  bookingRef: string,
  email: string,
  registration: ReminderRegistration,
  locale: string
): Promise<SubscribeReminderResult> {
  try {
    const res = await post(reminderPath(bookingRef), { email, locale, ...registration });
    if (res.ok) return { ok: true };
    if (res.status !== 409) return { ok: false };
    const body = await res.json().catch(() => ({}));
    return { ok: false, message: apiErrorMessage(body, "") || undefined };
  } catch {
    return { ok: false };
  }
}

export async function unsubscribeReminder(
  bookingRef: string,
  email: string,
  endpoint: string
): Promise<boolean> {
  try {
    const res = await del(reminderPath(bookingRef), { body: { email, endpoint } });
    return res.ok;
  } catch (err) {
    console.error("unsubscribeReminder error:", err);
    return false;
  }
}
