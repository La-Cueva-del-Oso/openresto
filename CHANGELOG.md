# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

How each feature behaves, its API fields and its error codes are in
[feature reference](https://docs.openres.to/guides/features/).

## [Unreleased]

### Security

- Admin push notifications now belong to the account that enabled them. Before, a subscription had no owner: a deactivated admin kept receiving every booking, and a browser stayed subscribed after its user's password was changed or reset. Bookings are now pushed only to active accounts, a password change or reset removes that account's subscriptions (signing out does not), re-enabling notifications in a browser another admin had used takes it over, and only the account that owns a subscription can remove it. The public demo's reset also clears subscriptions, so a visitor's browser no longer keeps receiving the demo's bookings.
- A guest's booking request can no longer set another booking's id or its cancellation flags, and a request that fails no longer leaves a table held for five minutes. Bookings made over the API are also refused outside opening hours and on closed days, as holds already were.
- Changing the mail server, port, username or encryption in the email settings now requires typing the mail password again. Before, an account with access to those settings could point the saved password at a server of their own.
- Setting the security question now asks for the current password, and a password reset by an Owner clears the account's security question. Changing a password also invalidates a pending reset link.
- Deactivating an account now ends its sessions, so reactivating it no longer brings old logins back. `scripts/reset-admin.sh` ends the account's sessions too.
- `scripts/reset-admin.sh` now also deletes the recovered account's push subscriptions, in the same transaction as the reset. Before, a browser registered during a compromise kept receiving every booking notification after the recovery.
- An API key without permission to see guest details can no longer search bookings by email, and its text search no longer matches guest names or emails. Before, those searches could be used to work out a hidden address.
- Behind a TLS proxy, the stack can now recover each visitor's real address when you list the proxy in `OPENRESTO_TRUSTED_PROXIES` (see the HTTPS guide). Before, every visitor shared one rate limit, so one person could lock everyone else out of signing in.
- Two Owners demoting or deactivating each other at the same moment can no longer leave the instance without an active Owner. Wrong answers to the security question are now counted before they are checked, so a burst of guesses reaches the lockout instead of all starting from the same count, and two session revocations at once (a password change and a deactivation, say) are both counted.
- The native-app readiness check now opens its connection only to a public address, closing a gap where a hostile DNS answer could point its `/.well-known/` request at an internal service. Web Push endpoints and that check also refuse `localhost.`, `.local.` and `.internal.` written with a trailing dot, which passed the address check before.
- `scripts/reset-admin.sh` no longer accepts `--password`, which any user on the host could read with `ps`; pipe the password to `--password-stdin` instead (see the admin accounts guide). A reset also clears the account's security-question lockout.
- Purging a booking, or deleting it through the API, now also erases the guest's admin notifications, email-failure records and waitlist entry, which kept their name, email and reference before.
- Email errors no longer expose the mail server's own error text. The connection test and the booking email endpoints return a stable code (`email.auth_failed`, `email.connection_failed`, `email.recipient_rejected`, `email.send_failed`) shown to the admin as a plain sentence, and the failures list keeps only that code.
- The command-line client refuses plain `http://` server addresses unless they point at the same machine, or `OPENRESTO_ALLOW_HTTP=1` is set, so an API key never travels unencrypted by accident.
- Text that comes from the API can no longer drive the terminal of someone using the command-line client: control and escape characters in tables, records, errors and `--json` output are shown as visible `\xNN` escapes. The server also refuses control characters in a booking's name and special requests (`booking.name_invalid`, `booking.special_requests_invalid`).
- Web Push requests now give up after 15 seconds and read at most 64 KiB of the push service's answer. Before, a push address chosen by a guest could answer with an endless body and tie up the server while a reminder or notification was being sent; such a send is now just that subscription's failure.
- Purging a booking no longer erases another guest's notifications and email-failure records when two bookings happen to share a reference. Rows linked to the booking are found by that link; rows that only carry the reference are removed only if they also match the guest's own name or address.
- An API key with the `users:write` scope can now deactivate an account but no longer reactivate one: the request is refused with `api_key.not_allowed` (403) and an Owner reactivates from a signed-in session. Before, a key could switch an account back on, which gave its login back to anyone who still knew the old password. The command-line client therefore no longer has `openresto users activate`.
- Two admins saving the email settings at the same moment can no longer leave one's mail server beside the other's password. Before, an account that changed the server while an Owner was entering a new password could end up with that password pointed at its own server.
- A security-question answer being checked while an Owner resets that account's password, or while the account's question is changed, no longer earns a reset link once the reset has landed. Setting up a security question likewise fails if the account's password is reset or its sessions end while the request is in flight.
- A password reset link can no longer set a password after the account's password was changed or reset by an Owner while the reset request was in flight, and two requests carrying the same link can no longer both succeed. The link is now spent in the same step that sets the new password.
- Two places still printed API text raw and are now neutralized the same way: the command-line client's confirmation prompt (a location's name in `locations delete`) and `npm run native:init` (the brand name it prints and any error that echoes a response). A Manager could otherwise have put escape sequences in a location or brand name to drive the terminal of whoever ran them.
- `scripts/demo_data.py media` no longer copies the `--media-dir` path into the SQL it generates. A line break in that path ended the SQL comment it was written into, so the rest of the path ran as statements when the output was applied.
- A backend image built from a clone no longer includes `OpenRestoApi/appsettings.Local.json`. The file is git-ignored but the build context sent it, and the backend loads it in every environment, so credentials kept there could end up inside the image.
- Recovering an account now also revokes its API keys: changing the password, resetting it (with a reset link or by an Owner) and deactivating the account each revoke the account's live keys in the same step that ends its sessions. Before, a key minted while an account was compromised kept working after the recovery. Signing out does not revoke keys, and reactivating an account does not bring revoked ones back. `scripts/reset-admin.sh` revokes the account's keys in the same transaction as the password reset.
- Changing a password or a sign-in email is now refused when the account's sessions were ended in the meantime (a reset by an Owner, a deactivation, a sign-out elsewhere) instead of overwriting that change; the request answers as if the current password were wrong. Two accounts changing to the same email at once now get the usual "already in use" answer for the loser.
- Resetting a password with a security-question reset link now requires the account to be active, as sign-in does. Before, a deactivated account holding a link issued earlier could still set a password.

