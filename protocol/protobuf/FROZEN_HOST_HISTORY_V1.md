# Frozen Host History wire extension

This document fixes the additive wire numbers for E02. The Windows capture
lease, page encryption, and Android browse path are now connected behind
explicit peer capabilities. Complete product acceptance still requires the
end-to-end checks; a Relay `host_history_v1` Welcome alone does not authorize a
frozen request. The existing Host History operations and their protobuf field
numbers remain unchanged.

| Wire item | Number | Meaning |
|---|---:|---|
| `HostHistoryRequestKindV1.FROZEN_BEGIN` | 4 | Create one immutable capture and return its first page |
| `HostHistoryRequestKindV1.FROZEN_PAGE` | 5 | Read another page of the same capture |
| `HostHistoryRequestKindV1.FROZEN_RELEASE` | 6 | Release the capture |
| `HostHistoryObjectKindV1.FROZEN_PAGE` | 4 | Encrypted TLHPV1 or TLHPV2 page |
| `HostHistoryRequestPayload.frozen_history` | 9 | `FrozenHistoryRequest` |
| `HostHistoryResponsePayload.frozen_history` | 26 | `FrozenHistoryMetadata` |

`FrozenHistoryRequest` has field numbers 1–7: `token` (bytes), `capture_id`
(uint64), `capture_revision` (uint64), `max_lines` (uint32), `max_cells`
(uint32), `max_utf8_bytes` (uint32), `page_format_version` (uint32).
`FrozenHistoryMetadata` has field numbers 1–8: `token` (bytes), `capture_id`
(uint64), `capture_revision` (uint64), `lease_remaining_ms` (uint64),
`state_contract` (string), `head_truncated` (bool), `required_cells` (uint64),
`required_utf8_bytes` (uint64).

`FrozenHistoryCiphertextV1` is a separate AEAD container, not a Snapshot or
Transcript object. Its field numbers 1–22 are: `container_version`,
`session_id`, `pc_device_id`, `recipient_device_id`, `key_epoch`, `key_id`,
`token`, `capture_id`, `capture_revision`, `snapshot_seq`,
`request_before_line_id`, `max_lines`, `max_cells`, `max_utf8_bytes`,
`page_format_version`, `plaintext_bytes`, `nonce`, `ciphertext`,
`first_line_id`, `last_line_id`, `has_more_before`, `next_before_line_id`.
The three device/session IDs are 16 bytes, token is 32 bytes, nonce is 12
bytes, and ciphertext includes the AEAD tag. The container has its own
version and key derivation domain. Every selection field is authenticated
as AEAD AAD. The recipient verifies the outer response against the container,
then verifies the decrypted TLHPV1/TLHPV2 header and page anchor against both.
No part of the container changes the live terminal ACK or Snapshot semantics.

The authenticated Windows attachment fixes the session's persistent state
contract. Graphics `terminal_state_2_22_v1` uses the existing width-1 profile;
`terminal_state_2_22_v2` uses Unicode16 width-2. Both require `TLHPV2` page
format 2, `TERMLINK_FROZEN_HISTORY_PAGE_V2`, and the negotiated
`host_history_frozen_v1` plus `tlhp_v2` capabilities. The older
`terminal_state_2_21_v2` contract pairs only with `TLHPV1` page format 1 in
the crypto codec; the current Graphics product path requests format 2. The
outer response, metadata `state_contract`, sealed object, and decrypted page
must agree with the accepted attachment. Neither a page nor its metadata may
select a new width profile for a running session.

`FrozenHistoryCiphertextV1.container_version` remains 1 for both Graphics
state contracts. The exact state contract selects separate AEAD domains:

| State contract | Key ID prefix | HKDF info prefix | AAD prefix |
| --- | --- | --- | --- |
| `terminal_state_2_22_v1` | `frozen-history-v1:` | `TERMLINK-FROZEN-HISTORY-KEY-V1\0` | `TERMLINK-FROZEN-HISTORY-OBJECT-V1\0` |
| `terminal_state_2_22_v2` | `frozen-history-v2:` | `TERMLINK-FROZEN-HISTORY-KEY-V2\0` | `TERMLINK-FROZEN-HISTORY-OBJECT-V2\0` |

The Key ID suffix is `{recipient UUID as 32 lowercase hex digits}:{key epoch
as at least 8 decimal digits, zero padded}`. HKDF-SHA256 uses the pairwise key,
session UUID salt, and the selected prefix followed by recipient UUID,
big-endian epoch, length-prefixed Key ID. AAD includes its selected prefix and
the exact container metadata, including the state contract. The v1 domain and
existing vectors are unchanged; cross-profile decryption or a Key ID from the
other domain is rejected without trying a fallback key.

The host creates a cryptographically random, opaque 32-byte token on BEGIN.
BEGIN sends an empty token and no capture ID/revision; PAGE and RELEASE must
echo the token, capture ID, and revision. Token, capture, session, recipient,
key epoch, state contract, and source snapshot sequence S must match the host
lease. A native capture ID is an identity check, never an authorization secret.
The lease is not renewed by reading pages. `lease_remaining_ms` is advisory;
the host's monotonic lease deadline decides validity.

`HostHistoryRequestPayload.snapshot_seq` selects exact S. The returned
`capture_revision` is S+1. `before_line_id` is the page anchor, with
`18446744073709551615` (uint64 maximum) for the first page. Line IDs are
identities rather than sortable offsets. The response repeats S in
`snapshot_seq` and the requested anchor in `request_before_line_id`.
`has_more_before` determines completion; zero `next_before_line_id` has no
special end-of-history meaning for frozen pages.

BEGIN and PAGE require explicit budgets: `max_lines` 1–128, `max_cells`
1–65536, `max_utf8_bytes` 1–4194304, and a negotiated page format version.
Graphics requires TLHPV2. The host checks object format, encrypted object
size, chunk count, and every request bound before allocation or sending.
`PAGE_BUDGET` returns required cell and UTF-8 counts without truncating a
logical line or omitting image resources. The response repeats identical
capture metadata on every chunk. A successful BEGIN/PAGE returns a frozen page
object, including a properly encoded empty page. Only an explicit
`FROZEN_RELEASE` may return an empty OK response; legacy empty-response rules
remain unchanged.

Error codes are `STALE_CAPTURE`, `STALE_REVISION`, `STALE_ANCHOR`,
`PAGE_BUDGET`, `CAPTURE_LIMIT`, `UNSUPPORTED_FORMAT`, and `ANCHOR_CHANGED`.
Errors are not interpreted as end-of-history. The current Graphics path
requires `host_history_frozen_v1` and `tlhp_v2` from both peers and an exact
profile match before BEGIN/PAGE/RELEASE. A client without these capabilities
continues through existing snapshot/transcript history. The page object uses
the separate AEAD derivation and authenticated context above; protobuf fields
alone do not authenticate it.
