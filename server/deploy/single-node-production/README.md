# TermLink single-node production deployment

This package installs the supported single-server topology:

```text
Caddy or an external TLS proxy
              |
        TermLink Server
          /         \
   PostgreSQL      Keycloak
```

Valkey, MinIO, a separate migration job, and separate Direct/NAT signalling
services are not used. TermLink Server owns those short-lived single-node
responsibilities. PostgreSQL still stores the TermLink and Keycloak databases
separately.

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
  --image docker.io/myucxy/termlink:0.2.38@sha256:<64-hex-digest>
```

The default data directory is `/opt/termlink`. The installer creates private
runtime and secret directories, builds the pinned Keycloak theme image, starts
one Server replica, reconciles Keycloak, runs embedded database migrations, and
then performs health and OIDC discovery checks.

View the temporary initial password exactly once:

```bash
sudo /opt/termlink/termlink-deploy credentials
```

The username is `admin`; Keycloak requires the password to be changed at first
login.

## External TLS

To keep an existing host reverse proxy, use:

```bash
sudo bash ./termlink-deploy install \
  --domain termlink.example.com \
  --email admin@example.com \
  --tls external \
  --http-port 8080 \
  --image docker.io/myucxy/termlink:0.2.38@sha256:<64-hex-digest>
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
  --image docker.io/myucxy/termlink:0.2.39@sha256:<64-hex-digest>

# Show current non-secret configuration
sudo /opt/termlink/termlink-deploy config

# Change the public domain and reapply Keycloak and the stack
sudo /opt/termlink/termlink-deploy config \
  --domain new.termlink.example.com \
  --email admin@example.com

# Stop and remove containers and networks, preserving Docker volumes and files
sudo /opt/termlink/termlink-deploy uninstall --keep-data
```

Run `upgrade` from the newly downloaded deployment directory when deployment
assets changed. Running the installed copy is sufficient for an image-only
upgrade.

## Encrypted backup and restore

Create and protect an age identity outside the server. Configure its public
recipient during install or later:

```bash
sudo /opt/termlink/termlink-deploy config \
  --backup-recipient age1example...

sudo /opt/termlink/termlink-deploy backup \
  --output /var/backups/termlink/termlink-$(date -u +%Y%m%d).tar.age
```

The v3 archive contains custom-format dumps for both databases, critical
file-backed secrets, metadata, and a SHA-256 manifest. The plaintext staging
directory is private and removed after encryption. Keep the archive outside
`/opt/termlink` and keep the age identity off the server.

Restore is intentionally explicit and destructive:

```bash
sudo /opt/termlink/termlink-deploy restore \
  --input /var/backups/termlink/termlink-20260906.tar.age \
  --identity /secure/off-host-copy/age-key.txt \
  --confirm-restore
```

Restore rejects unexpected paths, links, missing files, and hash mismatches.
It stops Server and Keycloak before replacing either database, restores the
secret set, preserves the new server's domain configuration, reruns identity
reconciliation, and leaves application services stopped if any destructive
step fails.

A fresh server can restore during install with `--restore` and `--identity`.

## Managed files

```text
/opt/termlink/
  .termlink-managed
  config.env                 # non-secret deployment settings
  secrets/                   # mode 0700, file-backed secrets mode 0600
  runtime/                   # reviewed Compose, Keycloak, and Caddy assets
  termlink-deploy
```

PostgreSQL, Keycloak, and Caddy durable state remains in named Docker volumes.
The tool never deletes those volumes. Always create and verify an encrypted
backup before manually removing Docker data.
