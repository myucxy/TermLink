# Bounded History Object V1

`HistoryCiphertextV1` is the stable client-to-client format for cloud history.
The server stores the serialized object as opaque bytes and verifies only the
declared byte length and SHA-256 digest.

1. A Windows PC maps each committed ordered frame to `HistoryEventV1` using
   the same `MessageType` and encrypted-payload Protobuf message as realtime.
2. Events in one `HistoryChunkV1` are non-empty, strictly contiguous, and
   bounded by the authenticated `start_seq` and `end_seq`.
3. The PC and target Android derive the pairwise X25519 key defined by
   `E2EE_SESSION_V1.md`. A history key is HKDF-SHA256 with the same salt and
   info `"TERMLINK-HISTORY-KEY-V1" || 0x00 || session_uuid ||
   recipient_device_uuid || key_epoch_be64 || key_id_utf8`.
4. `key_id` is `history-v1:<recipient UUID without dashes>:<epoch padded to
   eight digits>`. It lets a client reject objects encrypted for another
   device before download or decryption.
5. ChaCha20-Poly1305 uses a fresh random 12-byte nonce for every object.
6. Associated data is the byte concatenation of
   `"TERMLINK-HISTORY-OBJECT-V1" || 0x00`, the session/PC/recipient UUIDs in
   RFC 4122 order, key epoch as signed BE64, key-id length as BE16, key-id
   UTF-8, start/end as BE64, and the 12-byte nonce.
7. Android cross-checks the authenticated container against server metadata,
   verifies the downloaded object SHA-256, decrypts only in memory, and feeds
   output/resize events into a read-only terminal engine.

The deterministic vector is in `bounded-history-v1.json` and is verified by
the .NET protocol validator and Android `core-crypto` tests.
