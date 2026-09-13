#ifndef TERMLINK_DIRECT_FFI_V5_H
#define TERMLINK_DIRECT_FFI_V5_H

/*
 * TermLink Direct transport ABI V5.
 *
 * This is a new, independently versioned data-plane boundary.  It does not
 * include the V4 header and does not extend any V4 record with reserved
 * storage. The native side copies every accepted Envelope before returning,
 * owns Direct writer/reader/liveness, and exposes Relay/control actions.
 *
 * V5 has no Relay, realtime-session, renderer, durable-ACK, Agent, or
 * SessionHost handle. A transport receipt only releases a native Direct
 * retransmission copy. Production promotes an uncommitted V4 candidate/check
 * attempt; ownership of its nominated UDP socket moves exactly once into V5.
 *
 * A handle returned by create is opaque. Calls may use the handle only while
 * it is live. The caller MUST externally synchronize destroy with every other
 * operation on that handle; destroy may not run concurrently with any call.
 * Promotion additionally requires both the V4 and V5 handles to remain live
 * until termlink_direct_promote_v5 returns, and must be externally serialized
 * with mutating/control operations on the nominated V4 attempt.
 */

#include <stddef.h>
#include <stdint.h>

#if defined(_MSC_VER)
#define TERMLINK_DIRECT_V5_ALIGN8 __declspec(align(8))
#elif defined(__GNUC__) || defined(__clang__)
#define TERMLINK_DIRECT_V5_ALIGN8 __attribute__((aligned(8)))
#else
#error "TermLink Direct V5 requires an explicit 8-byte alignment declaration"
#endif

#define TERMLINK_DIRECT_ABI_VERSION_V5 5u

/* These are hard ordinary queue bounds, not tunable deployment defaults. */
#define TERMLINK_DIRECT_MAX_FRAMES_V5 256u
#define TERMLINK_DIRECT_MAX_BYTES_V5 (8u * 1024u * 1024u)
/*
 * Exact fallback/control metadata may use this fixed emergency tail while
 * replacing already-owned route entries. Total retained frames therefore
 * never exceed MAX_FRAMES + FAILURE_ACTION_TAIL; bytes never exceed MAX_BYTES.
 */
#define TERMLINK_DIRECT_FAILURE_ACTION_TAIL_V5 4u
#define TERMLINK_DIRECT_MAX_RETAINED_FRAMES_V5 \
  (TERMLINK_DIRECT_MAX_FRAMES_V5 + TERMLINK_DIRECT_FAILURE_ACTION_TAIL_V5)
/* A realtime Envelope must be strictly smaller than 1 MiB. */
#define TERMLINK_DIRECT_MAX_ENVELOPE_BYTES_V5 ((1024u * 1024u) - 1u)
#define TERMLINK_DIRECT_FIRST_DATA_FRAME_SEQ_V5 UINT64_C(4)
#define TERMLINK_DIRECT_MAX_FRAME_SEQ_V5 UINT64_C(9223372036854775807)
#define TERMLINK_DIRECT_MAX_APPLIED_CURSORS_PER_DIRECTION_V5 256u

/* Return values. Poll-event returns 1/0; two-phase reads use NO_DATA. */
#define TERMLINK_DIRECT_V5_OK 0
#define TERMLINK_DIRECT_V5_NO_DATA 1
#define TERMLINK_DIRECT_V5_ERR_INVALID_HANDLE (-1)
#define TERMLINK_DIRECT_V5_ERR_INVALID_ARGUMENT (-2)
#define TERMLINK_DIRECT_V5_ERR_QUEUE_FULL (-3)
#define TERMLINK_DIRECT_V5_ERR_STALE_PATH (-4)
#define TERMLINK_DIRECT_V5_ERR_BUFFER_TOO_SMALL (-5)
#define TERMLINK_DIRECT_V5_ERR_NOT_READY (-6)
#define TERMLINK_DIRECT_V5_ERR_CANCELLED (-7)
#define TERMLINK_DIRECT_V5_ERR_LIMIT (-8)
#define TERMLINK_DIRECT_V5_ERR_PROTOCOL (-9)
#define TERMLINK_DIRECT_V5_ERR_DISABLED (-10)
#define TERMLINK_DIRECT_V5_ERR_TIMEOUT (-11)
#define TERMLINK_DIRECT_V5_ERR_TICKET_EXPIRED (-12)

