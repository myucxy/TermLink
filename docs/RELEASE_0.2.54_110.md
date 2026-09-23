# TermLink 0.2.54 (110)

Date: 2026-09-23 (Asia/Shanghai)

## Scope

- Android and Windows AppShip stable; Server image on Docker Hub only.
- Live AppShip preflight: Android 0.2.53 (109), Windows 0.2.52 (108).
  Git tag v0.2.54 is unused. Docker Hub preflight is pending network recovery.
- Windows supplies the authenticated E2EE session catalog and process state.
  Android hides content when its host is offline or the catalog is unavailable,
  and its session page contains only locally opened tabs.
- Server stores a statistical/routing snapshot and retires omitted RUNNING rows.
- Transport policy now distinguishes loading, failure and unavailable support;
  WebSocket capability validation uses that connection's original HELLO.
- Upgrade order: Server, Windows, Android. Older peers fail closed; there is no
  fallback to a stale server catalog. No schema migration or history deletion.

## Verification

- Go realtime, realtimewire, API and store tests and vet passed.
- Windows Release solution build: zero warnings/errors. PC heartbeat,
  session fault isolation and Windows Setup validations passed.
- Android core-network Debug/Release and App Debug unit tests passed.
- Prior isolated PostgreSQL and emulator smoke results are recorded in
  HOST_SESSION_CATALOG.md. Authenticated product/offline recovery and physical
  LAN/NAT acceptance are not established by these unit tests.
- Signing preflight matches Android SHA-256
  f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b and Windows SHA-1
  91AE60BC06D866628B8EB2C62F32BDE9DAB56466 (valid until 2036-08-04).

## Publication

Production build, artifact verification and external publication are pending.
Exact source revision, hashes, publication IDs and image digest will be added
after those operations complete. Local Server/client upgrades are not implied
by publication.

## Recovery

Keep immutable artifacts, tags and AppShip state. Resume interrupted uploads
with identical inputs. Client rollback uses a newer same-signer build; never
uninstall or clear application data. Server rollback pins the previously
verified image digest; do not move an existing tag.
