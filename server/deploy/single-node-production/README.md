# TermLink single-node production deployment

This package installs the supported single-server topology:

```text
Caddy or an external TLS proxy
              |
        TermLink Server
              |
          PostgreSQL
```

TermLink Server embeds OAuth 2.0/OpenID Connect, account management, database
migrations, realtime coordination, and Direct/NAT signalling. PostgreSQL is the
only required long-running companion service. Keycloak, Valkey, MinIO, and
separate migration services are not used. A network-isolated one-shot container
copies host secrets into owner-only Docker volumes before PostgreSQL and Server
start; it does not remain running.

The public identity contract remains unchanged. Web, Android, and Windows use
the same `/realms/termlink` issuer, client IDs, redirect URIs, claims, and Web
paths as before.

## Prerequisites

- A Linux server with a public DNS name.
- Docker Engine with the Docker Compose v2 plugin.
- `bash`, `curl`, `openssl`, GNU coreutils, and GNU tar.
- An immutable signed TermLink image reference in `tag@sha256:digest` form.
- For managed TLS, DNS must point to the server and inbound TCP 443 must reach it.
- `age` is required only for backup and restore.

Do not use `server/acceptance` Compose files for production.

## Install

Run from a release source checkout:

```bash
cd server/deploy/single-node-production
sudo bash ./termlink-deploy install \
  --domain termlink.example.com \
  --email admin@example.com \
  --image docker.io/myucxy/termlink:0.2.41@sha256:<64-hex-digest>
```

The default data directory is `/opt/termlink`. The installer creates private
runtime and secret directories, starts PostgreSQL and one Server replica, then
checks migrations, health, and OIDC discovery. Server creates the initial
administrator transactionally only when the embedded identity database is
empty.

View the initial password exactly once:

```bash
sudo /opt/termlink/termlink-deploy credentials
```

The username is `admin`. Change the generated password after the first login.

## External TLS

To keep an existing host reverse proxy, use:

```bash
sudo bash ./termlink-deploy install \
  --domain termlink.example.com \
  --email admin@example.com \
  --tls external \
  --http-port 8080 \
  --image docker.io/myucxy/termlink:0.2.41@sha256:<64-hex-digest>
```

The application origin then listens only on `127.0.0.1:8080`. The external
proxy must terminate HTTPS for the configured domain, preserve the `Host`
header, set `X-Forwarded-Proto: https`, and support WebSocket upgrades. Do not
publish the origin port to another machine.

## Operations

```bash
# Health, topology, OIDC discovery, and migration state
sudo /opt/termlink/termlink-deploy doctor

# Upgrade code and runtime assets; an encrypted backup runs first when configured
sudo bash ./termlink-deploy upgrade \
  --image docker.io/myucxy/termlink:0.2.42@sha256:<64-hex-digest>

# Show current non-secret configuration
sudo /opt/termlink/termlink-deploy config

# Change the public domain and reapply the stack
sudo /opt/termlink/termlink-deploy config \
  --domain new.termlink.example.com \
  --email admin@example.com

# Stop and remove containers and networks, preserving Docker volumes and files
sudo /opt/termlink/termlink-deploy uninstall --keep-data
```

Run `upgrade` from the newly downloaded deployment directory when deployment
assets changed. Running the installed copy is sufficient for an image-only
upgrade.

An older installation that still runs Keycloak must not be upgraded in place
without first exporting and importing its identities. For a reset deployment,
install into a clean data directory instead. The offline
`identity-import-keycloak` command remains available for deliberate migration;
it is not part of the production runtime.

## Encrypted backup and restore

Create and protect an age identity outside the server. Configure its public
recipient during install or later:

```bash
sudo /opt/termlink/termlink-deploy config \
  --backup-recipient age1example...

sudo /opt/termlink/termlink-deploy backup \
  --output /var/backups/termlink/termlink-$(date -u +%Y%m%d).tar.age
```

The v4 archive contains one custom-format database dump, including embedded
identity data, plus critical file-backed secrets, metadata, and a SHA-256
manifest. The plaintext staging directory is private and removed after
encryption. Keep the archive outside `/opt/termlink` and keep the age identity
off the server.

Restore is intentionally explicit and destructive:

```bash
sudo /opt/termlink/termlink-deploy restore \
  --input /var/backups/termlink/termlink-20260906.tar.age \
  --identity /secure/off-host-copy/age-key.txt \
  --confirm-restore
```

Restore rejects unexpected paths, links, missing files, and hash mismatches. It
stops Server before replacing the TermLink database, restores the secret set,
preserves the new server's domain configuration, and leaves the application
stopped if any destructive step fails.

A fresh server can restore during install with `--restore` and `--identity`.

## Managed files

```text
/opt/termlink/
  .termlink-managed
  config.env                 # non-secret deployment settings
  secrets/                   # mode 0700, file-backed secrets mode 0600
  runtime/                   # reviewed Compose and optional Caddy assets
  termlink-deploy
```

PostgreSQL and optional Caddy state remain in named Docker volumes. The tool
never deletes those volumes. Always create and verify an encrypted backup before
manually removing Docker data.