#define TERMLINK_DIRECT_V5_EVENT_PATH_USED 1u
#define TERMLINK_DIRECT_V5_EVENT_ENVELOPE_QUEUED 2u
#define TERMLINK_DIRECT_V5_EVENT_ENVELOPE_AVAILABLE 3u
#define TERMLINK_DIRECT_V5_EVENT_TRANSPORT_RECEIPT 4u
#define TERMLINK_DIRECT_V5_EVENT_CANCELLED 5u
#define TERMLINK_DIRECT_V5_EVENT_NETWORK_CHANGED 6u
#define TERMLINK_DIRECT_V5_EVENT_QUEUE_LIMIT 7u
#define TERMLINK_DIRECT_V5_EVENT_PATH_READY 8u
#define TERMLINK_DIRECT_V5_EVENT_DIRECT_FAILED 9u
#define TERMLINK_DIRECT_V5_EVENT_PING 10u
#define TERMLINK_DIRECT_V5_EVENT_PONG 11u
#define TERMLINK_DIRECT_V5_EVENT_SIGN_REQUEST 12u

#define TERMLINK_DIRECT_V5_EVENT_FLAG_OUTBOUND 1u
#define TERMLINK_DIRECT_V5_EVENT_FLAG_INBOUND 2u
#define TERMLINK_DIRECT_V5_EVENT_FLAG_TRANSPORT_COPY 4u
#define TERMLINK_DIRECT_V5_EVENT_FLAG_LAN 8u
#define TERMLINK_DIRECT_V5_EVENT_FLAG_NAT 16u

#define TERMLINK_DIRECT_V5_PATH_KIND_LAN 1u
#define TERMLINK_DIRECT_V5_PATH_KIND_NAT 2u

#define TERMLINK_DIRECT_V5_ROLE_INITIATOR 1u
#define TERMLINK_DIRECT_V5_ROLE_RESPONDER 2u
#define TERMLINK_DIRECT_V5_CANDIDATE_HOST 1u
#define TERMLINK_DIRECT_V5_CANDIDATE_SERVER_REFLEXIVE 2u
#define TERMLINK_DIRECT_V5_CANDIDATE_CARRIER_UDP_QUIC 1u

#define TERMLINK_DIRECT_V5_DIRECTION_PC_TO_ANDROID 1u
#define TERMLINK_DIRECT_V5_DIRECTION_ANDROID_TO_PC 2u

#define TERMLINK_DIRECT_V5_ROUTE_DIRECT_ENVELOPE 1u
#define TERMLINK_DIRECT_V5_ROUTE_RELAY_ENVELOPE 2u
#define TERMLINK_DIRECT_V5_ROUTE_ACCEPT 4u
#define TERMLINK_DIRECT_V5_ROUTE_COMMIT 5u
#define TERMLINK_DIRECT_V5_ROUTE_FALLBACK 6u
#define TERMLINK_DIRECT_V5_ROUTE_CARRIER 7u
#define TERMLINK_DIRECT_V5_ROUTE_INGRESS_DELIVER 8u
#define TERMLINK_DIRECT_V5_ROUTE_INGRESS_HOLD 9u
#define TERMLINK_DIRECT_V5_ROUTE_INGRESS_DROP 10u
#define TERMLINK_DIRECT_V5_ROUTE_SESSION_CARRIER 11u

/* Values only for TermLinkDirectIngressV5.source. */
#define TERMLINK_DIRECT_V5_INGRESS_RELAY 1u
#define TERMLINK_DIRECT_V5_INGRESS_DIRECT 2u

/*
 * Values for TermLinkDirectRouteActionV5.carrier. CARRIER applies to the whole
 * direction. SESSION_CARRIER applies only to its non-zero session_id. A
 * direction-level RELAY action invalidates every remembered per-session Direct
 * projection for that direction; consumers must not infer Direct from path
 * readiness alone.
 */
#define TERMLINK_DIRECT_V5_CARRIER_RELAY 1u
#define TERMLINK_DIRECT_V5_CARRIER_LAN_DIRECT 2u
#define TERMLINK_DIRECT_V5_CARRIER_NAT_DIRECT 3u