### Changed

- Existing admin push subscriptions have no owner to attribute them to and are discarded by the upgrade. A browser that already had notifications on registers itself again the next time an admin opens the admin area, so most installs need no action; if notifications stay silent, turn them off and on again in Settings. Until then, the notifications page still lists new bookings, but nothing is pushed to the device.
- Failed-email records now expire after 30 days and admin notifications after 90 (`Retention:EmailFailureDays`, `Retention:NotificationDays`; a value of 0 or less keeps them forever). Before, they were kept indefinitely along with the guest's address and name.

### Fixed

- Two simultaneous bookings for the same table and time could both succeed, whether they came from the website, the admin, the waitlist or a booking being moved. Creating, moving and seating now go through one lock, so the second request gets the usual "table taken" answer. The cover limit per slot is checked inside the same lock.
- Restoring a cancelled booking now refuses when its table was booked in the meantime, instead of seating two parties at once.
- 22 error messages (API keys, brand settings, booking reminders, wallet passes, push notifications) were shown in English whatever the language; they are now translated into English, Spanish, French and German, and a test fails when a new error code ships without its translations. Spanish addresses everyone as _tú_.
- A booking made with stray spaces around the email (`" guest@example.com "`) could not be found afterwards: the lookup answered "not found" for the right reference. The address is now stored trimmed, and the lookup also finds bookings already saved with spaces.
- Seating the same waitlist ticket twice at the same moment (two staff pressing Seat, or a double tap) created two bookings on two tables. The ticket is now checked and closed inside the same lock as the booking, so the second request gets the "already left the waitlist" answer.

## [2.4.1] - 2026-10-14

### Changed

