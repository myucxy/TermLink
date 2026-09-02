#ifndef TERMLINK_DIRECT_FFI_V2_H
#define TERMLINK_DIRECT_FFI_V2_H

#include <stddef.h>
#include <stdint.h>

#define TERMLINK_DIRECT_ABI_VERSION_V2 2u
#define TERMLINK_DIRECT_MAX_STUN_SERVERS_V2 2u
#define TERMLINK_DIRECT_MAX_TICKET_BYTES_V2 2048u
#define TERMLINK_DIRECT_MAX_EVENT_PAYLOAD_BYTES_V2 2048u
#define TERMLINK_DIRECT_MAX_SIGNATURE_BYTES_V2 128u
#define TERMLINK_DIRECT_MAX_PROOF_NONCE_BYTES_V2 32u

#define TERMLINK_DIRECT_PLATFORM_WINDOWS_V2 1u
#define TERMLINK_DIRECT_PLATFORM_ANDROID_V2 2u
#define TERMLINK_DIRECT_ROLE_INITIATOR_V2 1u
#define TERMLINK_DIRECT_ROLE_RESPONDER_V2 2u

#define TERMLINK_DIRECT_EVENT_IDENTITY_READY_V2 1u
#define TERMLINK_DIRECT_EVENT_STATE_CHANGED_V2 2u
#define TERMLINK_DIRECT_EVENT_DIRECT_FAILED_V2 3u
#define TERMLINK_DIRECT_EVENT_SIGN_REQUEST_V2 4u
#define TERMLINK_DIRECT_EVENT_SHADOW_READY_V2 5u
#define TERMLINK_DIRECT_EVENT_IGNORED_STALE_V2 6u

#define TERMLINK_DIRECT_STATE_RELAY_ONLY_V2 1u
#define TERMLINK_DIRECT_STATE_GATHERING_V2 2u
#define TERMLINK_DIRECT_STATE_PROBING_V2 3u
#define TERMLINK_DIRECT_STATE_AUTHENTICATING_V2 4u
#define TERMLINK_DIRECT_STATE_SHADOW_DIRECT_V2 5u

#define TERMLINK_DIRECT_REASON_DISABLED_V2 1u
#define TERMLINK_DIRECT_REASON_INVALID_ABI_V2 2u
#define TERMLINK_DIRECT_REASON_INVALID_EPOCH_V2 3u
#define TERMLINK_DIRECT_REASON_INVALID_PLATFORM_V2 4u
#define TERMLINK_DIRECT_REASON_INVALID_CANDIDATE_V2 5u
#define TERMLINK_DIRECT_REASON_INVALID_TICKET_V2 6u
#define TERMLINK_DIRECT_REASON_SOCKET_V2 7u
#define TERMLINK_DIRECT_REASON_PROBE_V2 8u
#define TERMLINK_DIRECT_REASON_QUIC_V2 9u
#define TERMLINK_DIRECT_REASON_PEER_PROOF_V2 10u
#define TERMLINK_DIRECT_REASON_SIGNATURE_TIMEOUT_V2 11u
#define TERMLINK_DIRECT_REASON_CANCELLED_V2 12u
#define TERMLINK_DIRECT_REASON_NETWORK_CHANGED_V2 13u
#define TERMLINK_DIRECT_REASON_QUEUE_FULL_V2 14u
#define TERMLINK_DIRECT_REASON_INTERNAL_V2 15u

typedef struct TermLinkDirectCreateV2 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint32_t enabled;
  uint32_t queue_capacity;
  uint32_t platform;
  uint32_t reserved[3];
} TermLinkDirectCreateV2;

typedef struct TermLinkSocketAddrV2 {
  uint8_t ip[16];
  uint32_t ip_length;
  uint32_t port;
} TermLinkSocketAddrV2;

typedef struct TermLinkDirectStartV2 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint64_t epoch;
  uint8_t attempt_id[16];
  uint8_t connection_id[16];
  uint8_t local_device_id[16];
  uint8_t peer_device_id[16];
  uint32_t role;
  uint32_t timeout_ms;
  uint64_t network_handle;
  TermLinkSocketAddrV2 local_bind;
  TermLinkSocketAddrV2 remote_candidate;
  uint32_t stun_count;
  uint32_t reserved0;
  TermLinkSocketAddrV2 stun_servers[TERMLINK_DIRECT_MAX_STUN_SERVERS_V2];
  uint32_t ticket_length;
  uint32_t reserved1;
  uint8_t ticket[TERMLINK_DIRECT_MAX_TICKET_BYTES_V2];
  uint8_t server_signing_public_key[32];
  uint8_t peer_certificate_sha256[32];
  uint8_t peer_device_signing_public_key[32];
  uint8_t probe_hmac_key[32];
  uint8_t ticket_id[16];
  uint8_t nomination_id[16];
  uint8_t initiator_candidate_id[16];
  uint8_t responder_candidate_id[16];
  uint8_t local_probe_nonce[16];
  uint8_t peer_probe_nonce[16];
  uint32_t local_proof_nonce_length;
  uint32_t peer_proof_nonce_length;
  uint8_t local_proof_nonce[TERMLINK_DIRECT_MAX_PROOF_NONCE_BYTES_V2];
  uint8_t peer_proof_nonce[TERMLINK_DIRECT_MAX_PROOF_NONCE_BYTES_V2];
  int64_t now_ms;
  uint64_t reserved[4];
} TermLinkDirectStartV2;