#define TERMLINK_DIRECT_V5_REASON_BUFFER_ENVELOPE_LIMIT 1u
#define TERMLINK_DIRECT_V5_REASON_BUFFER_BYTE_LIMIT 2u
#define TERMLINK_DIRECT_V5_REASON_DIRECT_FAILURE 3u
#define TERMLINK_DIRECT_V5_REASON_EPOCH_REPLACED 4u
#define TERMLINK_DIRECT_V5_REASON_NEGOTIATION_TIMEOUT 5u
#define TERMLINK_DIRECT_V5_REASON_PEER_REJECTED 6u
#define TERMLINK_DIRECT_V5_REASON_BARRIER_MISMATCH 7u
#define TERMLINK_DIRECT_V5_REASON_INVALID_ENVELOPE 8u
#define TERMLINK_DIRECT_V5_REASON_PROTOCOL_VIOLATION 9u
#define TERMLINK_DIRECT_V5_REASON_REJECT_INVALID_EPOCH 101u
#define TERMLINK_DIRECT_V5_REASON_REJECT_FUTURE_EPOCH 102u
#define TERMLINK_DIRECT_V5_REASON_REJECT_INVALID_PROPOSAL 103u
#define TERMLINK_DIRECT_V5_REASON_REJECT_INVALID_TIMEOUT 104u
#define TERMLINK_DIRECT_V5_REASON_REJECT_PATH_BINDING_MISMATCH 105u
#define TERMLINK_DIRECT_V5_REASON_REJECT_DIRECTION_BUSY 106u
#define TERMLINK_DIRECT_V5_REASON_REJECT_BARRIER_ID_MISMATCH 107u
#define TERMLINK_DIRECT_V5_REASON_REJECT_BARRIER_HASH_MISMATCH 108u
#define TERMLINK_DIRECT_V5_REASON_REJECT_UNEXPECTED_ACCEPT 109u
#define TERMLINK_DIRECT_V5_REASON_REJECT_LATE_COMMIT 110u
#define TERMLINK_DIRECT_V5_REASON_REJECT_PREFIX_TIMEOUT 111u

/*
 * The path fence is copied into every operation.  A path is valid only when
 * all fields match the active fence.  A higher epoch may replace an older
 * fence; an older epoch or a different attempt at the same epoch fails
 * closed.
 */
typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectPathV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint64_t path_epoch;
  uint8_t connection_id[16];
  uint8_t attempt_id[16];
  uint64_t reserved[2];
} TermLinkDirectPathV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectCreateV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint32_t enabled;
  uint32_t reserved0;
  uint32_t queue_capacity_frames;
  uint32_t reserved1;
  uint64_t queue_capacity_bytes;
  TermLinkDirectPathV5 path;
  uint64_t reserved[2];
} TermLinkDirectCreateV5;

/*
 * Events carry metadata only.  In particular, there is no Envelope ACK,
 * durable cursor, session identifier, Relay handle, or renderer permission.
 * frame_seq is a direct transport frame sequence; released_frames is only a
 * count of transport copies released by a receipt. For an outbound
 * TRANSPORT_RECEIPT event, frame_seq is the receipt control frame's own
 * sequence. For an inbound TRANSPORT_RECEIPT event, frame_seq is the highest
 * outbound frame acknowledged by that receipt.
 */
typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectEventV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint32_t kind;
  int32_t status;
  uint32_t flags;
  uint32_t reserved0;
  uint64_t path_epoch;
  uint8_t attempt_id[16];
  uint64_t frame_seq;
  uint32_t payload_bytes;
  uint32_t released_frames;
  uint64_t reserved[2];
} TermLinkDirectEventV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkSocketAddrV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint8_t ip[16];
  uint32_t ip_length;
  uint32_t port;
  uint64_t reserved[2];
} TermLinkSocketAddrV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectCandidateV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint8_t candidate_id[16];
  uint8_t related_candidate_id[16];
  TermLinkSocketAddrV5 endpoint;
  uint32_t kind;
  uint32_t carrier;
  uint32_t priority;
  uint32_t network_cost;
  int64_t expires_at_ms;
  uint64_t reserved[2];
} TermLinkDirectCandidateV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectCandidatePairV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  uint32_t rank;
  uint32_t reserved0;
  TermLinkDirectCandidateV5 initiator_candidate;
  TermLinkDirectCandidateV5 responder_candidate;
  uint64_t reserved[2];
} TermLinkDirectCandidatePairV5;

