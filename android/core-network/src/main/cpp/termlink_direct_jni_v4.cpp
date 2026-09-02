#include <jni.h>

#include <array>
#include <atomic>
#include <climits>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <unordered_map>

#include "termlink_direct_ffi_v4.h"

namespace {

constexpr jint kOk = 0;
constexpr jint kInvalid = -2;
constexpr jint kBridgeFailure = -3;
constexpr jsize kGatherScalarCount = 6;
constexpr jsize kBeginScalarCount = 9;
constexpr jsize kBeginBlobCount = 11;
constexpr jsize kAppendScalarCount = 5;
constexpr jsize kAppendBlobCount = 2;
constexpr jsize kCommitScalarCount = 5;
constexpr jsize kCommitBlobCount = 7;
constexpr jsize kPollMetadataCount = 11;
constexpr jsize kEncodedCandidateBytes = 80;
constexpr jsize kEncodedPairBytes = 164;

static_assert(sizeof(TermLinkDirectCreateV4) == 32);
static_assert(sizeof(TermLinkSocketAddrV4) == 48);
static_assert(sizeof(TermLinkDirectGatherV4) == 256);
static_assert(sizeof(TermLinkDirectCandidateV4) == 128);
static_assert(sizeof(TermLinkDirectCandidateBatchV4) == 1088);
static_assert(sizeof(TermLinkDirectCandidatePairV4) == 288);
static_assert(sizeof(TermLinkDirectBeginChecksV4) == 4656);
static_assert(sizeof(TermLinkDirectAppendCheckPairsV4) == 2400);
static_assert(sizeof(TermLinkDirectCommitNominationV4) == 504);
static_assert(sizeof(TermLinkDirectControlV4) == 48);
static_assert(sizeof(TermLinkDirectSignatureV4) == 192);
static_assert(sizeof(TermLinkDirectEventV4) == 2224);
static_assert(offsetof(TermLinkDirectBeginChecksV4, pairs) == 2320);
static_assert(offsetof(TermLinkDirectEventV4, payload) == 112);

struct BridgeHolderV4 {
    explicit BridgeHolderV4(void* value) : bridge(value) {}
    ~BridgeHolderV4() {
        if (bridge != nullptr) termlink_direct_destroy_v4(bridge);
    }
    std::mutex mutex;
    void* bridge;
};

std::mutex g_v4_registry_mutex;
std::unordered_map<std::uint64_t, std::shared_ptr<BridgeHolderV4>> g_v4_registry;
std::atomic<std::uint64_t> g_v4_next_token{1};

std::shared_ptr<BridgeHolderV4> find_v4_holder(jlong raw_token) {
    if (raw_token <= 0) return {};
    std::lock_guard lock(g_v4_registry_mutex);
    const auto found = g_v4_registry.find(static_cast<std::uint64_t>(raw_token));
    return found == g_v4_registry.end() ? std::shared_ptr<BridgeHolderV4>{} : found->second;
}

std::uint64_t register_v4_holder(std::shared_ptr<BridgeHolderV4> holder) {
    for (;;) {
        const auto token = g_v4_next_token.fetch_add(1, std::memory_order_relaxed);
        if (token == 0 || token > static_cast<std::uint64_t>(INT64_MAX)) continue;
        std::lock_guard lock(g_v4_registry_mutex);
        if (g_v4_registry.emplace(token, holder).second) return token;
    }
}

std::shared_ptr<BridgeHolderV4> remove_v4_holder(jlong raw_token) {
    if (raw_token <= 0) return {};
    std::lock_guard lock(g_v4_registry_mutex);
    const auto found = g_v4_registry.find(static_cast<std::uint64_t>(raw_token));
    if (found == g_v4_registry.end()) return {};
    auto holder = found->second;
    g_v4_registry.erase(found);
    return holder;
}

template <typename Operation>
jint with_v4_bridge(jlong token, Operation&& operation) {
    auto holder = find_v4_holder(token);
    if (!holder) return kInvalid;
    std::lock_guard lock(holder->mutex);
    if (holder->bridge == nullptr) return kInvalid;
    return static_cast<jint>(operation(holder->bridge));
}

bool all_zero(const std::uint8_t* bytes, std::size_t size) {
    for (std::size_t index = 0; index < size; ++index) {
        if (bytes[index] != 0) return false;
    }
    return true;
}

void secure_zero(std::uint8_t* bytes, std::size_t size) {
    volatile std::uint8_t* output = bytes;
    for (std::size_t index = 0; index < size; ++index) output[index] = 0;
}

bool nonzero_id(const std::uint8_t* bytes) { return !all_zero(bytes, 16); }

bool copy_exact(JNIEnv* env, jbyteArray source, std::uint8_t* destination,
                std::size_t expected_size) {
    if (source == nullptr ||
        env->GetArrayLength(source) != static_cast<jsize>(expected_size)) return false;
    env->GetByteArrayRegion(source, 0, static_cast<jsize>(expected_size),
                            reinterpret_cast<jbyte*>(destination));
    return !env->ExceptionCheck();
}

bool copy_bounded(JNIEnv* env, jbyteArray source, std::uint8_t* destination,
                  std::size_t capacity, std::uint32_t& length) {
    if (source == nullptr) return false;
    const auto size = env->GetArrayLength(source);
    if (size < 0 || static_cast<std::size_t>(size) > capacity) return false;
    if (size > 0) {
        env->GetByteArrayRegion(source, 0, size, reinterpret_cast<jbyte*>(destination));
        if (env->ExceptionCheck()) return false;
    }
    length = static_cast<std::uint32_t>(size);
    return true;
}

jbyteArray blob_at(JNIEnv* env, jobjectArray blobs, jsize index) {
    if (blobs == nullptr || index < 0 || index >= env->GetArrayLength(blobs)) return nullptr;
    return static_cast<jbyteArray>(env->GetObjectArrayElement(blobs, index));
}

bool copy_exact_blob(JNIEnv* env, jobjectArray blobs, jsize index,
                     std::uint8_t* destination, std::size_t expected_size) {
    jbyteArray blob = blob_at(env, blobs, index);
    const bool copied = copy_exact(env, blob, destination, expected_size);
    if (blob != nullptr) env->DeleteLocalRef(blob);
    return copied;
}

bool copy_bounded_blob(JNIEnv* env, jobjectArray blobs, jsize index,
                       std::uint8_t* destination, std::size_t capacity,
                       std::uint32_t& length) {
    jbyteArray blob = blob_at(env, blobs, index);
    const bool copied = copy_bounded(env, blob, destination, capacity, length);
    if (blob != nullptr) env->DeleteLocalRef(blob);
    return copied;
}

bool decode_address(JNIEnv* env, jbyteArray ip, jlong port, TermLinkSocketAddrV4& address) {
    if (ip == nullptr || port < 0 || port > 65535) return false;
    const auto length = env->GetArrayLength(ip);
    if (length != 4 && length != 16) return false;
    address = {};
    address.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    address.struct_size = static_cast<std::uint32_t>(sizeof(address));
    address.ip_length = static_cast<std::uint32_t>(length);
    address.port = static_cast<std::uint32_t>(port);
    env->GetByteArrayRegion(ip, 0, length, reinterpret_cast<jbyte*>(address.ip));
    return !env->ExceptionCheck();
}

bool decode_blob_address(JNIEnv* env, jobjectArray blobs, jsize index, jint port,
                         TermLinkSocketAddrV4& address) {
    jbyteArray ip = blob_at(env, blobs, index);
    const bool decoded = decode_address(env, ip, port, address);
    if (ip != nullptr) env->DeleteLocalRef(ip);
    return decoded;
}

std::uint32_t read_u32_le(const std::uint8_t* bytes) {
    return static_cast<std::uint32_t>(bytes[0]) |
           (static_cast<std::uint32_t>(bytes[1]) << 8U) |
           (static_cast<std::uint32_t>(bytes[2]) << 16U) |
           (static_cast<std::uint32_t>(bytes[3]) << 24U);
}

std::uint64_t read_u64_le(const std::uint8_t* bytes) {
    std::uint64_t result = 0;
    for (std::uint32_t index = 0; index < 8; ++index) {
        result |= static_cast<std::uint64_t>(bytes[index]) << (index * 8U);
    }
    return result;
}

bool decode_candidate(const std::uint8_t* encoded, TermLinkDirectCandidateV4& output) {
    output = {};
    output.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    output.struct_size = static_cast<std::uint32_t>(sizeof(output));
    std::memcpy(output.candidate_id, encoded, 16);
    std::memcpy(output.related_candidate_id, encoded + 16, 16);
    output.endpoint.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    output.endpoint.struct_size = static_cast<std::uint32_t>(sizeof(output.endpoint));
    std::memcpy(output.endpoint.ip, encoded + 32, 16);
    output.endpoint.ip_length = read_u32_le(encoded + 48);
    output.endpoint.port = read_u32_le(encoded + 52);
    output.kind = read_u32_le(encoded + 56);
    output.carrier = read_u32_le(encoded + 60);
    output.priority = read_u32_le(encoded + 64);
    output.network_cost = read_u32_le(encoded + 68);
    output.expires_at_ms = static_cast<std::int64_t>(read_u64_le(encoded + 72));
    return nonzero_id(output.candidate_id) &&
           (output.endpoint.ip_length == 4 || output.endpoint.ip_length == 16) &&
           output.endpoint.port >= 1024 && output.endpoint.port <= 65535 &&
           (output.endpoint.ip_length != 4 || all_zero(output.endpoint.ip + 4, 12)) &&
           (output.kind == TERMLINK_DIRECT_CANDIDATE_HOST_V4 ||
            output.kind == TERMLINK_DIRECT_CANDIDATE_SERVER_REFLEXIVE_V4) &&
           output.carrier == TERMLINK_DIRECT_CARRIER_UDP_QUIC_V4 && output.priority != 0 &&
           output.expires_at_ms > 0 &&
           ((output.kind == TERMLINK_DIRECT_CANDIDATE_HOST_V4 &&
             all_zero(output.related_candidate_id, 16)) ||
            (output.kind == TERMLINK_DIRECT_CANDIDATE_SERVER_REFLEXIVE_V4 &&
             nonzero_id(output.related_candidate_id)));
}

bool decode_pair(JNIEnv* env, jbyteArray source, TermLinkDirectCandidatePairV4& output) {
    std::array<std::uint8_t, kEncodedPairBytes> encoded{};
    if (!copy_exact(env, source, encoded.data(), encoded.size())) return false;
    output = {};
    output.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    output.struct_size = static_cast<std::uint32_t>(sizeof(output));
    output.rank = read_u32_le(encoded.data());
    return output.rank > 0 && output.rank <= TERMLINK_DIRECT_MAX_PAIRS_TOTAL_V4 &&
           decode_candidate(encoded.data() + 4, output.initiator_candidate) &&
           decode_candidate(encoded.data() + 4 + kEncodedCandidateBytes,
                            output.responder_candidate) &&
           std::memcmp(output.initiator_candidate.candidate_id,
                       output.responder_candidate.candidate_id, 16) != 0;
}

template <std::size_t Capacity>
bool decode_pairs(JNIEnv* env, jobjectArray encoded_pairs,
                  TermLinkDirectCandidatePairV4 (&output)[Capacity], std::uint32_t count) {
    if (encoded_pairs == nullptr || count > Capacity ||
        env->GetArrayLength(encoded_pairs) != static_cast<jsize>(count)) return false;
    for (std::uint32_t index = 0; index < count; ++index) {
        jbyteArray pair = blob_at(env, encoded_pairs, static_cast<jsize>(index));
        const bool decoded = decode_pair(env, pair, output[index]);
        if (pair != nullptr) env->DeleteLocalRef(pair);
        if (!decoded) return false;
    }
    return true;
}

TermLinkDirectControlV4 control_record(jlong epoch,
                                       const std::array<std::uint8_t, 16>& attempt_id) {
    TermLinkDirectControlV4 control{};
    control.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    control.struct_size = static_cast<std::uint32_t>(sizeof(control));
    control.epoch = static_cast<std::uint64_t>(epoch);
    std::memcpy(control.attempt_id, attempt_id.data(), attempt_id.size());
    return control;
}

bool reserved_zero(const std::uint64_t* values, std::size_t count) {
    for (std::size_t index = 0; index < count; ++index) {
        if (values[index] != 0) return false;
    }
    return true;
}

bool address_valid(const TermLinkSocketAddrV4& value, bool optional) {
    if (optional && all_zero(reinterpret_cast<const std::uint8_t*>(&value), sizeof(value))) {
        return true;
    }
    if (value.abi_version != TERMLINK_DIRECT_ABI_VERSION_V4 ||
        value.struct_size != sizeof(value) || !reserved_zero(value.reserved, 2)) return false;
    if (optional && value.ip_length == 0 && value.port == 0) return all_zero(value.ip, 16);
    return (value.ip_length == 4 || value.ip_length == 16) && value.port <= 65535 &&
           value.port != 0 && (value.ip_length != 4 || all_zero(value.ip + 4, 12));
}

bool candidate_valid(const TermLinkDirectCandidateV4& value) {
    return value.abi_version == TERMLINK_DIRECT_ABI_VERSION_V4 &&
           value.struct_size == sizeof(value) && nonzero_id(value.candidate_id) &&
           address_valid(value.endpoint, false) &&
           (value.kind == TERMLINK_DIRECT_CANDIDATE_HOST_V4 ||
            value.kind == TERMLINK_DIRECT_CANDIDATE_SERVER_REFLEXIVE_V4) &&
           value.carrier == TERMLINK_DIRECT_CARRIER_UDP_QUIC_V4 && value.priority != 0 &&
           value.expires_at_ms > 0 && reserved_zero(value.reserved, 2) &&
           ((value.kind == TERMLINK_DIRECT_CANDIDATE_HOST_V4 &&
             all_zero(value.related_candidate_id, 16)) ||
            (value.kind == TERMLINK_DIRECT_CANDIDATE_SERVER_REFLEXIVE_V4 &&
             nonzero_id(value.related_candidate_id)));
}

bool pair_valid(const TermLinkDirectCandidatePairV4& value) {
    return value.abi_version == TERMLINK_DIRECT_ABI_VERSION_V4 &&
           value.struct_size == sizeof(value) && value.rank > 0 && value.reserved0 == 0 &&
           candidate_valid(value.initiator_candidate) &&
           candidate_valid(value.responder_candidate) &&
           value.initiator_candidate.endpoint.ip_length ==
               value.responder_candidate.endpoint.ip_length &&
           std::memcmp(value.initiator_candidate.candidate_id,
                       value.responder_candidate.candidate_id, 16) != 0 &&
           reserved_zero(value.reserved, 2);
}

bool event_valid(const TermLinkDirectEventV4& event) {
    if (event.abi_version != TERMLINK_DIRECT_ABI_VERSION_V4 ||
        event.struct_size != sizeof(event) || event.epoch == 0 || event.generation == 0 ||
        !nonzero_id(event.attempt_id) || !address_valid(event.endpoint, true) ||
        !reserved_zero(event.reserved, 4)) return false;
    switch (event.kind) {
        case TERMLINK_DIRECT_EVENT_IDENTITY_READY_V4:
        case TERMLINK_DIRECT_EVENT_SIGN_REQUEST_V4:
            return event.payload_size == sizeof(TermLinkDirectOpaqueBytesV4) &&
                   event.payload.opaque.abi_version == TERMLINK_DIRECT_ABI_VERSION_V4 &&
                   event.payload.opaque.struct_size == sizeof(TermLinkDirectOpaqueBytesV4) &&
                   event.payload.opaque.data_length > 0 &&
                   event.payload.opaque.data_length <= TERMLINK_DIRECT_MAX_EVENT_BYTES_V4 &&
                   event.payload.opaque.reserved0 == 0 &&
                   all_zero(event.payload.opaque.data + event.payload.opaque.data_length,
                            TERMLINK_DIRECT_MAX_EVENT_BYTES_V4 -
                                event.payload.opaque.data_length) &&
                   reserved_zero(event.payload.opaque.reserved, 2);
        case TERMLINK_DIRECT_EVENT_CANDIDATE_BATCH_V4: {
            const auto& batch = event.payload.candidate_batch;
            if (event.payload_size != sizeof(batch) ||
                batch.abi_version != TERMLINK_DIRECT_ABI_VERSION_V4 ||
                batch.struct_size != sizeof(batch) || batch.batch_seq == 0 ||
                batch.candidate_count > TERMLINK_DIRECT_MAX_CANDIDATES_PER_BATCH_V4 ||
                batch.total_candidate_count > TERMLINK_DIRECT_MAX_CANDIDATES_TOTAL_V4 ||
                batch.total_candidate_count < batch.candidate_count ||
                batch.gathering_complete > 1 ||
                (batch.candidate_count == 0 && batch.gathering_complete == 0) ||
                batch.epoch != event.epoch ||
                std::memcmp(batch.attempt_id, event.attempt_id, 16) != 0 ||
                !reserved_zero(batch.reserved, 2)) return false;
            for (std::uint32_t index = 0; index < batch.candidate_count; ++index) {
                if (!candidate_valid(batch.candidates[index])) return false;
            }
            for (std::uint32_t index = batch.candidate_count;
                 index < TERMLINK_DIRECT_MAX_CANDIDATES_PER_BATCH_V4; ++index) {
                if (!all_zero(reinterpret_cast<const std::uint8_t*>(&batch.candidates[index]),
                              sizeof(batch.candidates[index]))) return false;
            }
            return true;
        }
        case TERMLINK_DIRECT_EVENT_PAIR_VALIDATED_V4:
            return event.payload_size == sizeof(TermLinkDirectPairValidatedV4) &&
                   event.payload.pair_validated.abi_version == TERMLINK_DIRECT_ABI_VERSION_V4 &&
                   event.payload.pair_validated.struct_size ==
                       sizeof(TermLinkDirectPairValidatedV4) &&
                   event.payload.pair_validated.epoch == event.epoch &&
                   std::memcmp(event.payload.pair_validated.attempt_id,
                               event.attempt_id, 16) == 0 &&
                   pair_valid(event.payload.pair_validated.pair) &&
                   event.payload.pair_validated.probe_attempts <= 4 &&
                   reserved_zero(event.payload.pair_validated.reserved, 2);
        case TERMLINK_DIRECT_EVENT_CHECKS_COMPLETE_V4: {
            const auto& complete = event.payload.checks_complete;
            return event.payload_size == sizeof(TermLinkDirectChecksCompleteV4) &&
                   complete.abi_version == TERMLINK_DIRECT_ABI_VERSION_V4 &&
                   complete.struct_size == sizeof(TermLinkDirectChecksCompleteV4) &&
                   complete.epoch == event.epoch &&
                   std::memcmp(complete.attempt_id, event.attempt_id, 16) == 0 &&
                   complete.total_pair_count <= TERMLINK_DIRECT_MAX_PAIRS_TOTAL_V4 &&
                   complete.validated_pair_count <= complete.total_pair_count &&
                   complete.exhausted_pair_count + complete.validated_pair_count ==
                       complete.total_pair_count && complete.result <= 1 &&
                   reserved_zero(complete.reserved, 2);
        }
        case TERMLINK_DIRECT_EVENT_NOMINATION_READY_V4:
            return event.payload_size == sizeof(TermLinkDirectNominationReadyV4) &&
                   event.payload.nomination_ready.abi_version == TERMLINK_DIRECT_ABI_VERSION_V4 &&
                   event.payload.nomination_ready.struct_size ==
                       sizeof(TermLinkDirectNominationReadyV4) &&
                   event.payload.nomination_ready.epoch == event.epoch &&
                   std::memcmp(event.payload.nomination_ready.attempt_id,
                               event.attempt_id, 16) == 0 &&
                   nonzero_id(event.payload.nomination_ready.check_round_id) &&
                   nonzero_id(event.payload.nomination_ready.nomination_id) &&
                   pair_valid(event.payload.nomination_ready.winning_pair) &&
                   (event.payload.nomination_ready.role == TERMLINK_DIRECT_ROLE_INITIATOR_V4 ||
                    event.payload.nomination_ready.role == TERMLINK_DIRECT_ROLE_RESPONDER_V4) &&
                   event.payload.nomination_ready.reserved0 == 0 &&
                   reserved_zero(event.payload.nomination_ready.reserved, 2);
        case TERMLINK_DIRECT_EVENT_SHADOW_READY_V4:
            return event.payload_size == sizeof(TermLinkDirectShadowReadyV4) &&
                   event.payload.shadow_ready.abi_version == TERMLINK_DIRECT_ABI_VERSION_V4 &&
                   event.payload.shadow_ready.struct_size == sizeof(TermLinkDirectShadowReadyV4) &&
                   event.payload.shadow_ready.epoch == event.epoch &&
                   std::memcmp(event.payload.shadow_ready.attempt_id,
                               event.attempt_id, 16) == 0 &&
                   nonzero_id(event.payload.shadow_ready.check_round_id) &&
                   nonzero_id(event.payload.shadow_ready.nomination_id) &&
                   pair_valid(event.payload.shadow_ready.winning_pair) &&
                   !all_zero(event.payload.shadow_ready.direct_path_binding, 32) &&
                   reserved_zero(event.payload.shadow_ready.reserved, 2);
        case TERMLINK_DIRECT_EVENT_STATE_CHANGED_V4:
        case TERMLINK_DIRECT_EVENT_DIRECT_FAILED_V4:
        case TERMLINK_DIRECT_EVENT_IGNORED_STALE_V4:
            return event.payload_size == 0;
        default:
            return false;
    }
}

}  // namespace

