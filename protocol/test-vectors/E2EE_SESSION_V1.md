# E2EE Session V1

This vector fixes the TermLink device-to-device key schedule independently of
the Relay. The Relay must not have either private key or any derived key.

1. X25519 derives the pairwise shared material.
2. HKDF-SHA256 uses `SHA-256("TERMLINK-E2EE-V1")` as salt. Pairwise info is
   `"TERMLINK-PAIRWISE-V1" || 0x00 || pc_device_uuid || android_device_uuid ||
   key_epoch_be64`.
3. Session HKDF uses the same salt and info
   `"TERMLINK-SESSION-KEY-V1" || 0x00 || session_uuid || direction_u8 || key_id`.
4. ChaCha20-Poly1305 uses a 12-byte nonce formed from a random four-byte epoch
   prefix plus the direction sequence as big-endian uint64.
5. AEAD associated data is the deterministic protobuf encoding of every
   Envelope metadata field. Field 11 (`ciphertext`) is omitted; optional field
   12 (`recipient_device_id`) is included when present.

UUID bytes use RFC 4122/network order. Output and input always use independently
derived direction keys. The JSON vector is verified by Go, .NET, and Android.
