import { subscribeReminder, unsubscribeReminder } from "@/api/reminders";

const mockFetch = jest.fn();
global.fetch = mockFetch;

beforeEach(() => {
  mockFetch.mockReset();
});

describe("subscribeReminder", () => {
  it("posts the registration with the email and locale and reports ok", async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 204 });

    const result = await subscribeReminder(
      "sunny taco/1234",
      "guest@example.com",
      { channel: "webpush", endpoint: "https://push.example/abc", p256dh: "p", auth: "a" },
      "fr"
    );

    expect(result).toEqual({ ok: true });
    const [url, init] = mockFetch.mock.calls[0];
    expect(url).toBe("/api/bookings/ref/sunny%20taco%2F1234/reminders");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body)).toEqual({
      email: "guest@example.com",
      locale: "fr",
      channel: "webpush",
      endpoint: "https://push.example/abc",
      p256dh: "p",
      auth: "a",
    });
  });

  it("reports a failure without a reason on a non-2xx rather than throwing", async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 404,
      json: async () => ({ message: "Booking not found." }),
    });
    await expect(
      subscribeReminder("ref", "x@y.z", { channel: "expo", endpoint: "ExponentPushToken[1]" }, "en")
    ).resolves.toEqual({ ok: false });
  });

  it("carries the translated device-limit reason on a 409", async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 409,
      json: async () => ({
        message: "This booking already has the most devices that can receive reminders.",
        code: "booking.reminder_device_limit",
      }),
    });
    const result = await subscribeReminder(
      "ref",
      "x@y.z",
      { channel: "expo", endpoint: "ExponentPushToken[1]" },
      "en"
    );
    expect(result).toEqual({
      ok: false,
      message: expect.stringContaining("too many devices with reminders"),
    });
  });

  it("leaves the reason out of a 409 whose body cannot be read", async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 409,
      json: async () => {
        throw new Error("no body");
      },
    });
    await expect(
      subscribeReminder("ref", "x@y.z", { channel: "expo", endpoint: "ExponentPushToken[1]" }, "en")
    ).resolves.toEqual({ ok: false });
  });

  it("reports a failure when the request itself fails, without logging it", async () => {
    const spy = jest.spyOn(console, "error").mockImplementation(() => {});
    mockFetch.mockRejectedValue(new Error("offline"));
    await expect(
      subscribeReminder("ref", "x@y.z", { channel: "expo", endpoint: "ExponentPushToken[1]" }, "en")
    ).resolves.toEqual({ ok: false });
    expect(spy).not.toHaveBeenCalled();
    spy.mockRestore();
  });
});

describe("unsubscribeReminder", () => {
  it("sends the endpoint and email in a DELETE body", async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 204 });

    await expect(unsubscribeReminder("ref-1", "x@y.z", "ExponentPushToken[1]")).resolves.toBe(true);
    const [url, init] = mockFetch.mock.calls[0];
    expect(url).toBe("/api/bookings/ref/ref-1/reminders");
    expect(init.method).toBe("DELETE");
    expect(JSON.parse(init.body)).toEqual({ email: "x@y.z", endpoint: "ExponentPushToken[1]" });
  });

  it("reports false on a non-2xx and on a network failure", async () => {
    const spy = jest.spyOn(console, "error").mockImplementation(() => {});
    mockFetch.mockResolvedValueOnce({ ok: false, status: 404 });
    await expect(unsubscribeReminder("ref-1", "x@y.z", "e")).resolves.toBe(false);
    mockFetch.mockRejectedValueOnce(new Error("offline"));
    await expect(unsubscribeReminder("ref-1", "x@y.z", "e")).resolves.toBe(false);
    spy.mockRestore();
  });
});
