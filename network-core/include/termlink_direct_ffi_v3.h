#ifndef TERMLINK_DIRECT_FFI_V3_H
#define TERMLINK_DIRECT_FFI_V3_H

#include "termlink_direct_ffi_v2.h"

#define TERMLINK_DIRECT_ABI_VERSION_V3 3u
#define TERMLINK_DIRECT_MAX_CANDIDATES_V3 3u
#define TERMLINK_DIRECT_EVENT_CANDIDATES_READY_V3 7u

/*
 * ABI V3 is a shadow-only two-stage workflow. Gather owns one UDP socket and
 * advertises candidates from it; Nominate consumes that exact retained socket
 * for probe, QUIC, peer proof, and shadow ping. No record exposes Relay/WSS,
 * Envelope, durable ACK, renderer, terminal session, or routing handles.
 *
 * TermLinkDirectControlV2, TermLinkDirectSignatureV2, TermLinkDirectEventV2,
 * TermLinkDirectStartV2, and TermLinkSocketAddrV2 retain their exact V2 memory
 * layout when used by V3. Their abi_version field is V3 except the embedded
 * TermLinkDirectStartV2 in Nominate, whose header remains V2.
 */

typedef struct TermLinkDirectCreateV3 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint32_t enabled;
  uint32_t queue_capacity;
  uint32_t platform;
  uint32_t reserved[3];
} TermLinkDirectCreateV3;

typedef struct TermLinkDirectGatherV3 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint64_t epoch;
  uint8_t attempt_id[16];
  uint64_t network_handle;
  TermLinkSocketAddrV2 local_bind;
  uint8_t advertised_local_ip[16];
  uint32_t advertised_local_ip_length;
  uint32_t stun_count;
  TermLinkSocketAddrV2 stun_servers[TERMLINK_DIRECT_MAX_STUN_SERVERS_V2];
  uint32_t gather_timeout_ms;
  uint32_t reserved0;
  uint64_t reserved[4];
} TermLinkDirectGatherV3;

typedef struct TermLinkDirectNominateV3 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectStartV2 start_v2;
  /*
   * Direction-specific HKDF outputs for authenticated UDP probes. The local
   * key authenticates every datagram sent by this device; the peer key
   * authenticates every datagram received from the nominated peer.
   */
  uint8_t local_send_probe_hmac_key[32];
  uint8_t peer_receive_probe_hmac_key[32];
  uint64_t reserved[4];
} TermLinkDirectNominateV3;

typedef struct TermLinkDirectCandidatesV3 {
  uint32_t candidate_count;
  uint32_t reserved;
  /* candidates[0] is HOST; any remaining entries are unique SRFLX values. */
  TermLinkSocketAddrV2 candidates[TERMLINK_DIRECT_MAX_CANDIDATES_V3];
} TermLinkDirectCandidatesV3;

#if defined(__cplusplus)
#define TERMLINK_DIRECT_V3_STATIC_ASSERT(condition, message) static_assert(condition, message)
extern "C" {
#else
#define TERMLINK_DIRECT_V3_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

uint32_t termlink_direct_abi_version_v3(void);
void *termlink_direct_create_v3(const TermLinkDirectCreateV3 *config);
int32_t termlink_direct_gather_v3(void *handle, const TermLinkDirectGatherV3 *input);
int32_t termlink_direct_nominate_v3(void *handle,
                                   const TermLinkDirectNominateV3 *input);
int32_t termlink_direct_cancel_v3(void *handle,
                                 const TermLinkDirectControlV2 *control);
int32_t termlink_direct_network_changed_v3(
    void *handle, const TermLinkDirectControlV2 *control);
int32_t termlink_direct_submit_signature_v3(
    void *handle, const TermLinkDirectSignatureV2 *signature);
int32_t termlink_direct_poll_event_v3(void *handle, TermLinkDirectEventV2 *output);
void termlink_direct_destroy_v3(void *handle);

#if defined(__cplusplus)
} /* extern "C" */
#endif

TERMLINK_DIRECT_V3_STATIC_ASSERT(sizeof(TermLinkDirectCreateV3) == 32,
                                 "TermLinkDirectCreateV3 ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(sizeof(TermLinkDirectGatherV3) == 176,
                                 "TermLinkDirectGatherV3 ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(sizeof(TermLinkDirectNominateV3) == 2696,
                                 "TermLinkDirectNominateV3 ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(sizeof(TermLinkDirectCandidatesV3) == 80,
                                 "TermLinkDirectCandidatesV3 ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(offsetof(TermLinkDirectGatherV3, local_bind) == 40,
                                 "TermLinkDirectGatherV3.local_bind ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(offsetof(TermLinkDirectGatherV3, stun_servers) == 88,
                                 "TermLinkDirectGatherV3.stun_servers ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(offsetof(TermLinkDirectGatherV3, reserved) == 144,
                                 "TermLinkDirectGatherV3.reserved ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(offsetof(TermLinkDirectNominateV3, start_v2) == 8,
                                 "TermLinkDirectNominateV3.start_v2 ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(
    offsetof(TermLinkDirectNominateV3, local_send_probe_hmac_key) == 2600,
    "TermLinkDirectNominateV3.local_send_probe_hmac_key ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(
    offsetof(TermLinkDirectNominateV3, peer_receive_probe_hmac_key) == 2632,
    "TermLinkDirectNominateV3.peer_receive_probe_hmac_key ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(offsetof(TermLinkDirectNominateV3, reserved) == 2664,
                                 "TermLinkDirectNominateV3.reserved ABI drift");
TERMLINK_DIRECT_V3_STATIC_ASSERT(offsetof(TermLinkDirectCandidatesV3, candidates) == 8,
                                 "TermLinkDirectCandidatesV3.candidates ABI drift");

#undef TERMLINK_DIRECT_V3_STATIC_ASSERT
#endif /* TERMLINK_DIRECT_FFI_V3_H */