- The documentation site now matches the look of [get.openres.to](https://get.openres.to): the same warm palette, terracotta accent, Inter type and brand mark, in light and dark.

### Fixed

- Documentation: upgrading now says to download the new release's `docker-compose.yml`, since the file attached to a release is pinned to its version and `docker compose pull` alone kept the old one.
- Documentation: Wallet, push and reminder settings are given under the `.env` names the release compose file reads, the backup commands find the volumes whatever the compose project is called, and the admin reset script is downloaded where it can find `.env`.
- Documentation: confirmation email links, upload limits and rate limits are described as they are; the rate limits are fixed, not configurable.
- On a phone browser, scrolling back up inside the booking result sheet no longer drags the sheet down at the same time.
- On iOS, dragging the booking result sheet down no longer makes the booking inside it bounce under your finger.

### Security

- .NET runtime updated to 10.0.13, which fixes the `System.Security.Cryptography.Pkcs` advisories. Expo SDK 57 packages and other npm dependencies updated to their latest patch releases.

## [2.4.0] - 2026-10-03

### Added

- **Documentation site** at [docs.openres.to](https://docs.openres.to), built from `docs-site/` in this repository. It adds a self-hosting guide (install, configuration reference, HTTPS, email, push notifications, admin recovery, upgrading, troubleshooting) and renders the API reference from the committed OpenAPI contract. Setup guides cover VAPID keys, which URL setting does what, and a native app overview.

### Changed

- The feature reference, API guide, native app guide and backup guide moved from `docs/` into the documentation site. The API Keys screen's default guide link now points at the site.

### Security

- Sessions end on password change, password reset and sign-out, and the account behind a session is resolved on every request.
- The security question locks after repeated wrong answers.
- A booking can only take a table that belongs to the location being booked.
- A guest booking's email is validated and its free-text fields are capped.
- One client can only hold a limited number of tables at once.
- The docs site overrides `form-data` to 4.0.6, and the deploy's SSH action is pinned to a commit.

## [2.3.1] - 2026-10-01

### Changed

- The hold banner on the booking form now names the table that was auto-assigned to the guest.
- Frontend and CLI npm dependencies updated to clear open `npm audit` advisories, and the backend's Magick.NET and MailKit packages moved to their latest patch releases (#479).

### Fixed

- **Waitlist board.** A party that can be seated now no longer shows a quoted wait beside an enabled Seat button. It shows "Free now, skips #N", naming the party ahead that the free table was estimated for.

## [2.3.0] - 2026-09-24

### Added

- **Turn times by party size.** Larger parties can get longer sittings. Availability, holds, auto-assign and the waitlist all use the party's own length. Existing bookings keep the length they were booked with.
- **Booking status.** Mark bookings Arrived, Seated, Finished or No-show from the admin, with five minutes to undo. Finishing early or a no-show frees the table straight away. The bookings list gets a No-shows tab, and lookups show a guest's past no-shows.
- **Walk-in-only tables.** Keep a single table for the door without closing online booking for the whole location.
- **Cover pacing.** Cap how many guests can start in one booking slot. The dashboard shows today's arrivals per slot.
- **Walk-in waitlist.** Guests at a walk-in-only location can join a queue online and see their estimated wait. Staff run it from the new Waitlist page, and calling a party can send a push notification and an email.

### Changed

- Backend packages updated: ASP.NET Core and EF Core 10.0.12, MailKit 4.18.0, Magick.NET 14.17.1 (#449).

### Security

- Browser push addresses that resolve to a private or local network are now refused when the server connects, not just when they're saved.

## [2.2.0] - 2026-09-13

### Added

- On wide screens, the booking result on `/lookup` and `/booking-confirmation` stays in view while the page scrolls.

## [2.1.0] - 2026-09-11

### Added

- Sharing a booking now includes a link to it, on web and in the native app. Browsers without a share sheet copy the link instead.

## [2.0.1] - 2026-09-05

### Added

- The email settings page previews the booking confirmation guests will get. Also available as `GET /api/admin/email-settings/preview`.

### Fixed

- Admin push notifications only arrived for one location. A subscription now covers every location, and existing subscriptions are migrated.
- One browser with bad push keys stopped every other admin from getting notifications.

## [2.0.0] - 2026-09-03

Hey everyone,

This is a massive update for OpenResto. When I started this project a few months ago, I just wanted something that let restaurants take table bookings online. As I've built it, I've learned a lot about software and what people expect from a product, and this release is the result. The highlights are a native app that self-hosters can publish themselves, and translations into multiple languages.

With the native app, the CLI and the API, OpenResto is truly "open": the booking logic is the product, and any frontend can use it. The web frontend is still the priority for the admin.

I think I've fully realised the vision I had for it, and it will keep being maintained and improved. I hope you enjoy using it as much as I enjoyed building it!

### Breaking Changes

- **The CLI's `users create`, `users role` and `users reset-password` commands are removed**, and the server refuses those actions to any API key. Create accounts in the admin UI.
- **Guest endpoints that take a booking reference (lookup, cancel) are limited to 10 requests per minute per IP.** If many guests share one IP (an office, a hotel), raise the limit in your reverse proxy.
- **New booking references end in four digits** (`crispy-basil-thyme-0482`). Existing references still work.

### Added

- **Native guest app** that self-hosters can build and publish to the App Store and Play Store under their own accounts (#388). See [native app guide](https://docs.openres.to/guides/native-app/).
- **Booking reminders** by push notification, in the native app and in browsers (#419).
- **Apple Wallet and Google Wallet passes** for bookings (#420).
- **Settings → Native app page** that checks whether the server is ready for the app and shows which app versions are in use. You can also set a minimum app version.
- **Privacy policy URL** in brand settings, linked from the guest footer.
- **CLI:** `status`, `bookings extend`, `bookings email`, `locations extend` and `locations conflicts`.
- **CLI on npm:** `npm install -g openresto-cli`. The Docker image still ships.
- **Read-only `email` API key scope** to check whether mail is configured and delivering.
- **API Keys screen** now shows how to use a key, with a curl example and links to the CLI and [API guide](https://docs.openres.to/api/authentication/) (#409).

### Changed

- The native app uses the platform's own tab bar (#426).
- Party size in the native app is a stepper instead of a dropdown (#424).
- Booking and lookup forms let the phone autofill name and email (#427).
- The admin sidebar's booking search is removed. Press `/` to jump to the bookings list search.
- API key permissions are one None / Read / Write choice per resource.

### Removed

- Unused files in the repository root: `nginx.conf`, `docker-compose.dev.yml` and `DOCKER_README.md`.

### Security

- An API key can no longer create an admin login or take over an existing one.
- Booking references are now random enough that they can't be guessed.
- A failed cancellation no longer writes the guest's email to the log.
- The CLI no longer follows redirects, so it can't send its API key to another server.
- A made-up `X-API-Key` header no longer raises the caller's rate limit.
- A demo reset now deletes API keys created by visitors.

### Fixed

- The native app looks and behaves like an app rather than a shrunken website: correct dark mode, headers, status bar, shadows and layout on tablets.
- Language and theme can be changed from the native app's home screen.
- The service floor counted combinable tables twice, and some of its labels weren't translated.
- The new API key dialog could say "Copied" when nothing was copied.
- API keys without `guests:read` can email guests again.
- Sending email with no SMTP configured now returns a clear `email.not_configured` error.
- Layout and accessibility fixes for the scroll-to-top button and the API key dialog.

## [1.9.0] - 2026-08-27

Hi all! The headline this time is languages: OpenResto now speaks English, French, Spanish and German across the guest site and the admin. The admin timetable has also been rebuilt, and changing a location's hours now tells you which bookings no longer fit.

### Added

- **French, Spanish and German translations**, with a language switcher (#368, #374). Set the default language with `OPENRESTO_DEFAULT_LOCALE`.
- **Error messages from the server are translated** too (#375).
- **Schedule conflicts:** after narrowing a location's hours, the admin lists the upcoming bookings that no longer fit, and the dashboard shows the total (#359).
- Moving a booking pre-fills an email telling the guest about the change.
- Changing a location's timezone warns you if it has upcoming bookings (#362).

### Changed

- The admin timetable shows bookings at their real start and end times, including combined tables and bookings whose table was deleted (#334).
- Admin booking search matches name, email or reference, including partial matches (#358).
- The guest language picker is in the navbar menu (#387).
- Small layout improvements to the new-booking form and the location page.

### Fixed

- Editing a booking could double-book a table. The conflict check had never run.
- Late-night slots after midnight were offered and then refused (#363).
- Walk-in-only days were wrongly reported as schedule conflicts.
- The scroll-to-top button covered the footer's links (#352, #353).
- Bookings outside current opening hours disappeared from the timetable, and overnight hours broke it.
- Several smaller admin fixes: a stuck cancel button, the notifications filter toggle, opening a conflicting booking in place.

### Security

- Unexpected server errors no longer return internal details outside Development.
- Patched `image-size` denial-of-service advisories (GHSA-w3rx-r6r6-pgpr, GHSA-5p2g-fcmc-qvqq).

## [1.8.0] - 2026-08-17

Hey everyone! This is a meaty update: multiple admin accounts with Owner and Manager roles, an audit trail, and another frontend pass. Enjoy!

### Added

- **Multiple admin accounts** with Owner and Manager roles (#265). Owners can invite, promote, deactivate and reset other users. Your existing admin becomes the Owner when you upgrade.
- **Activity log** showing who did what in the admin, visible to Owners (#327). Entries are kept for a year by default and can't be deleted.
- **"Book now" button** on every location card (#328).
- **Location picker** inside the booking panel (#329).
- **Live preview** of the home page next to the brand settings.

### Changed

- Settings is split into Brand, Email, Users and Account pages (#322).
- Brand and location settings save automatically, with ten seconds to undo.
- The brand settings are split into five cards.
- Row actions in the admin are labelled buttons instead of bare icons.
- Archiving and deleting act on the location you have selected, and a location must be archived before it can be deleted (#335). Deleting is Owner-only.
- `/lookup` and `/booking-confirmation` are now the same screen (#337). Server errors no longer show as "No booking found".

### Fixed

- **Upgrading to the multi-user schema failed on existing installs.**
- Pausing bookings blocked every future date, not just the pause window (#326).
- Walk-in-only days could still be filled in and submitted (#320), and the date picker couldn't leave them.
- Dropdowns and the date picker now work fully with a keyboard and screen reader (#348, #350).
- Clearing the favicon never saved.
- Many smaller visual fixes to dropdowns, settings cards, notification rows and the email settings banner.

## [1.7.0] - 2026-08-12

This is a frontend-focused release. I wanted interfaces that feel familiar on both mobile and web, and I gave the code some love with a lot of refactoring and accessibility work.

Next up is accounts. The app is currently scoped to one admin, and it's now in good enough shape to think about restaurants with several locations, or one restaurant split into areas like upstairs, downstairs and patio.

Cheers!

### Added

- **Redesigned Locations page** for comparing locations: one filter bar for party size, date and time, smaller cards, and booking in a side panel or bottom sheet (#302, #307, #308).
- **Accessibility pass:** every button and control now has a name and role for screen readers (#303, #305, #306, #314).

### Changed

- The home page behaves more like an app: loading placeholders, a swipeable highlights row, and press feedback.
- Smoother page and panel transitions.
- The scroll-to-top button appears on every screen size.
- Release tooling now checks that every version number matches the tag.

### Fixed

- The booking form used the desktop two-column layout on phone browsers.
- Dark mode flashed white between pages.
- Layout fixes for the Locations filter bar on narrow screens.
- Dragging the booking sheet down could reload the page.
- A startup database check threw a SQL error.
- VPS deploys could run out of disk space and briefly return 502s.

### Security

- ASP.NET Core and EF Core 10.0.11, patching `Microsoft.OpenApi` (GHSA-v5pm-xwqc-g5wc) and `SQLitePCLRaw.lib.e_sqlite3` (GHSA-2m69-gcr7-jv3q).
- `nanoid` pinned to `^3.3.17` (GHSA-2v37-7h3g-55p8).

## [1.6.1] - 2026-08-09

### Added

- **Numeric booking references** (#179). A location can give out digit-only references (`48273910`) instead of words.

### Fixed

- Numeric references broke the booking confirmation page.
- Confirmation screen options didn't apply on native.
- Uploaded demo images were wiped by the demo reset.

## [1.6.0] - 2026-08-07

Hello! The headline of this release is **combinable table groups**: mark tables that can be pushed together (tables 8 and 9 become one 6-top), and availability, auto-assign, holds and the booking form all understand them. Combinable tables can still be booked on their own; they're just filled last.

Also in here: per-location contact info, safer deletes, and a refreshed table and section settings screen.

### Added

- **Combinable table groups** (#271, #272, #273, #274).
- **Contact phone and email per location**, with brand-wide defaults (#262).
- **Delete confirmations** for tables and sections that show how many upcoming bookings are affected (#270).
- Seat counts are dropdowns limited to 1–50.

### Changed

- Redesigned table and section settings.
- Dependency security patch for `brace-expansion` (GHSA-rgw5-rvv9-x895).

### Fixed

- **Combined tables could be double-booked.**
- Combinable tables were only bookable as a group (#242).
- Deleting or shrinking a table in a group left the group advertising seats it didn't have (#242).
- Group bookings showed no table to the guest, and groups ignored the selected section.
- Dropdown options couldn't be clicked on web.
- Clearing a location's description or menu link didn't save.

## [1.5.0] - 2026-07-30

Hello! This release includes an Expo upgrade, routine updates and a couple of new features.

### Added

- Bookings are blocked when the party is larger than any table, with a prompt to contact the restaurant (#261). Single-location sites open the location automatically.
- Server-side validation for social links, highlights and menu URLs (#264).

### Fixed

- Booking times defaulted to midnight (#257).
- The pre-commit linter touched unrelated files (#260).

### Changed

- Expo SDK 57 (#267).
- Dependency security and routine updates (#259, #266).

## [1.4.1] - 2026-07-26

Hello! No major changes today. I'm upgrading from Node 20 to Node 24, which is now LTS.

As always, let me know if there are any features you'd like to see!

- Node 24 (#252).
- Dependency security patches (#253, #254).

## [1.4.0] - 2026-07-20

Hello! This release reworks the booking pages. As always, let me know if you run into any issues!

### Added

- **Redesigned navigation** (#196, #205, #211, #240). One Locations page for browsing and booking, full weekly hours, and a menu with theme and help.
- **"Any section"** is the default in the booking form, and the server picks the table (#243, #248).
- **Booking start interval** of 15, 30 or 60 minutes, separate from booking length (#245, #247).
- **Maximum table oversize** so small parties don't get large tables (#244, #249).
- **Menu PDF upload** (#246, #250).

### Fixed

- Admin pages clashed with the new `/locations` page. Admin routes now live under `/admin`.
- Locations card and overflow menu polish (#241).

## [1.3.1] - 2026-07-17

- Admin bookings page styling and column widths.
- The location description now shows on the booking page.

## [1.3.0] - 2026-07-16

Hello again! This release adds home page customisation, dashboard polish, and a large internal refactor that shouldn't change anything you see.

### Added

- **Home page customisation** (#183, #184, #185, #187): subtitle, location description with links, clickable highlight cards, and hero image fit.
- Sortable admin bookings list (#208).
- Occupancy chart improvements (#180).
- A dropdown time picker on web.

### Fixed

- The UI shows why a table hold was refused (#213).
- The timezone hint is hidden when it matches your own (#181).
- Occupancy chart and footer layout fixes (#223, #224, #225, #226).
- ASP.NET Core and EF Core 10.0.10.

## [1.2.1] - 2026-07-06

- Fixed dates showing incorrectly on the home page.
- Added a native calendar view with closed days blocked out.
- Fixed the new Lucide icons.

## [1.2.0] - 2026-07-03

This one's mostly driven by your feedback. Thanks for all the issues and comments since 1.1.0! Please open an issue if anything looks off after upgrading.

### Added

- **Opening hours per day of the week** (#175).
- **Walk-in-only locations**, or walk-in-only days (#176).
- **Booking length per location** instead of a fixed hour (#135, #177).
- Admins can change their own email (#172).
- **Footer with social links**, which is also where the Admin link now lives (#186, #182).
- Keyboard shortcuts (#140) and haptic feedback on mobile (#147).
- Five more favicon icons (#188).
- Caching headers in nginx.

### Fixed

- Guests could cancel or book bookings in the past (#159, #160).
- The admin dashboard didn't refresh after actions (#93).
- Calendar links ignored the booking length (#192).
- Confirmation email formatting.
- nginx ports are bound to `127.0.0.1`.

## [1.1.1] - 2026-06-29

- Fixed the admin email not being set from environment variables in Docker.
- Fixed the release job's handling of the `v` prefix.

## [1.1.0] - 2026-06-22

Hello! Thanks for reading, and for the 50 stars on GitHub! This release builds on 1.0.0 with feedback-driven polish and some cleanup. Let me know if you run into any issues.

### Added

- **Booking controls:** pause new bookings or extend active ones by an hour.
- Redesigned Location Manager.
- Restaurant photo and a shared template in emails.
- "Opens in Xh Ym" on closed restaurants.
- Configurable website URL for links in emails.

### Fixed

- Bookings with no end time counted as active forever.
- The email's "Manage your booking" link opens the booking directly.
- Header images in emails used relative URLs.
- HSTS enabled in the production nginx config.
- Faster arm64 Docker builds.

## [1.0.0] - 2026-06-17

### Added

- Multi-restaurant booking with no guest accounts. Guests use a booking reference.
- Admin dashboard for bookings, tables, sections, pausing and branding.
- Five-minute table holds during checkout.
- Timezone-aware availability.
- Lunch, Dinner and Off-Peak slot labels.
- White-label branding with dynamic PWA icons.
- Optional email (SMTP) and push notifications (VAPID).
- Permanent booking deletion for GDPR.
- Multi-arch Docker images on GHCR and a pinned `docker-compose.yml` per release.
- Automatic database migrations on startup. See [backup and restore guide](https://docs.openres.to/self-hosting/backup-restore/).

[1.0.0]: https://github.com/karanshukla/openresto/releases/tag/v1.0.0
[1.1.0]: https://github.com/karanshukla/openresto/releases/tag/v1.1.0
[1.1.1]: https://github.com/karanshukla/openresto/releases/tag/v1.1.1
[1.2.0]: https://github.com/karanshukla/openresto/releases/tag/v1.2.0
[1.2.1]: https://github.com/karanshukla/openresto/releases/tag/v1.2.1
[1.3.0]: https://github.com/karanshukla/openresto/releases/tag/v1.3.0
[1.3.1]: https://github.com/karanshukla/openresto/releases/tag/v1.3.1
[1.4.0]: https://github.com/karanshukla/openresto/releases/tag/v1.4.0
[1.4.1]: https://github.com/karanshukla/openresto/releases/tag/v1.4.1
[1.5.0]: https://github.com/karanshukla/openresto/releases/tag/v1.5.0
[1.6.0]: https://github.com/karanshukla/openresto/releases/tag/v1.6.0
[1.6.1]: https://github.com/karanshukla/openresto/releases/tag/v1.6.1
[1.7.0]: https://github.com/karanshukla/openresto/releases/tag/v1.7.0
[1.8.0]: https://github.com/karanshukla/openresto/releases/tag/v1.8.0
[1.9.0]: https://github.com/karanshukla/openresto/releases/tag/v1.9.0
[2.0.0]: https://github.com/karanshukla/openresto/releases/tag/v2.0.0
[2.0.1]: https://github.com/karanshukla/openresto/releases/tag/v2.0.1
[2.1.0]: https://github.com/karanshukla/openresto/releases/tag/v2.1.0
[2.2.0]: https://github.com/karanshukla/openresto/releases/tag/v2.2.0
[2.3.0]: https://github.com/karanshukla/openresto/releases/tag/v2.3.0
[2.3.1]: https://github.com/karanshukla/openresto/releases/tag/v2.3.1
[Unreleased]: https://github.com/karanshukla/openresto/compare/v2.4.0...HEAD
[2.4.0]: https://github.com/karanshukla/openresto/compare/v2.3.1...v2.4.0
