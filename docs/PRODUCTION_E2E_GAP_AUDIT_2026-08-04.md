# TermLink Production E2E Gap Audit

Date: 2026-08-04
Scope: production Server, Win-Agent, and Android path only

This audit deliberately excludes `LAN Replay V1`, `TLT1`, UI prototypes, and
module-only tests as end-to-end completion evidence.

## Baseline

- The Git worktree was clean before this audit.
- `go test ./...`, `go vet ./...`, the .NET production protocol validation,
  the .NET production runtime lifecycle validation, Android `test`, and Android
  `lintDebug` pass in the current workspace.
- `https://termlink.kget.cc:8910/readyz` and `/v1/client-config` currently return
  HTTP 502.
- `http://192.168.9.28:8080/readyz` currently refuses the connection.
- No TermLink Server, PostgreSQL, Valkey, object store, or OIDC container is
  currently running on this workstation.

Passing module tests therefore does not currently produce a reachable system.

## Gap Table

| Area | Present implementation | Production connection gap | Required completion evidence |
| --- | --- | --- | --- |
| Server deployment | Hardened Go image, migrations, PostgreSQL, Valkey, optional MinIO, Caddy, health and deployment policy checks | The public origin is 502 and the Lucky upstream is down. The disposable integration stack starts dependencies only, not an OIDC issuer plus the production server and TLS edge as one repeatable acceptance stack. | One command starts OIDC, migrations, API, Relay and TLS; OIDC discovery, `/readyz`, `/v1/client-config`, device registration and a signed Realtime handshake pass across containers. |
| Identity | Server validates signed OIDC JWTs and Android implements Authorization Code + PKCE | No OIDC service, realm/client bootstrap, first administrator workflow, or repeatable local identity deployment exists in this repository. `TERMLINK_OIDC_CLIENT_SECRET` is loaded but unused. | A real issuer is bootstrapped without committed secrets; Android completes PKCE and the server accepts a real issued token. |
| Win-Agent account link | DPAPI protects a device signing key, exchange key and Access Token | No product command or Tray flow creates `production-config.json` or credentials. Only tests call `ProductionCredentials.Save`. The stored short-lived Access Token has no refresh-token or expiry handling. | Browser/device authorization links the Agent, refresh survives token expiry, and no token is printed or stored outside DPAPI. |
| Win-Agent session publication | `SessionRegistry` committed events are replayed from the DPAPI buffer and encrypted per Android device | The production runtime has only module/lifecycle validation against an unreachable URL. It has not connected a real Registry/ConPTY to a real Relay and Android. | A real PowerShell Session Host is listed through REST and its encrypted output reaches Android through `/v1/realtime`. |
| Remote input | Android encrypts `INPUT_EVENT`; Relay checks Grant and Lease; Agent checks the lease again and SessionAgent persists `input_id` deduplication | No real three-process execution evidence exists. Input failures before a committed Host result can time out without a structured rejection. | English, CJK, Enter, Tab, arrows and Ctrl+C execute once in real ConPTY and return ordered `INPUT_ACCEPTED`/`INPUT_REJECTED`. |
| Resize | Android emits leased `RESIZE_REQUEST`; protocol and Relay route it | Win-Agent ignores `RESIZE_REQUEST`. Android `RemoteTerminalSession` attempts resize in Live/ReadOnly states although the production outbound requires a control lease. Product recomposition also reapplies server dimensions and emits resize. | A controlling Android viewport changes the real ConPTY once after debounce; read-only display never forces reconnect or resize oscillation. |
| Local preemption | Session Host emits `LocalInputObserved`; Agent force-releases the server lease and publishes ordered `LEASE_REVOKED` with `local_input_epoch` | Only local/runtime tests exist. No real race between in-flight Android input and local Windows input has been exercised through Relay. | A local key immediately revokes the lease; stale/in-flight input with the old epoch is rejected and never reaches ConPTY. |
| ACK and reconnect | Android uses cumulative ACK in memory; Agent replays its durable buffer and emits Gap when retention is lost | Android has an `ack_cursors` table but the production live client neither loads nor persists it. App/process restart resumes at zero. No real Relay restart or phone sleep recovery evidence exists. | Socket and app-process reconnect resume from durable ACK, feed no duplicate output, and execute no duplicate input. |
| Backpressure and Gap | Relay has bounded 128-frame peer queues and closes slow consumers; Agent has bounded DPAPI replay and an explicit encrypted Gap | There is no production `FLOW_CONTROL` exchange and no real slow Android/ConPTY test. Android cannot recover from History Gap because a stable snapshot is intentionally not frozen. | Slow-consumer close and resume are measured; retained suffix restores correctly, and unrecoverable data produces one explicit user-visible Gap without corrupting VT state. |
| Android product UI | Launcher is the product Compose activity; real REST/OIDC/Realtime repositories and Termux engine are wired | It still uses application ID `dev.termlink.harness`; live ACK is not persisted; no production Extra Keys control exposes Tab/arrows/Ctrl+C; only generic Paste is explicit. | Emulator/phone product page signs in, pairs, lists the real PC/session, watches output, controls all required keys, and reconnects. |
| Lifecycle | Host/Agent local lifecycle and Android session state machines are implemented | Production exit/interruption has not crossed the real Relay. Remote shell termination is intentionally not exposed. | Real Shell exit drains output exactly once, updates REST metadata, reaches Android as Exited, and disables input. |
| E2EE and secrecy | X25519/HKDF/ChaCha20-Poly1305 cross-platform vectors pass; Relay routes opaque Protobuf Envelopes | Key epoch distribution is static configuration and there is no real revocation/rotation cycle. Logs/DB/Valkey/S3 have not been scanned after a real terminal run. | Packet/log/storage inspection finds no terminal plaintext, prompt, token or secret; revoked devices cannot decrypt a new epoch. |
| Public TLS | Caddy production policy and TLS settings exist | The configured public origin currently terminates at Lucky with 502. Real certificate chain, WebSocket upgrade and OIDC callbacks are unavailable until the upstream stack is running. | Public `/readyz`, OIDC callback and `wss://.../v1/realtime` pass with the real certificate chain. |

## Critical Path

1. Add and run a disposable but production-protocol OIDC/TLS/Server acceptance
   deployment. It must use generated local secrets and real signed OIDC tokens.
2. Add a supported Win-Agent account-link and token-refresh path, then connect
   the Registry to that deployment.
3. Fix production resize and durable Android ACK wiring before claiming
   disconnect recovery.
4. Run Android product UI against the same deployment and exercise real
   ConPTY input/output.
5. Only after the local production-protocol closure passes, point Lucky at the
   validated upstream and repeat the acceptance matrix on the public origin.
