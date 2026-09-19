# TermLink single-node production deployment

This package installs the supported single-server topology:

```text
       External HTTPS proxy
              |
        TermLink Server
              |
          PostgreSQL
```

TermLink Server embeds OAuth 2.0/OpenID Connect, account management, database
migrations, realtime coordination, and Direct/NAT signalling. PostgreSQL is the
only required long-running companion service. Keycloak, Valkey, MinIO, and
separate migration services are not used. A network-isolated startup helper
copies host secrets into owner-only Docker volumes before PostgreSQL and Server
start; the installer removes its exited container after startup, so the steady
state contains only Server and PostgreSQL.

The public identity contract remains unchanged. Web, Android, and Windows use
the same `/realms/termlink` issuer, client IDs, redirect URIs, claims, and Web
paths as before.

## Prerequisites

- A Linux server with a public DNS name.
- Docker Engine with the Docker Compose v2 plugin.
- `bash`, `curl`, `openssl`, GNU coreutils, and GNU tar.
- The released single-file `termlink-deploy` installer. Its Server image is
  already pinned by tag and digest.
- An existing HTTPS reverse proxy such as Nginx, Traefik, Lucky, or a cloud
  tunnel. It must support WebSocket upgrades.
- `age` is required only for backup and restore.

Do not use `server/acceptance` Compose files for production.

## Install

Download the installer for the desired TermLink release, verify the published
SHA-256 checksum, and run it. No repository checkout, Compose file, or image
digest argument is required:

```bash
curl --fail --location --output termlink-deploy https://RELEASE-URL/termlink-deploy
echo '<published-sha256>  termlink-deploy' | sha256sum --check
sudo bash ./termlink-deploy install
```

The installer asks only for the public domain. For automation, pass it with
`--domain`. A source-tree installer has no release image embedded and
additionally requires `--image`.

The default data directory is `/opt/termlink`. The installer creates private
runtime and secret directories, starts PostgreSQL and one Server replica, then
checks migrations, health, and OIDC discovery. Server creates the initial
administrator transactionally only when the embedded identity database is
empty.

Interactive installation prints the initial password exactly once. If output is
redirected or installation is non-interactive, retrieve it exactly once with:

```bash
sudo /opt/termlink/termlink-deploy credentials
```

The username is `admin`. Change the generated password after the first login.

## HTTPS proxy

TermLink intentionally does not install or operate a TLS proxy. Configure the
host's existing reverse proxy first, then install with its public domain:

```bash
sudo bash ./termlink-deploy install \
  --domain termlink.example.com \
  --http-port 8080
```

The application origin then listens only on `127.0.0.1:8080`. The external
proxy must terminate HTTPS for the configured domain, preserve the `Host`
header, set `X-Forwarded-Proto: https`, and support WebSocket upgrades. Do not
publish the origin port to another machine.

If the public proxy listens on a non-standard HTTPS port, add
`--public-port PORT`. This changes generated OIDC URLs but does not expose that
port from Docker.

## Operations

```bash
# Health, topology, OIDC discovery, and migration state
sudo /opt/termlink/termlink-deploy doctor

# Download the newer release's one-file installer, then upgrade. Its pinned
# image and embedded runtime assets are selected automatically.
sudo bash ./termlink-deploy upgrade

# Show current non-secret configuration
sudo /opt/termlink/termlink-deploy config

# Change the public domain and reapply the stack
sudo /opt/termlink/termlink-deploy config \
  --domain new.termlink.example.com

# Stop and remove containers and networks, preserving Docker volumes and files
sudo /opt/termlink/termlink-deploy uninstall --keep-data
```

Run `upgrade` with the newly downloaded installer. The installed copy remains
useful for `doctor`, `config`, `backup`, `restore`, and `uninstall`, but it does
not silently follow a floating image tag.

Release maintainers generate the downloadable one-file installer with:

```powershell
.\Build-Installer.ps1 `
  -ServerImage 'docker.io/myucxy/termlink:VERSION@sha256:DIGEST' `
  -OutputPath '.\out\termlink-deploy'
```

The single-node release uses TermLink's embedded identity provider. Deploy a
new release into a clean data directory when replacing an older Keycloak-based
installation; identity migration is intentionally outside the supported
runtime.

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
  runtime/                   # Compose asset rendered by the installer
  termlink-deploy            # installed copy of the single-file tool
```

PostgreSQL state remains in a named Docker volume. The tool never deletes that
volume. Always create and verify an encrypted backup before manually removing
Docker data.