typedef struct TermLinkDirectControlV2 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint64_t epoch;
  uint8_t attempt_id[16];
} TermLinkDirectControlV2;

typedef struct TermLinkDirectSignatureV2 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint64_t epoch;
  uint8_t attempt_id[16];
  uint64_t request_id;
  uint32_t signature_length;
  uint32_t reserved;
  uint8_t signature[TERMLINK_DIRECT_MAX_SIGNATURE_BYTES_V2];
} TermLinkDirectSignatureV2;

typedef struct TermLinkDirectEventV2 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint32_t kind;
  uint32_t state;
  uint32_t reason;
  uint32_t payload_length;
  uint64_t epoch;
  uint64_t generation;
  uint64_t request_id;
  uint8_t attempt_id[16];
  TermLinkSocketAddrV2 endpoint;
  uint8_t payload[TERMLINK_DIRECT_MAX_EVENT_PAYLOAD_BYTES_V2];
} TermLinkDirectEventV2;

#if defined(__cplusplus)
#define TERMLINK_DIRECT_STATIC_ASSERT(condition, message) static_assert(condition, message)
extern "C" {
#else
#define TERMLINK_DIRECT_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

uint32_t termlink_direct_abi_version_v2(void);
/*
 * Frozen legacy discovery surface. create_v2 always returns NULL because v2
 * has only one probe key and cannot authenticate both wire directions safely.
 * New callers must require ABI v3; no production fallback to v2 is allowed.
 */
void *termlink_direct_create_v2(const TermLinkDirectCreateV2 *config);
int32_t termlink_direct_start_v2(void *handle, const TermLinkDirectStartV2 *start);
int32_t termlink_direct_cancel_v2(void *handle, const TermLinkDirectControlV2 *control);
int32_t termlink_direct_network_changed_v2(void *handle,
                                           const TermLinkDirectControlV2 *control);
int32_t termlink_direct_submit_signature_v2(
    void *handle, const TermLinkDirectSignatureV2 *signature);
int32_t termlink_direct_poll_event_v2(void *handle, TermLinkDirectEventV2 *output);
void termlink_direct_destroy_v2(void *handle);

#if defined(__cplusplus)
} /* extern "C" */
#endif

TERMLINK_DIRECT_STATIC_ASSERT(sizeof(TermLinkDirectCreateV2) == 32,
                              "TermLinkDirectCreateV2 ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(sizeof(TermLinkSocketAddrV2) == 24,
                              "TermLinkSocketAddrV2 ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(sizeof(TermLinkDirectStartV2) == 2592,
                              "TermLinkDirectStartV2 ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(sizeof(TermLinkDirectControlV2) == 32,
                              "TermLinkDirectControlV2 ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(sizeof(TermLinkDirectSignatureV2) == 176,
                              "TermLinkDirectSignatureV2 ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(sizeof(TermLinkDirectEventV2) == 2136,
                              "TermLinkDirectEventV2 ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(offsetof(TermLinkDirectStartV2, ticket) == 208,
                              "TermLinkDirectStartV2.ticket ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(
    offsetof(TermLinkDirectStartV2, peer_certificate_sha256) == 2288,
    "TermLinkDirectStartV2.peer_certificate_sha256 ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(offsetof(TermLinkDirectStartV2, now_ms) == 2552,
                              "TermLinkDirectStartV2.now_ms ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(offsetof(TermLinkDirectEventV2, generation) == 32,
                              "TermLinkDirectEventV2.generation ABI drift");
TERMLINK_DIRECT_STATIC_ASSERT(offsetof(TermLinkDirectEventV2, payload) == 88,
                              "TermLinkDirectEventV2.payload ABI drift");

#undef TERMLINK_DIRECT_STATIC_ASSERT
#endif /* TERMLINK_DIRECT_FFI_V2_H */