/*
 * local_outbound_direction is semantic, not derived from QUIC role: Windows
 * MUST use PC_TO_ANDROID and Android MUST use ANDROID_TO_PC.
 */
typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectPromoteV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectPathV5 path;
  uint8_t check_round_id[16];
  uint8_t nomination_id[16];
  TermLinkDirectCandidatePairV5 winning_pair;
  uint32_t role;
  uint32_t timeout_ms;
  uint32_t selection_confirmed;
  uint32_t local_proof_nonce_length;
  uint8_t peer_certificate_sha256[32];
  uint8_t peer_device_signing_public_key[32];
  uint8_t local_proof_nonce[32];
  int64_t now_ms;
  uint32_t local_outbound_direction;
  uint32_t reserved0;
  uint64_t reserved[3];
} TermLinkDirectPromoteV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectPathInfoV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectPathV5 path;
  uint32_t path_kind;
  uint32_t ready;
  uint8_t direct_path_binding[32];
  uint64_t reserved[2];
} TermLinkDirectPathInfoV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectSignatureV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectPathV5 path;
  uint64_t request_id;
  uint32_t signature_length;
  uint32_t reserved0;
  uint8_t signature[128];
  uint64_t reserved[2];
} TermLinkDirectSignatureV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectSessionBarrierV5 {
  uint8_t session_id[16];
  uint64_t first_direct_seq;
} TermLinkDirectSessionBarrierV5;

/*
 * barrier_sha256 MUST equal SHA-256 over:
 * "TERMLINK-DIRECT-CUTOVER-V1\0" || connection_id || path_epoch_be64 ||
 * the deterministic DirectCutoverProposalV1 protobuf reconstructed from
 * direction, barrier_id, direct_path_binding, the active sorted session prefix,
 * and duplicate_grace_ms. Native recomputes and rejects a mismatch; unused
 * session slots remain zero and are not part of the protobuf.
 */
typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectProposalV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectPathV5 path;
  uint32_t direction;
  uint32_t session_count;
  uint8_t barrier_id[16];
  uint8_t barrier_sha256[32];
  uint8_t direct_path_binding[32];
  uint32_t duplicate_grace_ms;
  uint32_t reserved0;
  uint64_t now_ms;
  uint64_t timeout_ms;
  TermLinkDirectSessionBarrierV5 sessions[32];
  uint64_t reserved[2];
} TermLinkDirectProposalV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectTransitionV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectPathV5 path;
  uint32_t direction;
  uint32_t reserved0;
  uint8_t barrier_id[16];
  uint8_t barrier_sha256[32];
  uint32_t accepted;
  uint32_t is_echo;
  uint32_t reserved1;
  uint32_t reserved2;
  uint64_t now_ms;
  uint64_t timeout_ms;
  uint64_t reserved[2];
} TermLinkDirectTransitionV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectCursorV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectPathV5 path;
  uint32_t direction;
  uint32_t reserved0;
  uint8_t session_id[16];
  uint64_t highest_contiguous;
  uint64_t reserved[2];
} TermLinkDirectCursorV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectRouteEnvelopeV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectPathV5 path;
  uint32_t direction;
  uint32_t route_eligible;
  uint8_t session_id[16];
  uint64_t envelope_sequence;
  uint64_t now_ms;
  uint64_t reserved[2];
} TermLinkDirectRouteEnvelopeV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectIngressV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectPathV5 path;
  uint32_t direction;
  uint32_t source;
  uint8_t session_id[16];
  uint64_t envelope_sequence;
  uint64_t now_ms;
  uint32_t duplicate_grace_ms;
  uint32_t reserved0;
  uint64_t reserved[2];
} TermLinkDirectIngressV5;

