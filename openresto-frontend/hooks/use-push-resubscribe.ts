import { useEffect } from "react";
import { Platform } from "react-native";
import { subscribePush } from "@/api/notifications";
import { arrayBufferToBase64 } from "@/utils/notifications";

/**
 * Tells the server about the push subscription this browser already holds, once per signed-in
 * session. The browser keeps its subscription across logins, password changes and server-side
 * clean-ups, so the panels that read it locally would otherwise say "active" while the server
 * has no row and nothing arrives. The subscribe endpoint is idempotent and hands the row to the
 * caller, so repeating it is safe.
 *
 * It never prompts: no granted permission or no local subscription means nothing to re-register.
 * Failures are swallowed — the panels keep showing the browser's real state.
 *
 * @see [use-push-resubscribe.test.tsx](../tests/hooks/use-push-resubscribe.test.tsx) — pins that
 * a granted permission with a local subscription re-registers once, and that no subscription, a
 * denied or undecided permission, a missing Push API or a server error do not.
 */
export function usePushResubscribe(enabled: boolean) {
  useEffect(() => {
    if (!enabled || Platform.OS !== "web") return;
    if (!("serviceWorker" in navigator) || !("PushManager" in window)) return;
    if (Notification.permission !== "granted") return;

    (async () => {
      try {
        const sw = await navigator.serviceWorker.ready;
        const sub = await sw.pushManager.getSubscription();
        const p256dh = sub?.getKey("p256dh");
        const auth = sub?.getKey("auth");
        if (!sub || !p256dh || !auth) return;
        await subscribePush({
          endpoint: sub.endpoint,
          p256dh: arrayBufferToBase64(p256dh),
          auth: arrayBufferToBase64(auth),
        });
      } catch {
        // Nothing to show: this runs unprompted, and the panels already report the browser's state.
      }
    })();
  }, [enabled]);
}
