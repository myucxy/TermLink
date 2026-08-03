# TermLink Production Realtime V1

This contract is separate from the `TLT1` test protocol. `TLT1` frames are
never accepted by `/v1/realtime`.

## Connection handshake

The WebSocket upgrade uses an OIDC bearer token and these headers:

- `X-TermLink-Device`: canonical UUID of the registered device;
- `X-TermLink-Timestamp`: Unix milliseconds;
- `X-TermLink-Nonce`: base64url 16-32 random bytes;
- `X-TermLink-Signature`: base64url Ed25519 signature;
- `Sec-WebSocket-Protocol`: `termlink.realtime.v1`.

The signed bytes are UTF-8 and use exactly:

```text
TERMLINK-DEVICE-PROOF-V1\n
GET\n
/v1/realtime\n
<timestamp>\n
<nonce>\n
<lowercase SHA-256 of empty body>
```

The server checks a 60 second clock window and consumes the nonce once in
Valkey. The first client message is a bounded JSON `ClientHello` control
record containing `role`, `protocol_versions`, and `capabilities`. It contains
no terminal content. The server answers with `ServerWelcome` and then accepts
only binary protobuf `Envelope` messages.

## Envelope and E2EE

`termlink_realtime.proto` is the authoritative wire schema. Version 1 uses
UUIDs as 16 raw bytes. A binary frame is one serialized `Envelope` and is at
most 1 MiB. Unknown protobuf fields are ignored; an unknown message type is
rejected unless a later negotiated capability explicitly marks it optional.

For encrypted application messages, `nonce` is 12 bytes and `ciphertext` is
ChaCha20-Poly1305 output including the authentication tag. `key_id` identifies
an X25519/HKDF key epoch without revealing key material. Associated data is a
deterministic protobuf encoding of every Envelope field except field 11
(`ciphertext`). Optional field 12 (`recipient_device_id`) is included when set.
The two directions use distinct HKDF output keys. `(key_id, direction, seq)`
must never repeat.

The Relay validates routing fields, authorization, direction, monotonically
increasing sequence numbers, and size. It never decrypts field 11.

Pairwise device-channel messages set `recipient_device_id` and are delivered
only to that approved device. Empty-recipient messages are limited to message
types whose semantics explicitly allow account/session broadcast. Session
output in V1 is recipient-specific; Relay never maps Key IDs to private keys.

## Capabilities

Version 1 requires `e2ee_x25519_chacha20poly1305`, `cumulative_ack`, and
`lease_v1`. `bounded_event_replay_v1` is supported for recovery. The proposed
`terminal_snapshot_v1` capability remains experimental until Termux/xterm
cross-engine compatibility validation is accepted; it is not a stable format.

## Input authority

An input payload contains a unique `input_id`, the active `lease_id`, lease
version, and input sequence. The PC Agent validates the lease again immediately
before writing to ConPTY and keeps a bounded duplicate-input cache. Local PC
input revokes the remote lease and always takes priority.

The ordered `LEASE_REVOKED` payload carries the new monotonic
`local_input_epoch`. Subsequent Android input echoes the latest observed epoch,
so a stale lease or in-flight input cannot cross a local-input boundary.