typedef struct TERMLINK_DIRECT_V5_ALIGN8 TermLinkDirectRouteActionV5 {
  uint32_t abi_version;
  uint32_t struct_size;
  TermLinkDirectPathV5 path;
  uint32_t kind;
  uint32_t direction;
  uint8_t session_id[16];
  uint64_t envelope_sequence;
  uint64_t direct_frame_seq;
  uint8_t barrier_id[16];
  uint8_t barrier_sha256[32];
  uint32_t accepted;
  uint32_t is_echo;
  uint32_t carrier;
  /* Reserved in V5: native always writes zero; consumers assign no meaning. */
  uint32_t flags;
  uint32_t reason;
  uint32_t payload_bytes;
  uint64_t reserved[2];
} TermLinkDirectRouteActionV5;

#if defined(__cplusplus)
#define TERMLINK_DIRECT_V5_STATIC_ASSERT(condition, message) static_assert(condition, message)
#define TERMLINK_DIRECT_V5_ALIGNOF(type) alignof(type)
extern "C" {
#else
#define TERMLINK_DIRECT_V5_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#define TERMLINK_DIRECT_V5_ALIGNOF(type) _Alignof(type)
#endif

uint32_t termlink_direct_abi_version_v5(void);
void *termlink_direct_create_v5(const TermLinkDirectCreateV5 *config);

/* Install or advance the exact path fence.  It does not establish QUIC. */
int32_t termlink_direct_path_usage_v5(void *handle,
                                      const TermLinkDirectPathV5 *path);

/* Without a promoted socket this compatibility seam returns NOT_READY. */
int32_t termlink_direct_attach_v5(void *handle,
                                  const TermLinkDirectPathV5 *path);

/*
 * Atomically requests the nominated socket from an uncommitted V4 attempt.
 * Promotion is one-shot from the caller's perspective. Once V4 accepts the
 * request, the sole socket owner moves out of V4; a later timeout, cancel,
 * authentication error, or V5 activation failure closes that socket and it
 * cannot be reused by either ABI. After any failed promotion, callers MUST
 * start a new V4 attempt rather than retrying the old nominated socket.
 */
int32_t termlink_direct_promote_v5(void *handle, void *v4_handle,
                                  const TermLinkDirectPromoteV5 *promote);
int32_t termlink_direct_submit_signature_v5(
    void *handle, const TermLinkDirectSignatureV5 *signature);
/*
 * Two-phase read. NO_DATA sets required_bytes to zero and leaves request_id
 * unchanged. BUFFER_TOO_SMALL (including a null output) sets required_bytes
 * to the complete front payload size, but neither writes request_id nor
 * dequeues. OK writes request_id and the complete payload, then dequeues it.
 */
int32_t termlink_direct_receive_signing_payload_v5(
    void *handle, const TermLinkDirectPathV5 *path, uint64_t *request_id,
    uint8_t *output, uint32_t output_capacity, uint32_t *required_bytes);
int32_t termlink_direct_path_info_v5(void *handle,
                                    const TermLinkDirectPathV5 *path,
                                    TermLinkDirectPathInfoV5 *output);

/*
 * A short or null output buffer does not copy, dequeue, or write frame_seq.
 * required_bytes is set to the front Envelope size in that case. NO_DATA sets
 * required_bytes to zero and leaves frame_seq unchanged.
 */
int32_t termlink_direct_receive_envelope_v5(
    void *handle, const TermLinkDirectPathV5 *path, uint64_t *frame_seq,
    uint8_t *output, uint32_t output_capacity, uint32_t *required_bytes);

/*
 * On OK, the caller sends the exact proposal it supplied. No partial
 * PROPOSAL route action is emitted.
 */
int32_t termlink_direct_start_proposal_v5(
    void *handle, const TermLinkDirectProposalV5 *proposal);
int32_t termlink_direct_receive_proposal_v5(
    void *handle, const TermLinkDirectProposalV5 *proposal);
int32_t termlink_direct_receive_accept_v5(
    void *handle, const TermLinkDirectTransitionV5 *transition);
int32_t termlink_direct_receive_commit_v5(
    void *handle, const TermLinkDirectTransitionV5 *transition);
int32_t termlink_direct_commit_echo_sent_v5(
    void *handle, const TermLinkDirectTransitionV5 *transition);
int32_t termlink_direct_receive_fallback_v5(
    void *handle, const TermLinkDirectTransitionV5 *transition);
int32_t termlink_direct_tick_v5(
    void *handle, const TermLinkDirectTransitionV5 *transition);
/*
 * Installs a caller-verified application cursor; this never creates a durable
 * ACK. Cursors survive transport epochs and are bounded per direction by
 * MAX_APPLIED_CURSORS_PER_DIRECTION. At capacity, a new session returns LIMIT
 * atomically while an existing session remains updateable. Advancing a cursor
 * authorizes native to release its delivered bytes/digest history through that
 * sequence. The caller's existing journal/durable-ACK digest fence remains
 * authoritative for older late duplicates and conflicts.
 */
int32_t termlink_direct_update_applied_cursor_v5(
    void *handle, const TermLinkDirectCursorV5 *cursor);
/*
 * Removes cursor.session_id from exactly cursor.direction while preserving the
 * authenticated path and every other session. highest_contiguous MUST be zero.
 * Any unreceipted Direct-only bytes are returned as byte-identical Relay route
 * actions. A SESSION_CARRIER action identifies a per-session carrier change;
 * the fixed RouteAction layout and offsets are unchanged.
 */
int32_t termlink_direct_deregister_session_v5(
    void *handle, const TermLinkDirectCursorV5 *session);
/*
 * On OK, native has taken ownership of a private copy of this exact Envelope.
 * Eligible Direct transmissions are written by native code and produce
 * metadata-only DIRECT actions. Relay actions carry the exact payload for the
 * existing Relay sender. On every non-OK result, including QUEUE_FULL, the
 * current Envelope was not accepted or retained: ownership remains with the
 * caller, which may retry or send it via Relay. If QUEUE_FULL also retires a
 * Direct path, later Relay actions contain only exact bytes accepted by prior
 * successful calls.
 */
int32_t termlink_direct_route_envelope_v5(
    void *handle, const TermLinkDirectRouteEnvelopeV5 *route,
    const uint8_t *envelope, uint32_t envelope_bytes);
/*
 * NO_DATA sets required_bytes to zero and leaves action unchanged.
 * BUFFER_TOO_SMALL never dequeues or writes action metadata. After a Direct
 * failure/cancel/network change, drain ordered Relay/control actions before
 * destroying the handle. payload_bytes is the logical Envelope size described
 * by the action; required_bytes is the number of payload bytes returned by
 * this poll. Metadata-only Direct/Hold/Drop actions therefore have
 * payload_bytes > 0 and required_bytes == 0. Every HOLD is fenced by action.path;
 * a successor path invalidates it, so the platform MUST discard unpaired HOLD
 * state from the predecessor rather than waiting for a DROP action.
 */
int32_t termlink_direct_poll_route_action_v5(
    void *handle, TermLinkDirectRouteActionV5 *action, uint8_t *output,
    uint32_t output_capacity, uint32_t *required_bytes);
/*
 * On OK, native has copied or fully classified this ingress Envelope. On any
 * non-OK result it has not taken ownership of the current bytes. In particular,
 * PROTOCOL for same (direction, session, sequence) with different bytes retires
 * Direct. If the current input came from Relay, the caller MUST retain those
 * authoritative Relay bytes and invoke its existing journal/durable-ACK
 * conflict recovery. Sequences at or below an installed application cursor are
 * emitted as DROP; the caller must apply its durable digest fence as described
 * by update_applied_cursor before submitting such late inputs. DIRECT source is
 * accepted only while that exact semantic direction is committed to Direct;
 * path readiness alone is insufficient. Once the path is failed, cancelled, or
 * invalidated by a network change, new Relay and Direct ingress both return
 * CANCELLED without being retained; callers may only drain route actions and
 * other bytes already owned by native code.
 */
int32_t termlink_direct_arbitrate_ingress_v5(
    void *handle, const TermLinkDirectIngressV5 *ingress,
    const uint8_t *envelope, uint32_t envelope_bytes);
int32_t termlink_direct_tick_ingress_v5(
    void *handle, const TermLinkDirectPathV5 *path, uint64_t now_ms);

/*
 * Poll is non-blocking and writes one complete fixed-layout event. Return 0
 * means no event and leaves output unchanged; return 1 writes one event.
 */
int32_t termlink_direct_poll_event_v5(void *handle,
                                      TermLinkDirectEventV5 *output);

/* Both calls synchronously retire the exact path after worker processing. */
int32_t termlink_direct_cancel_v5(void *handle,
                                  const TermLinkDirectPathV5 *path);
int32_t termlink_direct_network_changed_v5(
    void *handle, const TermLinkDirectPathV5 *path);

/*
 * Cancels the worker and joins it before returning. The caller must ensure no
 * other thread is using this handle before destroy begins.
 */
void termlink_direct_destroy_v5(void *handle);

#if defined(__cplusplus)
} /* extern "C" */
#endif

