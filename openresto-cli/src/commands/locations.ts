import { Command } from "commander";
import {
  clientFor,
  getGlobalOptions,
  handle,
  printMessage,
} from "../context.js";
import { t } from "../i18n.js";
import { printResult } from "../output.js";
import { confirmOrExit } from "../confirm.js";
import { ApiError } from "../transport.js";

const LIST_COLUMNS = [
  "id",
  "name",
  "isArchived",
  "activeBookingsCount",
  "upcomingBookingsCount",
];

const SKIPPED_COLUMNS = ["id", "bookingRef", "tableName"];

const CONFLICT_COLUMNS = [
  "bookingId",
  "bookingRef",
  "date",
  "seats",
  "customerName",
  "reason",
];

export function registerLocationsCommands(program: Command): void {
  const locations = program
    .command("locations")
    .description("Manage restaurant locations");

  locations
    .command("list")
    .description("List locations, including archived ones (admin view)")
    .action(
      handle(async (_options: unknown, command: Command) => {
        const { client } = clientFor(command);
        const globals = getGlobalOptions(command);
        const result = await client.get("/api/admin/restaurants");
        printResult(result, Boolean(globals.json), LIST_COLUMNS);
      }),
    );

  locations
    .command("get <id>")
    .description(
      "Show full details for one location (public view — archived locations 404)",
    )
    .action(
      handle(async (id: string, _options: unknown, command: Command) => {
        const { client } = clientFor(command);
        const globals = getGlobalOptions(command);
        const result = await client.get(
          `/api/restaurants/${encodeURIComponent(id)}`,
        );
        printResult(result, Boolean(globals.json));
      }),
    );

  locations
    .command("create")
    .description("Create a new location")
    .requiredOption("--name <name>", "Location name")
    .option("--address <address>", "Street address")
    .action(
      handle(
        async (
          options: { name: string; address?: string },
          command: Command,
        ) => {
          const { client } = clientFor(command);
          const globals = getGlobalOptions(command);
          const result = await client.post("/api/admin/restaurants", {
            body: { name: options.name, address: options.address },
          });
          printResult(result, Boolean(globals.json));
        },
      ),
    );

  locations
    .command("pause <id>")
    .description(
      "Pause new bookings that would start within the next N minutes",
    )
    .requiredOption("--minutes <n>", "How many minutes ahead to close", Number)
    .action(
      handle(
        async (id: string, options: { minutes: number }, command: Command) => {
          const { client } = clientFor(command);
          await client.post(
            `/api/admin/restaurants/${encodeURIComponent(id)}/pause`,
            {
              body: { minutes: options.minutes },
            },
          );
          printMessage(command, "location.paused", {
            id,
            minutes: options.minutes,
          });
        },
      ),
    );

  locations
    .command("unpause <id>")
    .description("Lift an active booking pause")
    .action(
      handle(async (id: string, _options: unknown, command: Command) => {
        const { client } = clientFor(command);
        await client.post(
          `/api/admin/restaurants/${encodeURIComponent(id)}/unpause`,
        );
        printMessage(command, "location.unpaused", { id });
      }),
    );

  locations
    .command("extend <id>")
    .description(
      'Push every active booking at a location out by N minutes ("we\'re running late tonight"). ' +
        "A booking with another booking right after it on its table is left as it was and listed.",
    )
    .requiredOption(
      "--minutes <n>",
      "Minutes to add to each end time (1 to 480)",
      Number,
    )
    .action(
      handle(
        async (id: string, options: { minutes: number }, command: Command) => {
          const { client } = clientFor(command);
          const globals = getGlobalOptions(command);
          const result = (await client.post(
            `/api/admin/restaurants/${encodeURIComponent(id)}/extend`,
            { body: { minutes: options.minutes } },
          )) as { extendedBookings?: unknown[]; skippedBookings?: unknown[] };
          if (globals.json) {
            printResult(result, true);
            return;
          }
          const skipped = result?.skippedBookings ?? [];
          printMessage(command, "location.extended", {
            count: result?.extendedBookings?.length ?? 0,
            id,
            minutes: options.minutes,
          });
          if (skipped.length > 0) {
            printMessage(command, "location.extendSkipped", {
              count: skipped.length,
            });
            printResult(skipped, false, SKIPPED_COLUMNS);
          }
        },
      ),
    );

  locations
    .command("conflicts <id>")
    .description(
      "Bookings stranded by a narrowed schedule — taken under hours or open days the location " +
        "no longer runs. Editing the schedule leaves existing bookings alone by design.",
    )
    .action(
      handle(async (id: string, _options: unknown, command: Command) => {
        const { client } = clientFor(command);
        const globals = getGlobalOptions(command);
        const result = await client.get(
          `/api/restaurants/${encodeURIComponent(id)}/schedule-conflicts`,
        );
        printResult(result, Boolean(globals.json), CONFLICT_COLUMNS);
      }),
    );

  locations
    .command("archive <id>")
    .description("Archive a location (delists it; required before delete)")
    .action(
      handle(async (id: string, _options: unknown, command: Command) => {
        const { client } = clientFor(command);
        await client.patch(`/api/admin/restaurants/${encodeURIComponent(id)}`, {
          body: { isArchived: true },
        });
        printMessage(command, "location.archived", { id });
      }),
    );

  locations
    .command("restore <id>")
    .description(
      "Restore an archived location — no confirmation needed, this is reversible",
    )
    .action(
      handle(async (id: string, _options: unknown, command: Command) => {
        const { client } = clientFor(command);
        await client.patch(`/api/admin/restaurants/${encodeURIComponent(id)}`, {
          body: { isArchived: false },
        });
        printMessage(command, "location.restored", { id });
      }),
    );

  locations
    .command("delete <id>")
    .description(
      "Permanently delete an archived location and everything under it — cannot be undone. " +
        "The server rejects this unless the location is already archived.",
    )
    .option("--yes", "Skip the confirmation prompt")
    .action(
      handle(
        async (id: string, options: { yes?: boolean }, command: Command) => {
          const { client } = clientFor(command);
          const globals = getGlobalOptions(command);

          let confirmMessage = t("location.deleteConfirmGeneric", { id });
          try {
            const preview = await client.get<{
              name: string;
              sectionCount: number;
              tableCount: number;
              bookingCount: number;
            }>(
              `/api/admin/restaurants/${encodeURIComponent(id)}/delete-preview`,
            );
            confirmMessage = t("location.deleteConfirm", {
              name: preview.name,
              sections: preview.sectionCount,
              tables: preview.tableCount,
              bookings: preview.bookingCount,
            });
          } catch {
            // Preview is best-effort — fall back to the generic message rather than blocking delete.
          }

          await confirmOrExit(
            confirmMessage,
            Boolean(options.yes ?? globals.yes),
          );

          try {
            await client.delete(
              `/api/admin/restaurants/${encodeURIComponent(id)}`,
            );
          } catch (err) {
            if (err instanceof ApiError && err.status === 400) {
              throw new Error(
                t("location.archiveFirst", { message: err.message, id }),
              );
            }
            throw err;
          }
          printMessage(command, "location.deleted", { id });
        },
      ),
    );
}
