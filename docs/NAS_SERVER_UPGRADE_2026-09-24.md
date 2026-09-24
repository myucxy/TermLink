# NAS Server upgrade — 2026-09-24

Completed at 14:52 UTC (22:52 CST) on NAS `192.168.9.11`, existing Compose
project `termlink`, `/volume1/docker/termlink/docker-compose.yaml`.

## Image and deployment

- Target: `docker.io/myucxy/termlink:v0.2.61@sha256:68611748731e2039ce6f1411fd123983deed2577b07e28b657ed9abcd3e90be2`.
- Image ID: `sha256:6f16ac1c9bfa5d6f6822aa7902dafeb0c057b50768b2816ec0e1cd505fc46127`.
- OCI version: `0.2.61+117`; revision: `9be56278d83c5ab1b2bd26e6fa34417f4db9b579`.
- NAS and published image architecture: `linux/amd64`.
- Previous image ID: `sha256:918432634a2141a26e034eea5674dcb3aab6e4dc6ef45bc6363f7ab5b7c5d5da`.
  Its labels report `dev` / unknown revision; do not infer its precise version
  from the old floating `latest` reference.

Only the Server image default in the existing Compose file changed. Rendered
Compose configurations were compared with only that field allowed to differ.
`docker compose up -d --no-deps --pull never --wait server` replaced Server.
The PostgreSQL container, data volume `termlink_postgres-data`, Server
environment, HTTP port 8914 and public URL `https://termlink.kget.cc:8910` were
preserved. The gateway and local Windows-hosted Server were not modified.

NAS registry layer downloads reset/stalled. The already verified local image
was exported with `docker image save`, transferred over SSH and imported with
`docker load`. A subsequent pull of the exact published `tag@digest` succeeded
using the imported layers. The deployed container uses that registry reference;
both config ID and revision were checked before and after deployment. No NAS
DNS, proxy or Docker daemon setting was changed. This reuses the existing
single-platform artifact; no new Cosign or multiarch qualification is claimed.

## Backup and migration rehearsal

Encrypted backups are on the Windows maintenance machine:

- `artifacts/backups/termlink-nas-pre-v0.2.61-20260924T144344Z.dump.dpapi`
- `artifacts/backups/termlink-nas-pre-v0.2.61-20260924T144344Z.config.json.dpapi`

The custom PostgreSQL dump is 31,851,042 bytes before encryption. Both files
passed a disk-read/decryption/hash round trip. This legacy Compose deployment
has no managed `termlink-deploy` entrypoint, age tool or configured recipient;
the existing Windows DPAPI CurrentUser backup approach was reused. **Recovery
requires this Windows account's DPAPI profile**, unlike an independently held
age identity. No cleartext dump or supplied SSH password was written to disk.

The dump was fully restored into a temporary PostgreSQL container with no
external network. The exact target Server image then applied migrations 49
and 50 against that copy and passed its healthcheck. Those migrations add the
direct-routing policy and runtime-settings tables. Schema advanced from 48 to
50; all 2 users and 3 devices remained. Both temporary test containers were
removed after verification.

## Post-upgrade verification

- Server and PostgreSQL are healthy; Server restart count is zero.
- PostgreSQL container ID and mounts are unchanged.
- All `TERMLINK_*` environment values and port bindings are unchanged.
- Live database schema is 50; 2 users and 3 devices remain.
- NAS loopback, LAN `http://192.168.9.11:8914`, and public
  `https://termlink.kget.cc:8910` health/readiness checks pass.
- OIDC discovery advertises `https://termlink.kget.cc:8910/realms/termlink`.

Public HTTP checks alone do not establish which upstream Lucky selected; no
gateway configuration change or new Android/Windows session/LAN/NAT acceptance
is claimed. NAS container identity and local endpoint checks establish the
NAS upgrade independently.

Sanitized evidence is in `artifacts/validation/nas-upgrade-*.json`, including
backup, migration rehearsal, execution, result and external checks.

## Rollback

The previous image is retained on NAS. Root-only configuration and container
snapshots are under
`/volume1/docker/termlink/.upgrade-v0.2.61-20260924T144344Z`.
Restore the old Compose content with its Server image explicitly pinned to
the previous image ID above, then recreate only Server with `--no-deps --pull
never --wait`. Do not restore the floating `latest` default, delete volumes or
recreate PostgreSQL. Migrations 49/50 are additive; a complete data rollback
requires a separately authorized restore of the encrypted pre-upgrade dump.

Temporary SSH credential environment and askpass helper were cleared after
verification. No credential was added to Git or saved as a new permanent login.