#define TERMLINK_DIRECT_V5_ASSERT_OFFSET(type, field, value)                 \
  TERMLINK_DIRECT_V5_STATIC_ASSERT(offsetof(type, field) == (value),         \
                                   #type "." #field " ABI drift")
#define TERMLINK_DIRECT_V5_ASSERT_ALIGN8(type)                                \
  TERMLINK_DIRECT_V5_STATIC_ASSERT(TERMLINK_DIRECT_V5_ALIGNOF(type) == 8,      \
                                   #type " alignment drift")

TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectPathV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectCreateV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectEventV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkSocketAddrV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectCandidateV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectCandidatePairV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectPromoteV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectPathInfoV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectSignatureV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectSessionBarrierV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectProposalV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectTransitionV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectCursorV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectRouteEnvelopeV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectRouteActionV5);
TERMLINK_DIRECT_V5_ASSERT_ALIGN8(TermLinkDirectIngressV5);

TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectPathV5) == 64,
                                 "TermLinkDirectPathV5 ABI drift");
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathV5, path_epoch, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathV5, connection_id, 16);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathV5, attempt_id, 32);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathV5, reserved, 48);

TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectCreateV5) == 112,
                                 "TermLinkDirectCreateV5 ABI drift");
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCreateV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCreateV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCreateV5, enabled, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCreateV5, reserved0, 12);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCreateV5, queue_capacity_frames, 16);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCreateV5, reserved1, 20);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCreateV5, queue_capacity_bytes, 24);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCreateV5, path, 32);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCreateV5, reserved, 96);

TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectEventV5) == 80,
                                 "TermLinkDirectEventV5 ABI drift");
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, kind, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, status, 12);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, flags, 16);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, reserved0, 20);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, path_epoch, 24);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, attempt_id, 32);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, frame_seq, 48);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, payload_bytes, 56);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, released_frames, 60);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectEventV5, reserved, 64);

TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkSocketAddrV5) == 48,
                                 "TermLinkSocketAddrV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectCandidateV5) == 128,
                                 "TermLinkDirectCandidateV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectCandidatePairV5) == 288,
                                 "TermLinkDirectCandidatePairV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectPromoteV5) == 544,
                                 "TermLinkDirectPromoteV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectPathInfoV5) == 128,
                                 "TermLinkDirectPathInfoV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectSignatureV5) == 232,
                                 "TermLinkDirectSignatureV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectSessionBarrierV5) == 24,
                                 "TermLinkDirectSessionBarrierV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectProposalV5) == 968,
                                 "TermLinkDirectProposalV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectTransitionV5) == 176,
                                 "TermLinkDirectTransitionV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectCursorV5) == 120,
                                 "TermLinkDirectCursorV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectRouteEnvelopeV5) == 128,
                                 "TermLinkDirectRouteEnvelopeV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectRouteActionV5) == 200,
                                 "TermLinkDirectRouteActionV5 ABI drift");
