# ADR 0002: Stable Windows machine correlation

- Status: Accepted
- Date: 2026-09-04

## Context

The Windows Agent historically generated a new `device_id` and `pc_id` when
its local account files were removed. Reinstalling TermLink or signing out and
back in could therefore add another PC entry for the same Windows machine.
Using one hardware-derived value directly as an authentication credential
would make cloning and cross-account tracking possible, and would weaken the
existing per-device E2EE and revocation boundaries.

## Decision

Windows identity has two separate layers:

- `machine_id` is a non-secret, account-scoped correlation value. It may only
  locate an existing PC row owned by the bearer-token account.
- `device_id` and its signing/exchange key pairs remain the authentication,
  E2EE, trust and revocation identity.

The Agent derives `machine_id` as SHA-256 over a versioned domain, the
canonical Windows `MachineGuid`, the canonical OIDC issuer, and the stable
issuer/subject account binding. Only the 32-byte digest is sent to the Server.
The raw `MachineGuid` is neither uploaded nor persisted by TermLink.

The Server stores the digest in `pcs.machine_id_hash` and enforces at most one
non-null value per owner. An account-authenticated resolve request can return
the matching `pc_id`, display name and key epoch, but cannot authorize a PC
mutation. The subsequent PC upsert still requires a newly registered, trusted
Windows device and its normal signed device authentication.

If the local DPAPI credentials are available, the Agent reuses the existing
device key and PC ID. Normal sign-out removes OAuth tokens and the active
account configuration but retains a token-free, DPAPI-protected identity
record per OIDC account. If that record is unavailable, the Agent resolves the
PC by `machine_id`, creates a fresh device key pair, and atomically rebinds the
existing PC row. Rebinding revokes the previous device, emits the revocation
outbox event, transfers any bootstrap-approval references, advances the PC key
epoch, writes an audit event, and preserves the PC row, sessions, encrypted
history records and control policy. Preserving an encrypted record does not
make it decryptable after its private device key has been destroyed: normal
sign-out/reinstall keeps that key in the DPAPI identity store, while deliberate
removal of the Windows profile or TermLink identity store is a cryptographic
key-loss boundary. A still-recoverable session can publish new history for the
replacement key epoch; older ciphertext that exists only under the destroyed
key remains unreadable by design.

An existing authenticated Agent that does not send `machine_id` may continue
updating its already-bound PC. It cannot replace another device binding or
clear a stored machine identity.

## Security and lifecycle boundaries

- Possession or knowledge of `machine_id` is never sufficient to authenticate,
  decrypt data, obtain a control lease or replace a device key.
- Resolution is restricted to the OIDC account in the validated bearer token
  and to approved Windows registration client IDs.
- Machine correlation is deliberately different across OIDC accounts and
  issuers, preventing a Server from using it as a global cross-account hardware
  identifier.
- `machine_id` continuity does not override E2EE key-loss semantics and the
  Server never receives enough private material to decrypt or rewrap history.
- The value is stable across TermLink uninstall/reinstall while Windows keeps
  the same `MachineGuid`. Reinstalling or cloning Windows may change or copy
  `MachineGuid`; TermLink does not claim a tamper-proof physical-hardware ID.
- Existing duplicate PC rows are not deleted or merged automatically because
  doing so could destroy sessions, encrypted history or user-selected policy.
  The first upgraded active row becomes the stable match; old rows require an
  explicit later cleanup decision.

## Verification

- Deterministic derivation, canonicalization and OIDC-account separation tests.
- DPAPI retained-identity round trip and sign-out preservation tests.
- PostgreSQL migration and rollback contract tests.
- PostgreSQL integration coverage for resolve, legacy same-device updates,
  key rotation, stale-replacement rejection, old-device revocation, bootstrap
  transfer, outbox and audit.
- Android Room coverage for an atomic `pc_id` binding transition to a new
  device/key at a strictly newer epoch, while retaining the revoked public key
  as historical metadata and rejecting stale catalog rollback.
- Full Server tests and Windows Release solution build.

## Termux reference boundary

The Android review checked `termux/termux-app` (current upstream HEAD
`3b66f8799635a4dba4a206563048ff0e6792c487` on 2026-09-04) and TermLink's
pinned import `3df69d1da197dd9bd71a3bafd902dffd720576b4`. Searches for database/session
identity migration and encryption-key rotation found no corresponding Termux
issue or implementation. Termux owns a local PTY/session lifecycle and does not
have TermLink's remote Windows device ID, account-scoped machine correlation,
E2EE recipient epoch, ACK or History Gap state machine. Its terminal emulator
remains a rendering-behavior reference only; no Termux code was copied for this
change.
