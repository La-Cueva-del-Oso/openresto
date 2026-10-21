---
title: Backup and restore
description: What to save, how to save it, and how to get it back.
sidebar:
  order: 9
---

This page shows what to back up and how to restore it. Everything OpenResto saves lives in two
Docker volumes, so there is no separate database server or storage service to worry about.

## What to back up

| What                            | Where                                      | Why it matters                                                                                                                                                                                                                  |
| ------------------------------- | ------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The database, `openresto.db`    | `/data` in the backend (volume `db_data`)  | **Everything**: bookings, locations, tables, accounts, brand and email settings.                                                                                                                                                |
| Data Protection keys            | `/data/dp-keys`, in the same volume        | Encrypt the saved SMTP password and the guest "recent bookings" cookie. Lose them and you re-enter the SMTP password; no bookings are lost. **Optional, and kept apart from the database backup** (see [Keys](#keys-optional)). |
| Uploaded media                  | `/app/wwwroot/media` (volume `media_data`) | Hero image, location photos and menu PDFs.                                                                                                                                                                                      |
| `.env` and `docker-compose.yml` | Next to each other on the host             | Your secrets and settings, including the VAPID keys browsers subscribed with.                                                                                                                                                   |
| `./wallet`, `./well-known`      | Next to `docker-compose.yml`               | Only if you set up Wallet passes or a native app.                                                                                                                                                                               |

The commands below are run from the directory that holds `docker-compose.yml`. They reach the
volumes through the backend container (`--volumes-from`), so they work whatever Docker named
the volumes. Docker prefixes them with the project name, so `db_data` is really something like
`openresto_db_data`, which `docker volume ls` shows.

## Back up

Stopping the backend for a few seconds gives you a clean copy, since SQLite finishes writing
everything when it shuts down. Guests see the site as unavailable for that moment.

```bash
mkdir -p backups
docker compose stop backend

docker run --rm --volumes-from "$(docker compose ps -aq backend)" \
  -v "$PWD/backups":/backups alpine \
  tar czf "/backups/openresto-$(date +%Y%m%d-%H%M%S).tar.gz" --exclude=data/dp-keys -C / data app/wwwroot/media

docker compose start backend
```

That archive holds the database and the media, and leaves the keys out on purpose. The database
keeps the SMTP password encrypted and the keys are what decrypt it, so an archive holding both
(and archives get copied around, emailed and left on shared drives) hands over the mail account.
Copy `.env` and `backups/` somewhere off the server too: a backup on the same disk doesn't help
if the disk dies.

### Keys (optional)

Skipping the keys costs you little: after a restore you enter the SMTP password again under
**Settings → Email**, and guests' "recent bookings" cookies stop working until they book or look
up a booking again. If you would rather not redo that, back the keys up on their own, encrypted
with a passphrase, and keep that file somewhere other than the database backups:

```bash
docker run --rm --volumes-from "$(docker compose ps -aq backend)" alpine \
  tar cz -C /data dp-keys \
  | openssl enc -aes-256-cbc -pbkdf2 -salt -out "backups/openresto-keys-$(date +%Y%m%d).tar.gz.enc"
```

`openssl` asks for the passphrase; store it in a password manager, not next to the file. To put
the keys back (with the backend stopped):

```bash
openssl enc -d -aes-256-cbc -pbkdf2 -in backups/openresto-keys-DATE.tar.gz.enc \
  | docker run --rm -i --volumes-from "$(docker compose ps -aq backend)" alpine tar xz -C /data
```

### Without stopping anything

To back up while OpenResto keeps running, let SQLite take a snapshot of itself, then copy the
snapshot out:

```bash
docker compose exec backend sqlite3 /data/openresto.db ".backup /data/openresto-snapshot.db"

docker run --rm --volumes-from "$(docker compose ps -aq backend)" \
  -v "$PWD/backups":/backups alpine \
  mv /data/openresto-snapshot.db "/backups/openresto-$(date +%Y%m%d-%H%M%S).db"
```

This covers the database only. Back up media with the archive above when it changes.

## Restore

The restore replaces what is in the volumes now. If you are unsure, take a fresh backup first.

```bash
docker compose stop backend

# Swap TIMESTAMP for the backup you want.
docker run --rm --volumes-from "$(docker compose ps -aq backend)" \
  -v "$PWD/backups":/backups alpine \
  sh -c 'find /data -mindepth 1 -maxdepth 1 ! -name dp-keys -exec rm -rf {} + && rm -rf /app/wwwroot/media/* && tar xzf /backups/openresto-TIMESTAMP.tar.gz -C /'

docker compose start backend
```

The keys already on the server are left alone, so restoring onto the same install keeps the SMTP
password working. On a new server there are no keys yet: restore the encrypted keys file first
(see [Keys](#keys-optional)) or enter the SMTP password again.

To restore a `.db` snapshot instead, stop the backend, copy it over `/data/openresto.db` the
same way (and delete any `openresto.db-wal` and `openresto.db-shm` beside it), then start it.

Afterwards, check the database is sound:

```bash
docker compose exec backend sqlite3 /data/openresto.db "PRAGMA integrity_check;"
# ok
```

## Automatic daily backups

This script backs up the database every night without stopping anything and keeps a week of
copies. Save it as `/opt/openresto/backup.sh`, change `COMPOSE_DIR` to your install directory,
and make it executable with `chmod +x`.

```bash
#!/bin/sh
set -e

COMPOSE_DIR=/opt/openresto
BACKUP_DIR="$COMPOSE_DIR/backups"
DATE=$(date +%Y%m%d-%H%M%S)

cd "$COMPOSE_DIR"
mkdir -p "$BACKUP_DIR"

docker compose exec -T backend sqlite3 /data/openresto.db ".backup /data/openresto-snapshot.db"
docker run --rm --volumes-from "$(docker compose ps -aq backend)" \
  -v "$BACKUP_DIR":/backups alpine \
  mv /data/openresto-snapshot.db "/backups/openresto-$DATE.db"

# Keep seven days.
find "$BACKUP_DIR" -name "openresto-*.db" -mtime +7 -delete

echo "$DATE backup complete: $BACKUP_DIR/openresto-$DATE.db"
```

Then schedule it for 3 a.m. with a file at `/etc/cron.d/openresto-backup`:

```txt
0 3 * * * root /opt/openresto/backup.sh >> /var/log/openresto-backup.log 2>&1
```

## The VPS compose file

`docker-compose.vps.yml` keeps the database in a `./data` folder on the host rather than a
volume. Every command above still works, because `--volumes-from` follows bind mounts too. You
can also just copy the folder while the backend is stopped:

```bash
docker compose -f docker-compose.vps.yml stop backend
DEST="./backups/openresto-data-$(date +%Y%m%d-%H%M%S)"
cp -r ./data "$DEST"
rm -r "$DEST/dp-keys"   # the keys travel separately, see above
docker compose -f docker-compose.vps.yml start backend
```

Add `-f docker-compose.vps.yml` to every `docker compose` command on this page when you use it.

## Before an upgrade

Take a backup first, every time. The database updates itself when a new version starts, and an
older version may not understand the updated database. The full steps are in
[Upgrading](/self-hosting/upgrading/).