TERMLINK_DIRECT_V5_STATIC_ASSERT(sizeof(TermLinkDirectIngressV5) == 136,
                                 "TermLinkDirectIngressV5 ABI drift");

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkSocketAddrV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkSocketAddrV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkSocketAddrV5, ip, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkSocketAddrV5, ip_length, 24);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkSocketAddrV5, port, 28);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkSocketAddrV5, reserved, 32);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, candidate_id, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5,
                                 related_candidate_id, 24);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, endpoint, 40);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, kind, 88);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, carrier, 92);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, priority, 96);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, network_cost, 100);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, expires_at_ms, 104);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidateV5, reserved, 112);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidatePairV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidatePairV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidatePairV5, rank, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidatePairV5, reserved0, 12);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidatePairV5,
                                 initiator_candidate, 16);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidatePairV5,
                                 responder_candidate, 144);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCandidatePairV5, reserved, 272);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, path, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, check_round_id, 72);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, nomination_id, 88);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, winning_pair, 104);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, role, 392);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, timeout_ms, 396);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5,
                                 selection_confirmed, 400);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5,
                                 local_proof_nonce_length, 404);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5,
                                 peer_certificate_sha256, 408);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5,
                                 peer_device_signing_public_key, 440);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5,
                                 local_proof_nonce, 472);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, now_ms, 504);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5,
                                 local_outbound_direction, 512);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, reserved0, 516);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPromoteV5, reserved, 520);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathInfoV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathInfoV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathInfoV5, path, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathInfoV5, path_kind, 72);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathInfoV5, ready, 76);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathInfoV5,
                                 direct_path_binding, 80);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectPathInfoV5, reserved, 112);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSignatureV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSignatureV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSignatureV5, path, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSignatureV5, request_id, 72);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSignatureV5, signature_length, 80);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSignatureV5, reserved0, 84);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSignatureV5, signature, 88);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSignatureV5, reserved, 216);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSessionBarrierV5, session_id, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectSessionBarrierV5,
                                 first_direct_seq, 16);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, path, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, direction, 72);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, session_count, 76);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, barrier_id, 80);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, barrier_sha256, 96);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5,
                                 direct_path_binding, 128);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5,
                                 duplicate_grace_ms, 160);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, reserved0, 164);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, now_ms, 168);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, timeout_ms, 176);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, sessions, 184);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectProposalV5, reserved, 952);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, path, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, direction, 72);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, reserved0, 76);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, barrier_id, 80);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5,
                                 barrier_sha256, 96);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, accepted, 128);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, is_echo, 132);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, reserved1, 136);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, reserved2, 140);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, now_ms, 144);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, timeout_ms, 152);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectTransitionV5, reserved, 160);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCursorV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCursorV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCursorV5, path, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCursorV5, direction, 72);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCursorV5, reserved0, 76);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCursorV5, session_id, 80);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCursorV5,
                                 highest_contiguous, 96);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectCursorV5, reserved, 104);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteEnvelopeV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteEnvelopeV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteEnvelopeV5, path, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteEnvelopeV5, direction, 72);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteEnvelopeV5,
                                 route_eligible, 76);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteEnvelopeV5, session_id, 80);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteEnvelopeV5,
                                 envelope_sequence, 96);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteEnvelopeV5, now_ms, 104);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteEnvelopeV5, reserved, 112);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5, path, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5, direction, 72);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5, source, 76);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5, session_id, 80);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5,
                                 envelope_sequence, 96);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5, now_ms, 104);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5,
                                 duplicate_grace_ms, 112);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5, reserved0, 116);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectIngressV5, reserved, 120);

TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, abi_version, 0);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, struct_size, 4);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, path, 8);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, kind, 72);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, direction, 76);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, session_id, 80);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5,
                                 envelope_sequence, 96);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5,
                                 direct_frame_seq, 104);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, barrier_id, 112);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5,
                                 barrier_sha256, 128);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, accepted, 160);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, is_echo, 164);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, carrier, 168);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, flags, 172);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, reason, 176);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, payload_bytes, 180);
TERMLINK_DIRECT_V5_ASSERT_OFFSET(TermLinkDirectRouteActionV5, reserved, 184);

#undef TERMLINK_DIRECT_V5_ASSERT_OFFSET
#undef TERMLINK_DIRECT_V5_ASSERT_ALIGN8
#undef TERMLINK_DIRECT_V5_ALIGNOF
#undef TERMLINK_DIRECT_V5_STATIC_ASSERT
#undef TERMLINK_DIRECT_V5_ALIGN8

#endif /* TERMLINK_DIRECT_FFI_V5_H */
