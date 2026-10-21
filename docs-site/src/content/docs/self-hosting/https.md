---
title: HTTPS and reverse proxies
description: Terminate TLS in front of OpenResto, or use the bundled VPS nginx.
sidebar:
  order: 4
---

This page shows how to add HTTPS (TLS, the encryption behind the browser's lock icon) to your
install.

The release stack serves plain HTTP on `HOST_PORT`. Please make sure guests and admins reach it
over HTTPS, because admin sign-ins and API keys travel over the connection. There are two ways
to do this.

## Option 1: a TLS-terminating proxy in front (recommended)

Use any reverse proxy (a program that receives visitors and passes them on) that handles HTTPS and forwards to the stack's `reverse-proxy` container:
Caddy, Traefik, Nginx Proxy Manager, a host nginx, or a Cloudflare Tunnel. Point it at
`http://<host>:<HOST_PORT>`.

Check these three things:

1. **`CORS_ORIGINS` is the public HTTPS address**, e.g. `https://bookings.example.com`. Browsers
   calling the API from any other address are turned away.
2. **`OPENRESTO_TRUSTED_PROXIES` names your proxy, and the proxy forwards `X-Forwarded-For`.**
   Rate limits, table holds and the audit log all key on the visitor's address. Without this
   setting the stack sees every visitor as your proxy, so they share one limit (10 sign-in
   attempts a minute in total) and one person can lock everyone else out. See
   [Keep visitors' real addresses](#keep-visitors-real-addresses) below. Most proxies send
   `X-Forwarded-For` by default.
3. **Large uploads can pass through.** Menu PDFs can be 10 MB. If your proxy has an upload size
   limit, set it to 12 MB or more.

A minimal Caddyfile is enough:

```txt
bookings.example.com {
    reverse_proxy localhost:80
}
```

If the proxy runs on the same host, set `HOST_PORT=8080` (or similar) in `.env` so it can own
port 80, and point `reverse_proxy` at that port.

### Keep visitors' real addresses

Set `OPENRESTO_TRUSTED_PROXIES` in `.env` to the address your proxy connects from: one or more
IPs or CIDR ranges (a network written like `172.18.0.0/16`), separated by commas. Then apply it:

```dotenv
OPENRESTO_TRUSTED_PROXIES=172.18.0.1
```

```bash
docker compose up -d
```

The stack then reads the visitor's address from `X-Forwarded-For`, but only on requests that
arrive from a listed address. Anyone else connecting directly cannot choose their own address by
sending that header. Left empty (the default), nothing is trusted, which is right when visitors
reach the stack directly with no proxy in front.

Which address to list depends on where the proxy runs:

- **Another machine:** that machine's address, for example `203.0.113.10`.
- **The same host, outside Docker** (a host Caddy or nginx): Docker shows the connection as coming
  from the bridge network's gateway, usually `172.x.0.1`, not `127.0.0.1`. Open your site through
  the proxy, then run `docker compose logs reverse-proxy`: the address that starts the log line
  is the one to list.
- **Another container** (Traefik, Nginx Proxy Manager, `cloudflared`): the Docker network the two
  share, for example `172.18.0.0/16` (`docker network inspect` shows the range).

List only proxies you control, since anything on the list can claim to be any visitor. To trust
a chain of proxies, list each of them. If you enter something that is not an address, the proxy
container refuses to start and says which value it did not understand.

## Option 2: the bundled VPS nginx

The repository includes `docker-compose.vps.yml`, an nginx that terminates TLS itself, redirects
HTTP to HTTPS and adds security headers. Unlike the release stack, it **builds from source**, so
clone the repository on the server.

You need a domain pointing at the server, plus a certificate and private key. Let's Encrypt
is a free, popular choice:

```bash
certbot certonly --standalone -d bookings.example.com
```

If your CA issues a separate chain file, concatenate it onto the certificate first
(`cat your_domain.crt your_domain.ca-bundle > server.crt`).

Create `.env` in the repository root:

```dotenv
DOMAIN_NAME=bookings.example.com
JWT_KEY=...
ADMIN_EMAIL=you@example.com
ADMIN_PASSWORD=...

# Paths ON THE HOST
HOST_SSL_CERT_PATH=/etc/letsencrypt/live/bookings.example.com/fullchain.pem
HOST_SSL_KEY_PATH=/etc/letsencrypt/live/bookings.example.com/privkey.pem
```

Then:

```bash
docker compose -f docker-compose.vps.yml up -d
```

`CORS_ORIGINS` is set to `https://${DOMAIN_NAME}` for you, so you don't need to set it. The
proxy listens on `127.0.0.1:8080` (HTTP) and `127.0.0.1:8443` (HTTPS) only, so something on
the host, such as a firewall rule or a host-level proxy, needs to forward public ports 80 and
443 to those. This keeps the container off the internet by design.

After Let's Encrypt renews the certificate, restart the container so nginx picks up the new one:

```bash
docker compose -f docker-compose.vps.yml restart reverse-proxy
```

## Check it

```bash
curl -I https://bookings.example.com/api/health
```

You should see `200`. If you don't, check that your domain points at the server and that ports 80 and 443 are open.