extern "C" JNIEXPORT jlongArray JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_abiContractV4(
    JNIEnv* env, jobject) {
    const std::array<jlong, 36> values{
        termlink_direct_abi_version_v4(), sizeof(TermLinkDirectCreateV4),
        sizeof(TermLinkSocketAddrV4), sizeof(TermLinkDirectGatherV4),
        sizeof(TermLinkDirectCandidateV4), sizeof(TermLinkDirectCandidateBatchV4),
        sizeof(TermLinkDirectCandidatePairV4), sizeof(TermLinkDirectBeginChecksV4),
        sizeof(TermLinkDirectAppendCheckPairsV4), sizeof(TermLinkDirectCommitNominationV4),
        sizeof(TermLinkDirectControlV4), sizeof(TermLinkDirectSignatureV4),
        sizeof(TermLinkDirectOpaqueBytesV4), sizeof(TermLinkDirectPairValidatedV4),
        sizeof(TermLinkDirectChecksCompleteV4), sizeof(TermLinkDirectNominationReadyV4),
        sizeof(TermLinkDirectShadowReadyV4), sizeof(TermLinkDirectEventPayloadV4),
        sizeof(TermLinkDirectEventV4), offsetof(TermLinkDirectGatherV4, local_bind),
        offsetof(TermLinkDirectGatherV4, stun_servers),
        offsetof(TermLinkDirectCandidateV4, endpoint),
        offsetof(TermLinkDirectCandidateBatchV4, candidates),
        offsetof(TermLinkDirectCandidatePairV4, initiator_candidate),
        offsetof(TermLinkDirectCandidatePairV4, responder_candidate),
        offsetof(TermLinkDirectBeginChecksV4, ticket),
        offsetof(TermLinkDirectBeginChecksV4, local_send_probe_hmac_key),
        offsetof(TermLinkDirectBeginChecksV4, pairs),
        offsetof(TermLinkDirectAppendCheckPairsV4, pairs),
        offsetof(TermLinkDirectCommitNominationV4, winning_pair),
        offsetof(TermLinkDirectCommitNominationV4, local_proof_nonce),
        offsetof(TermLinkDirectEventV4, generation), offsetof(TermLinkDirectEventV4, endpoint),
        offsetof(TermLinkDirectEventV4, payload), offsetof(TermLinkDirectEventV4, reserved),
        offsetof(TermLinkDirectBeginChecksV4, reserved),
    };
    jlongArray result = env->NewLongArray(static_cast<jsize>(values.size()));
    if (result != nullptr) {
        env->SetLongArrayRegion(result, 0, static_cast<jsize>(values.size()), values.data());
    }
    return result;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_createV4(
    JNIEnv* env, jobject, jboolean enabled, jint queue_capacity, jlongArray out_handle) {
    if (out_handle == nullptr || env->GetArrayLength(out_handle) != 1 ||
        queue_capacity <= 0 || queue_capacity > 64) return kInvalid;
    TermLinkDirectCreateV4 config{};
    config.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    config.struct_size = static_cast<std::uint32_t>(sizeof(config));
    config.enabled = enabled == JNI_TRUE ? 1U : 0U;
    config.queue_capacity = static_cast<std::uint32_t>(queue_capacity);
    config.platform = TERMLINK_DIRECT_PLATFORM_ANDROID_V4;
    void* bridge = termlink_direct_create_v4(&config);
    if (bridge == nullptr) return -1;
    const auto token = register_v4_holder(std::make_shared<BridgeHolderV4>(bridge));
    const jlong encoded = static_cast<jlong>(token);
    env->SetLongArrayRegion(out_handle, 0, 1, &encoded);
    if (!env->ExceptionCheck()) return kOk;
    auto holder = remove_v4_holder(encoded);
    if (holder) {
        termlink_direct_destroy_v4(holder->bridge);
        holder->bridge = nullptr;
    }
    return kBridgeFailure;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_gatherV4(
    JNIEnv* env, jobject, jlong token, jlongArray scalar_array, jobjectArray blobs,
    jintArray stun_port_array) {
    if (scalar_array == nullptr || blobs == nullptr || stun_port_array == nullptr ||
        env->GetArrayLength(scalar_array) != kGatherScalarCount) return kInvalid;
    const auto stun_count = env->GetArrayLength(stun_port_array);
    if (stun_count < 0 || stun_count > 2 || env->GetArrayLength(blobs) != 3 + stun_count) {
        return kInvalid;
    }
    std::array<jlong, kGatherScalarCount> scalars{};
    std::array<jint, 2> stun_ports{};
    env->GetLongArrayRegion(scalar_array, 0, kGatherScalarCount, scalars.data());
    if (stun_count > 0) env->GetIntArrayRegion(stun_port_array, 0, stun_count, stun_ports.data());
    if (env->ExceptionCheck() || scalars[0] <= 0 || scalars[1] == 0 ||
        scalars[2] < 0 || scalars[2] > 65535 ||
        (scalars[2] != 0 && scalars[2] < 1024) || scalars[3] < 0 || scalars[4] <= 0 ||
        scalars[5] < 100 || scalars[5] > 5000) return kInvalid;
    TermLinkDirectGatherV4 input{};
    input.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    input.struct_size = static_cast<std::uint32_t>(sizeof(input));
    input.epoch = static_cast<std::uint64_t>(scalars[0]);
    input.network_handle = static_cast<std::uint64_t>(scalars[1]);
    input.network_cost = static_cast<std::uint32_t>(scalars[3]);
    input.candidate_expires_at_ms = static_cast<std::int64_t>(scalars[4]);
    input.gather_timeout_ms = static_cast<std::uint32_t>(scalars[5]);
    if (!copy_exact_blob(env, blobs, 0, input.attempt_id, 16) ||
        !decode_blob_address(env, blobs, 1, static_cast<jint>(scalars[2]), input.local_bind) ||
        !copy_bounded_blob(env, blobs, 2, input.advertised_local_ip, 16,
                           input.advertised_local_ip_length) ||
        (input.advertised_local_ip_length != 4 && input.advertised_local_ip_length != 16)) {
        return kInvalid;
    }
    input.stun_count = static_cast<std::uint32_t>(stun_count);
    for (jsize index = 0; index < stun_count; ++index) {
        if (stun_ports[static_cast<std::size_t>(index)] < 1024 ||
            !decode_blob_address(env, blobs, 3 + index,
                                 stun_ports[static_cast<std::size_t>(index)],
                                 input.stun_servers[static_cast<std::size_t>(index)])) return kInvalid;
    }
    return with_v4_bridge(token, [&input](void* bridge) {
        return termlink_direct_gather_v4(bridge, &input);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_beginChecksV4(
    JNIEnv* env, jobject, jlong token, jlongArray scalar_array, jobjectArray blobs,
    jobjectArray encoded_pairs) {
    if (scalar_array == nullptr || blobs == nullptr || encoded_pairs == nullptr ||
        env->GetArrayLength(scalar_array) != kBeginScalarCount ||
        env->GetArrayLength(blobs) != kBeginBlobCount) return kInvalid;
    std::array<jlong, kBeginScalarCount> scalars{};
    env->GetLongArrayRegion(scalar_array, 0, kBeginScalarCount, scalars.data());
    if (env->ExceptionCheck() || scalars[0] <= 0 || scalars[1] == 0 || scalars[2] <= 0 ||
        scalars[3] < 500 || scalars[3] > 10000 || scalars[4] < 1 || scalars[4] > 2 ||
        scalars[5] != 1 || scalars[6] < 1 || scalars[6] > 32 ||
        scalars[7] < 0 || scalars[7] > 1 || scalars[8] < 1 || scalars[8] > 8) return kInvalid;
    if (scalars[6] != scalars[8]) return kInvalid;
    TermLinkDirectBeginChecksV4 input{};
    input.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    input.struct_size = static_cast<std::uint32_t>(sizeof(input));
    input.epoch = static_cast<std::uint64_t>(scalars[0]);
    input.network_handle = static_cast<std::uint64_t>(scalars[1]);
    input.now_ms = static_cast<std::int64_t>(scalars[2]);
    input.timeout_ms = static_cast<std::uint32_t>(scalars[3]);
    input.role = static_cast<std::uint32_t>(scalars[4]);
    input.pair_batch_seq = 1;
    input.total_pair_count = static_cast<std::uint32_t>(scalars[6]);
    input.plan_complete = static_cast<std::uint32_t>(scalars[7]);
    input.pair_count = static_cast<std::uint32_t>(scalars[8]);
    if (!copy_exact_blob(env, blobs, 0, input.attempt_id, 16) ||
        !copy_exact_blob(env, blobs, 1, input.connection_id, 16) ||
        !copy_exact_blob(env, blobs, 2, input.local_device_id, 16) ||
        !copy_exact_blob(env, blobs, 3, input.peer_device_id, 16) ||
        !copy_bounded_blob(env, blobs, 4, input.ticket, sizeof(input.ticket), input.ticket_length) ||
        input.ticket_length == 0 ||
        !copy_exact_blob(env, blobs, 5, input.server_signing_public_key, 32) ||
        !copy_exact_blob(env, blobs, 6, input.ticket_id, 16) ||
        !copy_exact_blob(env, blobs, 7, input.check_round_id, 16) ||
        !copy_exact_blob(env, blobs, 8, input.connectivity_nonce, 16) ||
        !copy_exact_blob(env, blobs, 9, input.local_send_probe_hmac_key, 32) ||
        !copy_exact_blob(env, blobs, 10, input.peer_receive_probe_hmac_key, 32) ||
        !nonzero_id(input.attempt_id) || !nonzero_id(input.connection_id) ||
        !nonzero_id(input.local_device_id) || !nonzero_id(input.peer_device_id) ||
        !nonzero_id(input.ticket_id) || !nonzero_id(input.check_round_id) ||
        !nonzero_id(input.connectivity_nonce) ||
        std::memcmp(input.local_device_id, input.peer_device_id, 16) == 0 ||
        all_zero(input.local_send_probe_hmac_key, 32) ||
        all_zero(input.peer_receive_probe_hmac_key, 32) ||
        std::memcmp(input.local_send_probe_hmac_key, input.peer_receive_probe_hmac_key, 32) == 0 ||
        !decode_pairs(env, encoded_pairs, input.pairs, input.pair_count)) return kInvalid;
    const jint result = with_v4_bridge(token, [&input](void* bridge) {
        return termlink_direct_begin_checks_v4(bridge, &input);
    });
    secure_zero(input.local_send_probe_hmac_key, sizeof(input.local_send_probe_hmac_key));
    secure_zero(input.peer_receive_probe_hmac_key, sizeof(input.peer_receive_probe_hmac_key));
    return result;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_appendCheckPairsV4(
    JNIEnv* env, jobject, jlong token, jlongArray scalar_array, jobjectArray blobs,
    jobjectArray encoded_pairs) {
    if (scalar_array == nullptr || blobs == nullptr || encoded_pairs == nullptr ||
        env->GetArrayLength(scalar_array) != kAppendScalarCount ||
        env->GetArrayLength(blobs) != kAppendBlobCount) return kInvalid;
    std::array<jlong, kAppendScalarCount> scalars{};
    env->GetLongArrayRegion(scalar_array, 0, kAppendScalarCount, scalars.data());
    if (env->ExceptionCheck() || scalars[0] <= 0 || scalars[1] <= 1 ||
        scalars[2] < 1 || scalars[2] > 32 || scalars[3] < 0 || scalars[3] > 1 ||
        scalars[4] < 0 || scalars[4] > 8 || (scalars[4] == 0 && scalars[3] == 0)) return kInvalid;
    TermLinkDirectAppendCheckPairsV4 input{};
    input.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    input.struct_size = static_cast<std::uint32_t>(sizeof(input));
    input.epoch = static_cast<std::uint64_t>(scalars[0]);
    input.pair_batch_seq = static_cast<std::uint32_t>(scalars[1]);
    input.total_pair_count = static_cast<std::uint32_t>(scalars[2]);
    input.plan_complete = static_cast<std::uint32_t>(scalars[3]);
    input.pair_count = static_cast<std::uint32_t>(scalars[4]);
    if (!copy_exact_blob(env, blobs, 0, input.attempt_id, 16) ||
        !copy_exact_blob(env, blobs, 1, input.check_round_id, 16) ||
        !decode_pairs(env, encoded_pairs, input.pairs, input.pair_count)) return kInvalid;
    return with_v4_bridge(token, [&input](void* bridge) {
        return termlink_direct_append_check_pairs_v4(bridge, &input);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_commitNominationV4(
    JNIEnv* env, jobject, jlong token, jlongArray scalar_array, jobjectArray blobs) {
    if (scalar_array == nullptr || blobs == nullptr ||
        env->GetArrayLength(scalar_array) != kCommitScalarCount ||
        env->GetArrayLength(blobs) != kCommitBlobCount) return kInvalid;
    std::array<jlong, kCommitScalarCount> scalars{};
    env->GetLongArrayRegion(scalar_array, 0, kCommitScalarCount, scalars.data());
    if (env->ExceptionCheck() || scalars[0] <= 0 || scalars[1] < 1 || scalars[1] > 2 ||
        scalars[2] < 500 || scalars[2] > 10000 || scalars[3] != 1 || scalars[4] <= 0) {
        return kInvalid;
    }
    TermLinkDirectCommitNominationV4 input{};
    input.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    input.struct_size = static_cast<std::uint32_t>(sizeof(input));
    input.epoch = static_cast<std::uint64_t>(scalars[0]);
    input.role = static_cast<std::uint32_t>(scalars[1]);
    input.timeout_ms = static_cast<std::uint32_t>(scalars[2]);
    input.selection_confirmed = 1;
    input.now_ms = static_cast<std::int64_t>(scalars[4]);
    jbyteArray pair = blob_at(env, blobs, 3);
    const bool decoded_pair = decode_pair(env, pair, input.winning_pair);
    if (pair != nullptr) env->DeleteLocalRef(pair);
    if (!copy_exact_blob(env, blobs, 0, input.attempt_id, 16) ||
        !copy_exact_blob(env, blobs, 1, input.check_round_id, 16) ||
        !copy_exact_blob(env, blobs, 2, input.nomination_id, 16) || !decoded_pair ||
        !copy_exact_blob(env, blobs, 4, input.peer_certificate_sha256, 32) ||
        !copy_exact_blob(env, blobs, 5, input.peer_device_signing_public_key, 32) ||
        !copy_bounded_blob(env, blobs, 6, input.local_proof_nonce,
                           sizeof(input.local_proof_nonce), input.local_proof_nonce_length) ||
        input.local_proof_nonce_length < 16) return kInvalid;
    return with_v4_bridge(token, [&input](void* bridge) {
        return termlink_direct_commit_nomination_v4(bridge, &input);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_cancelV4(
    JNIEnv* env, jobject, jlong token, jlong epoch, jbyteArray attempt_array) {
    std::array<std::uint8_t, 16> attempt{};
    if (epoch <= 0 || !copy_exact(env, attempt_array, attempt.data(), attempt.size())) return kInvalid;
    const auto control = control_record(epoch, attempt);
    return with_v4_bridge(token, [&control](void* bridge) {
        return termlink_direct_cancel_v4(bridge, &control);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_networkChangedV4(
    JNIEnv* env, jobject, jlong token, jlong epoch, jbyteArray attempt_array) {
    std::array<std::uint8_t, 16> attempt{};
    if (epoch <= 0 || !copy_exact(env, attempt_array, attempt.data(), attempt.size())) return kInvalid;
    const auto control = control_record(epoch, attempt);
    return with_v4_bridge(token, [&control](void* bridge) {
        return termlink_direct_network_changed_v4(bridge, &control);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_submitSignatureV4(
    JNIEnv* env, jobject, jlong token, jlong epoch, jbyteArray attempt_array,
    jlong request_id, jbyteArray signature_array) {
    if (epoch <= 0 || request_id <= 0) return kInvalid;
    TermLinkDirectSignatureV4 input{};
    input.abi_version = TERMLINK_DIRECT_ABI_VERSION_V4;
    input.struct_size = static_cast<std::uint32_t>(sizeof(input));
    input.epoch = static_cast<std::uint64_t>(epoch);
    input.request_id = static_cast<std::uint64_t>(request_id);
    if (!copy_exact(env, attempt_array, input.attempt_id, 16) ||
        !copy_bounded(env, signature_array, input.signature, sizeof(input.signature),
                      input.signature_length) || input.signature_length == 0) return kInvalid;
    return with_v4_bridge(token, [&input](void* bridge) {
        return termlink_direct_submit_signature_v4(bridge, &input);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_pollV4(
    JNIEnv* env, jobject, jlong token, jlongArray metadata_array, jbyteArray attempt_array,
    jbyteArray endpoint_ip_array, jbyteArray payload_array) {
    if (metadata_array == nullptr || env->GetArrayLength(metadata_array) != kPollMetadataCount ||
        attempt_array == nullptr || env->GetArrayLength(attempt_array) != 16 ||
        endpoint_ip_array == nullptr || env->GetArrayLength(endpoint_ip_array) != 16 ||
        payload_array == nullptr || env->GetArrayLength(payload_array) !=
            static_cast<jsize>(sizeof(TermLinkDirectEventPayloadV4))) return kInvalid;
    TermLinkDirectEventV4 event{};
    const jint status = with_v4_bridge(token, [&event](void* bridge) {
        return termlink_direct_poll_event_v4(bridge, &event);
    });
    if (status != 1) return status;
    if (!event_valid(event)) return kBridgeFailure;
    const std::array<jlong, kPollMetadataCount> metadata{
        event.abi_version, event.struct_size, event.kind, event.state, event.reason,
        event.payload_size, static_cast<jlong>(event.epoch),
        static_cast<jlong>(event.generation), static_cast<jlong>(event.request_id),
        event.endpoint.ip_length, event.endpoint.port,
    };
    env->SetLongArrayRegion(metadata_array, 0, kPollMetadataCount, metadata.data());
    env->SetByteArrayRegion(attempt_array, 0, 16,
                            reinterpret_cast<const jbyte*>(event.attempt_id));
    env->SetByteArrayRegion(endpoint_ip_array, 0, 16,
                            reinterpret_cast<const jbyte*>(event.endpoint.ip));
    if (event.payload_size > 0) {
        env->SetByteArrayRegion(payload_array, 0, static_cast<jsize>(event.payload_size),
                                reinterpret_cast<const jbyte*>(event.payload.raw));
    }
    return env->ExceptionCheck() ? kBridgeFailure : 1;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV4_destroyV4(
    JNIEnv*, jobject, jlong token) {
    auto holder = remove_v4_holder(token);
    if (!holder) return kInvalid;
    std::lock_guard lock(holder->mutex);
    if (holder->bridge == nullptr) return kInvalid;
    termlink_direct_destroy_v4(holder->bridge);
    holder->bridge = nullptr;
    return kOk;
}
