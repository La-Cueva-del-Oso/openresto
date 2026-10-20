import { renderHook, waitFor, act } from "@testing-library/react-native";
import { Platform } from "react-native";
import { usePushResubscribe } from "@/hooks/use-push-resubscribe";
import * as notificationsApi from "@/api/notifications";

jest.mock("@/api/notifications", () => ({
  subscribePush: jest.fn(),
}));

const mockSubscribePush = notificationsApi.subscribePush as jest.Mock;

const g = global as Record<string, unknown>;

function makeSub(endpoint = "https://push.example.com/sub") {
  return { endpoint, getKey: jest.fn().mockReturnValue(new ArrayBuffer(16)) };
}

function setup({
  permission = "granted",
  sub = makeSub() as ReturnType<typeof makeSub> | null,
  serviceWorker = true,
  pushManager = true,
}: {
  permission?: string;
  sub?: ReturnType<typeof makeSub> | null;
  serviceWorker?: boolean;
  pushManager?: boolean;
} = {}) {
  Object.defineProperty(Platform, "OS", { value: "web", configurable: true });
  Object.defineProperty(global, "Notification", {
    value: { permission, requestPermission: jest.fn() },
    configurable: true,
    writable: true,
  });
  if (!g.navigator || typeof g.navigator !== "object") {
    Object.defineProperty(global, "navigator", { value: {}, configurable: true, writable: true });
  }
  const nav = g.navigator as Record<string, unknown>;
  const getSubscription = jest.fn().mockResolvedValue(sub);
  if (serviceWorker) {
    nav.serviceWorker = { ready: Promise.resolve({ pushManager: { getSubscription } }) };
  } else {
    delete nav.serviceWorker;
  }
  if (pushManager) g.PushManager = {};
  else delete g.PushManager;
  return { getSubscription };
}

beforeEach(() => {
  jest.clearAllMocks();
  mockSubscribePush.mockResolvedValue(undefined);
});

describe("usePushResubscribe", () => {
  it("re-registers the browser's existing subscription with the server", async () => {
    setup();
    renderHook(() => usePushResubscribe(true));
    await waitFor(() => expect(mockSubscribePush).toHaveBeenCalledTimes(1));
    expect(mockSubscribePush).toHaveBeenCalledWith({
      endpoint: "https://push.example.com/sub",
      p256dh: expect.any(String),
      auth: expect.any(String),
    });
  });

  it("does nothing while the admin session is not authenticated", async () => {
    const { getSubscription } = setup();
    renderHook(() => usePushResubscribe(false));
    await act(async () => {});
    expect(getSubscription).not.toHaveBeenCalled();
    expect(mockSubscribePush).not.toHaveBeenCalled();
  });

  it("registers when the session becomes authenticated, and only once across re-renders", async () => {
    setup();
    const { rerender } = renderHook(({ on }) => usePushResubscribe(on), {
      initialProps: { on: false },
    });
    rerender({ on: true });
    await waitFor(() => expect(mockSubscribePush).toHaveBeenCalledTimes(1));
    rerender({ on: true });
    rerender({ on: true });
    await act(async () => {});
    expect(mockSubscribePush).toHaveBeenCalledTimes(1);
  });

  it("does not call the server when the browser holds no subscription", async () => {
    const { getSubscription } = setup({ sub: null });
    renderHook(() => usePushResubscribe(true));
    await waitFor(() => expect(getSubscription).toHaveBeenCalled());
    await act(async () => {});
    expect(mockSubscribePush).not.toHaveBeenCalled();
  });

  it("does not call the server when the subscription lacks its keys", async () => {
    const sub = {
      endpoint: "https://push.example.com/sub",
      getKey: jest.fn().mockReturnValue(null),
    };
    const { getSubscription } = setup({ sub: sub as unknown as ReturnType<typeof makeSub> });
    renderHook(() => usePushResubscribe(true));
    await waitFor(() => expect(getSubscription).toHaveBeenCalled());
    await act(async () => {});
    expect(mockSubscribePush).not.toHaveBeenCalled();
  });

  it.each(["denied", "default"])(
    "neither registers nor asks for permission when it is %s",
    async (permission) => {
      const { getSubscription } = setup({ permission });
      renderHook(() => usePushResubscribe(true));
      await act(async () => {});
      expect(getSubscription).not.toHaveBeenCalled();
      expect(mockSubscribePush).not.toHaveBeenCalled();
      expect(
        (global.Notification as unknown as { requestPermission: jest.Mock }).requestPermission
      ).not.toHaveBeenCalled();
    }
  );

  it("does nothing without a service worker or the Push API", async () => {
    setup({ serviceWorker: false });
    renderHook(() => usePushResubscribe(true));
    await act(async () => {});
    expect(mockSubscribePush).not.toHaveBeenCalled();

    setup({ pushManager: false });
    renderHook(() => usePushResubscribe(true));
    await act(async () => {});
    expect(mockSubscribePush).not.toHaveBeenCalled();
  });

  it("does nothing off web", async () => {
    const { getSubscription } = setup();
    Object.defineProperty(Platform, "OS", { value: "ios", configurable: true });
    renderHook(() => usePushResubscribe(true));
    await act(async () => {});
    expect(getSubscription).not.toHaveBeenCalled();
    expect(mockSubscribePush).not.toHaveBeenCalled();
  });

  it("swallows a server error instead of throwing", async () => {
    setup();
    mockSubscribePush.mockRejectedValue(new Error("boom"));
    renderHook(() => usePushResubscribe(true));
    await waitFor(() => expect(mockSubscribePush).toHaveBeenCalledTimes(1));
    await act(async () => {});
  });

  it("swallows a failure reading the browser's subscription", async () => {
    const { getSubscription } = setup();
    getSubscription.mockRejectedValue(new Error("no worker"));
    renderHook(() => usePushResubscribe(true));
    await waitFor(() => expect(getSubscription).toHaveBeenCalled());
    await act(async () => {});
    expect(mockSubscribePush).not.toHaveBeenCalled();
  });
});
