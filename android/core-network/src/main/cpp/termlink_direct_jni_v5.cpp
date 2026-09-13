#include <jni.h>

#include <array>
#include <atomic>
#include <climits>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <type_traits>
#include <unordered_map>
#include <vector>

#include "termlink_direct_ffi_v5.h"
#include "termlink_direct_jni_v4_registry.h"

namespace {

constexpr jint kOk = TERMLINK_DIRECT_V5_OK;
constexpr jint kInvalid = TERMLINK_DIRECT_V5_ERR_INVALID_ARGUMENT;
constexpr jsize kReadMetadataCount = 2;
constexpr jsize kRequiredBytesCount = 1;
constexpr jsize kAbiContractCount = 41;

static_assert(sizeof(TermLinkDirectPathV5) == 64);
static_assert(sizeof(TermLinkDirectCreateV5) == 112);
static_assert(sizeof(TermLinkDirectEventV5) == 80);
static_assert(sizeof(TermLinkSocketAddrV5) == 48);
static_assert(sizeof(TermLinkDirectCandidateV5) == 128);
static_assert(sizeof(TermLinkDirectCandidatePairV5) == 288);
static_assert(sizeof(TermLinkDirectPromoteV5) == 544);
static_assert(sizeof(TermLinkDirectPathInfoV5) == 128);
static_assert(sizeof(TermLinkDirectSignatureV5) == 232);
static_assert(sizeof(TermLinkDirectSessionBarrierV5) == 24);
static_assert(sizeof(TermLinkDirectProposalV5) == 968);
static_assert(sizeof(TermLinkDirectTransitionV5) == 176);
static_assert(sizeof(TermLinkDirectCursorV5) == 120);
static_assert(sizeof(TermLinkDirectRouteEnvelopeV5) == 128);
static_assert(sizeof(TermLinkDirectIngressV5) == 136);
static_assert(sizeof(TermLinkDirectRouteActionV5) == 200);

struct BridgeHolderV5 {
    explicit BridgeHolderV5(void* value) : bridge(value) {}
    ~BridgeHolderV5() {
        if (bridge != nullptr) termlink_direct_destroy_v5(bridge);
    }
    std::mutex mutex;
    void* bridge;
};

std::mutex g_v5_registry_mutex;
std::unordered_map<std::uint64_t, std::shared_ptr<BridgeHolderV5>> g_v5_registry;
std::atomic<std::uint64_t> g_v5_next_token{1};

std::shared_ptr<BridgeHolderV5> find_v5_holder(jlong raw_token) {
    if (raw_token <= 0) return {};
    std::lock_guard lock(g_v5_registry_mutex);
    const auto found = g_v5_registry.find(static_cast<std::uint64_t>(raw_token));
    return found == g_v5_registry.end() ? std::shared_ptr<BridgeHolderV5>{} : found->second;
}

std::uint64_t register_v5_holder(std::shared_ptr<BridgeHolderV5> holder) {
    for (;;) {
        const auto token = g_v5_next_token.fetch_add(1, std::memory_order_relaxed);
        if (token == 0 || token > static_cast<std::uint64_t>(INT64_MAX)) continue;
        std::lock_guard lock(g_v5_registry_mutex);
        if (g_v5_registry.emplace(token, holder).second) return token;
    }
}

std::shared_ptr<BridgeHolderV5> remove_v5_holder(jlong raw_token) {
    if (raw_token <= 0) return {};
    std::lock_guard lock(g_v5_registry_mutex);
    const auto found = g_v5_registry.find(static_cast<std::uint64_t>(raw_token));
    if (found == g_v5_registry.end()) return {};
    auto holder = found->second;
    g_v5_registry.erase(found);
    return holder;
}

template <typename Operation>
jint with_v5_bridge(jlong token, Operation&& operation) {
    auto holder = find_v5_holder(token);
    if (!holder) return TERMLINK_DIRECT_V5_ERR_INVALID_HANDLE;
    std::lock_guard lock(holder->mutex);
    if (holder->bridge == nullptr) return TERMLINK_DIRECT_V5_ERR_INVALID_HANDLE;
    return static_cast<jint>(operation(holder->bridge));
}

void secure_zero(void* value, std::size_t size) {
    auto* output = static_cast<volatile std::uint8_t*>(value);
    for (std::size_t index = 0; index < size; ++index) output[index] = 0;
}

class SecureZeroGuard final {
public:
    SecureZeroGuard(void* value, std::size_t size) noexcept : value_(value), size_(size) {}
    ~SecureZeroGuard() noexcept {
        if (value_ != nullptr && size_ != 0) secure_zero(value_, size_);
    }

    SecureZeroGuard(const SecureZeroGuard&) = delete;
    SecureZeroGuard& operator=(const SecureZeroGuard&) = delete;

private:
    void* value_;
    std::size_t size_;
};

class SecureZeroVectorGuard final {
public:
    explicit SecureZeroVectorGuard(std::vector<std::uint8_t>& value) noexcept : value_(value) {}
    ~SecureZeroVectorGuard() noexcept {
        if (!value_.empty()) secure_zero(value_.data(), value_.size());
    }

    SecureZeroVectorGuard(const SecureZeroVectorGuard&) = delete;
    SecureZeroVectorGuard& operator=(const SecureZeroVectorGuard&) = delete;

private:
    std::vector<std::uint8_t>& value_;
};

bool exact_length(JNIEnv* env, jarray value, jsize expected) {
    return value != nullptr && env->GetArrayLength(value) == expected;
}

template <typename Record>
bool copy_record(JNIEnv* env, jbyteArray source, Record& output) {
    static_assert(std::is_trivially_copyable_v<Record>);
    if (!exact_length(env, source, static_cast<jsize>(sizeof(Record)))) return false;
    env->GetByteArrayRegion(source, 0, static_cast<jsize>(sizeof(Record)),
                            reinterpret_cast<jbyte*>(&output));
    return !env->ExceptionCheck();
}

template <typename Record>
bool write_record(JNIEnv* env, jbyteArray output, const Record& value) {
    if (!exact_length(env, output, static_cast<jsize>(sizeof(Record)))) return false;
    env->SetByteArrayRegion(output, 0, static_cast<jsize>(sizeof(Record)),
                            reinterpret_cast<const jbyte*>(&value));
    return !env->ExceptionCheck();
}

bool copy_owned_bytes(JNIEnv* env, jbyteArray source, std::vector<std::uint8_t>& output,
                      std::size_t maximum, bool allow_empty = false) {
    if (source == nullptr) return false;
    const auto size = env->GetArrayLength(source);
    if (size < 0 || (!allow_empty && size == 0) || static_cast<std::size_t>(size) > maximum) {
        return false;
    }
    output.resize(static_cast<std::size_t>(size));
    if (size > 0) {
        env->GetByteArrayRegion(source, 0, size, reinterpret_cast<jbyte*>(output.data()));
    }
    return !env->ExceptionCheck();
}

template <typename Record, typename Operation>
jint record_operation(JNIEnv* env, jlong handle, jbyteArray encoded, Operation&& operation) {
    Record record{};
    if (!copy_record(env, encoded, record)) return kInvalid;
    return with_v5_bridge(handle, [&](void* bridge) { return operation(bridge, &record); });
}

template <typename Operation>
jint read_bytes(JNIEnv* env, jlong handle, jbyteArray path_array, jlongArray metadata,
                jbyteArray output, Operation&& operation) {
    TermLinkDirectPathV5 path{};
    if (!copy_record(env, path_array, path) || !exact_length(env, metadata, kReadMetadataCount) ||
        output == nullptr) return kInvalid;
    const auto capacity = env->GetArrayLength(output);
    if (capacity < 0 ||
        static_cast<std::uint64_t>(capacity) > TERMLINK_DIRECT_MAX_ENVELOPE_BYTES_V5) {
        return kInvalid;
    }
    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(capacity));
    SecureZeroVectorGuard bytes_guard(bytes);
    std::uint64_t first = 0;
    std::uint32_t required = 0;
    const auto status = with_v5_bridge(handle, [&](void* bridge) {
        return operation(bridge, &path, &first, bytes.empty() ? nullptr : bytes.data(),
                         static_cast<std::uint32_t>(bytes.size()), &required);
    });
    const std::array<jlong, kReadMetadataCount> values{
        static_cast<jlong>(first), static_cast<jlong>(required)};
    env->SetLongArrayRegion(metadata, 0, kReadMetadataCount, values.data());
    if (status == TERMLINK_DIRECT_V5_OK && required > 0 && required <= bytes.size() &&
        !env->ExceptionCheck()) {
        env->SetByteArrayRegion(output, 0, static_cast<jsize>(required),
                                reinterpret_cast<const jbyte*>(bytes.data()));
    }
    return env->ExceptionCheck() ? kInvalid : status;
}

}  // namespace

extern "C" JNIEXPORT jlongArray JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_abiContractV5(
    JNIEnv* env, jobject) {
    const std::array<jlong, kAbiContractCount> values{
        static_cast<jlong>(termlink_direct_abi_version_v5()),
        static_cast<jlong>(TERMLINK_DIRECT_MAX_FRAMES_V5),
        static_cast<jlong>(TERMLINK_DIRECT_MAX_BYTES_V5),
        static_cast<jlong>(TERMLINK_DIRECT_MAX_ENVELOPE_BYTES_V5),
        static_cast<jlong>(TERMLINK_DIRECT_FAILURE_ACTION_TAIL_V5),
        static_cast<jlong>(TERMLINK_DIRECT_MAX_RETAINED_FRAMES_V5),
        static_cast<jlong>(TERMLINK_DIRECT_FIRST_DATA_FRAME_SEQ_V5),
        static_cast<jlong>(TERMLINK_DIRECT_MAX_FRAME_SEQ_V5),
        static_cast<jlong>(TERMLINK_DIRECT_MAX_APPLIED_CURSORS_PER_DIRECTION_V5),
        static_cast<jlong>(sizeof(TermLinkDirectPathV5)),
        static_cast<jlong>(sizeof(TermLinkDirectCreateV5)),
        static_cast<jlong>(sizeof(TermLinkDirectEventV5)),
        static_cast<jlong>(sizeof(TermLinkSocketAddrV5)),
        static_cast<jlong>(sizeof(TermLinkDirectCandidateV5)),
        static_cast<jlong>(sizeof(TermLinkDirectCandidatePairV5)),
        static_cast<jlong>(sizeof(TermLinkDirectPromoteV5)),
        static_cast<jlong>(sizeof(TermLinkDirectPathInfoV5)),
        static_cast<jlong>(sizeof(TermLinkDirectSignatureV5)),
        static_cast<jlong>(sizeof(TermLinkDirectSessionBarrierV5)),
        static_cast<jlong>(sizeof(TermLinkDirectProposalV5)),
        static_cast<jlong>(sizeof(TermLinkDirectTransitionV5)),
        static_cast<jlong>(sizeof(TermLinkDirectCursorV5)),
        static_cast<jlong>(sizeof(TermLinkDirectRouteEnvelopeV5)),
        static_cast<jlong>(sizeof(TermLinkDirectIngressV5)),
        static_cast<jlong>(sizeof(TermLinkDirectRouteActionV5)),
        static_cast<jlong>(alignof(TermLinkDirectPathV5)),
        static_cast<jlong>(alignof(TermLinkDirectCreateV5)),
        static_cast<jlong>(alignof(TermLinkDirectEventV5)),
        static_cast<jlong>(alignof(TermLinkSocketAddrV5)),
        static_cast<jlong>(alignof(TermLinkDirectCandidateV5)),
        static_cast<jlong>(alignof(TermLinkDirectCandidatePairV5)),
        static_cast<jlong>(alignof(TermLinkDirectPromoteV5)),
        static_cast<jlong>(alignof(TermLinkDirectPathInfoV5)),
        static_cast<jlong>(alignof(TermLinkDirectSignatureV5)),
        static_cast<jlong>(alignof(TermLinkDirectSessionBarrierV5)),
        static_cast<jlong>(alignof(TermLinkDirectProposalV5)),
        static_cast<jlong>(alignof(TermLinkDirectTransitionV5)),
        static_cast<jlong>(alignof(TermLinkDirectCursorV5)),
        static_cast<jlong>(alignof(TermLinkDirectRouteEnvelopeV5)),
        static_cast<jlong>(alignof(TermLinkDirectIngressV5)),
        static_cast<jlong>(alignof(TermLinkDirectRouteActionV5)),
    };
    jlongArray output = env->NewLongArray(kAbiContractCount);
    if (output != nullptr) env->SetLongArrayRegion(output, 0, kAbiContractCount, values.data());
    return output;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_createV5(
    JNIEnv* env, jobject, jboolean enabled, jint queue_capacity_frames,
    jlong queue_capacity_bytes, jbyteArray path_array, jlongArray out_handle) {
    TermLinkDirectPathV5 path{};
    if (!copy_record(env, path_array, path) || !exact_length(env, out_handle, 1) ||
        queue_capacity_frames <= 0 ||
        queue_capacity_frames > static_cast<jint>(TERMLINK_DIRECT_MAX_FRAMES_V5) ||
        queue_capacity_bytes <= 0 ||
        static_cast<std::uint64_t>(queue_capacity_bytes) > TERMLINK_DIRECT_MAX_BYTES_V5) {
        return kInvalid;
    }
    TermLinkDirectCreateV5 config{};
    config.abi_version = TERMLINK_DIRECT_ABI_VERSION_V5;
    config.struct_size = static_cast<std::uint32_t>(sizeof(config));
    config.enabled = enabled == JNI_TRUE ? 1U : 0U;
    config.queue_capacity_frames = static_cast<std::uint32_t>(queue_capacity_frames);
    config.queue_capacity_bytes = static_cast<std::uint64_t>(queue_capacity_bytes);
    config.path = path;
    void* bridge = termlink_direct_create_v5(&config);
    if (bridge == nullptr) return TERMLINK_DIRECT_V5_ERR_INVALID_ARGUMENT;
    const auto token = register_v5_holder(std::make_shared<BridgeHolderV5>(bridge));
    const jlong encoded = static_cast<jlong>(token);
    env->SetLongArrayRegion(out_handle, 0, 1, &encoded);
    if (!env->ExceptionCheck()) return kOk;
    (void)remove_v5_holder(encoded);
    return kInvalid;
}

#define TERMLINK_JNI_V5_PATH_OPERATION(method_name, ffi_name)                 \
extern "C" JNIEXPORT jint JNICALL                                             \
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_##method_name(\
    JNIEnv* env, jobject, jlong handle, jbyteArray path_array) {               \
    return record_operation<TermLinkDirectPathV5>(                             \
        env, handle, path_array, [](void* bridge, const TermLinkDirectPathV5* value) {\
            return ffi_name(bridge, value);                                    \
        });                                                                    \
}

TERMLINK_JNI_V5_PATH_OPERATION(pathUsageV5, termlink_direct_path_usage_v5)
TERMLINK_JNI_V5_PATH_OPERATION(attachV5, termlink_direct_attach_v5)
TERMLINK_JNI_V5_PATH_OPERATION(cancelV5, termlink_direct_cancel_v5)
TERMLINK_JNI_V5_PATH_OPERATION(networkChangedV5, termlink_direct_network_changed_v5)

#undef TERMLINK_JNI_V5_PATH_OPERATION

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_promoteV5(
    JNIEnv* env, jobject, jlong handle, jlong v4_handle, jbyteArray promote_array) {
    TermLinkDirectPromoteV5 promote{};
    SecureZeroGuard promote_guard(&promote, sizeof(promote));
    if (!copy_record(env, promote_array, promote) || v4_handle <= 0) return kInvalid;
    const auto status = with_v5_bridge(handle, [&](void* bridge) {
        return termlink_direct_jni_promote_v4_token(
            static_cast<std::int64_t>(v4_handle), bridge, &promote);
    });
    return status;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_submitSignatureV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray signature_array) {
    TermLinkDirectSignatureV5 signature{};
    SecureZeroGuard signature_guard(&signature, sizeof(signature));
    if (!copy_record(env, signature_array, signature)) return kInvalid;
    const auto status = with_v5_bridge(handle, [&](void* bridge) {
        return termlink_direct_submit_signature_v5(bridge, &signature);
    });
    return status;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_receiveSigningPayloadV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray path_array, jlongArray metadata,
    jbyteArray output) {
    return read_bytes(
        env, handle, path_array, metadata, output,
        [](void* bridge, const TermLinkDirectPathV5* path, std::uint64_t* request_id,
           std::uint8_t* bytes, std::uint32_t capacity, std::uint32_t* required) {
            return termlink_direct_receive_signing_payload_v5(
                bridge, path, request_id, bytes, capacity, required);
        });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_pathInfoV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray path_array, jbyteArray output) {
    TermLinkDirectPathV5 path{};
    if (!copy_record(env, path_array, path) ||
        !exact_length(env, output, static_cast<jsize>(sizeof(TermLinkDirectPathInfoV5)))) {
        return kInvalid;
    }
    TermLinkDirectPathInfoV5 info{};
    const auto status = with_v5_bridge(handle, [&](void* bridge) {
        return termlink_direct_path_info_v5(bridge, &path, &info);
    });
    if (status == TERMLINK_DIRECT_V5_OK && !write_record(env, output, info)) return kInvalid;
    return status;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_receiveEnvelopeV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray path_array, jlongArray metadata,
    jbyteArray output) {
    return read_bytes(
        env, handle, path_array, metadata, output,
        [](void* bridge, const TermLinkDirectPathV5* path, std::uint64_t* frame_sequence,
           std::uint8_t* bytes, std::uint32_t capacity, std::uint32_t* required) {
            return termlink_direct_receive_envelope_v5(
                bridge, path, frame_sequence, bytes, capacity, required);
        });
}

#define TERMLINK_JNI_V5_PROPOSAL_OPERATION(method_name, ffi_name)             \
extern "C" JNIEXPORT jint JNICALL                                             \
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_##method_name(\
    JNIEnv* env, jobject, jlong handle, jbyteArray record) {                   \
    return record_operation<TermLinkDirectProposalV5>(                         \
        env, handle, record, [](void* bridge, const TermLinkDirectProposalV5* value) {\
            return ffi_name(bridge, value);                                    \
        });                                                                    \
}

TERMLINK_JNI_V5_PROPOSAL_OPERATION(startProposalV5, termlink_direct_start_proposal_v5)
TERMLINK_JNI_V5_PROPOSAL_OPERATION(receiveProposalV5, termlink_direct_receive_proposal_v5)

#undef TERMLINK_JNI_V5_PROPOSAL_OPERATION

#define TERMLINK_JNI_V5_TRANSITION_OPERATION(method_name, ffi_name)           \
extern "C" JNIEXPORT jint JNICALL                                             \
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_##method_name(\
    JNIEnv* env, jobject, jlong handle, jbyteArray record) {                   \
    return record_operation<TermLinkDirectTransitionV5>(                       \
        env, handle, record, [](void* bridge, const TermLinkDirectTransitionV5* value) {\
            return ffi_name(bridge, value);                                    \
        });                                                                    \
}

TERMLINK_JNI_V5_TRANSITION_OPERATION(receiveAcceptV5, termlink_direct_receive_accept_v5)
TERMLINK_JNI_V5_TRANSITION_OPERATION(receiveCommitV5, termlink_direct_receive_commit_v5)
TERMLINK_JNI_V5_TRANSITION_OPERATION(commitEchoSentV5, termlink_direct_commit_echo_sent_v5)
TERMLINK_JNI_V5_TRANSITION_OPERATION(receiveFallbackV5, termlink_direct_receive_fallback_v5)
TERMLINK_JNI_V5_TRANSITION_OPERATION(tickV5, termlink_direct_tick_v5)

#undef TERMLINK_JNI_V5_TRANSITION_OPERATION

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_updateAppliedCursorV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray cursor) {
    return record_operation<TermLinkDirectCursorV5>(
        env, handle, cursor, [](void* bridge, const TermLinkDirectCursorV5* value) {
            return termlink_direct_update_applied_cursor_v5(bridge, value);
        });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_deregisterSessionV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray session) {
    return record_operation<TermLinkDirectCursorV5>(
        env, handle, session, [](void* bridge, const TermLinkDirectCursorV5* value) {
            return termlink_direct_deregister_session_v5(bridge, value);
        });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_routeEnvelopeV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray route_array, jbyteArray envelope_array) {
    TermLinkDirectRouteEnvelopeV5 route{};
    std::vector<std::uint8_t> envelope;
    SecureZeroVectorGuard envelope_guard(envelope);
    if (!copy_record(env, route_array, route) ||
        !copy_owned_bytes(env, envelope_array, envelope,
                          TERMLINK_DIRECT_MAX_ENVELOPE_BYTES_V5)) return kInvalid;
    const auto status = with_v5_bridge(handle, [&](void* bridge) {
        return termlink_direct_route_envelope_v5(
            bridge, &route, envelope.data(), static_cast<std::uint32_t>(envelope.size()));
    });
    return status;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_pollRouteActionV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray action_array, jbyteArray output,
    jlongArray required_array) {
    if (!exact_length(env, action_array, static_cast<jsize>(sizeof(TermLinkDirectRouteActionV5))) ||
        output == nullptr || !exact_length(env, required_array, kRequiredBytesCount)) return kInvalid;
    const auto capacity = env->GetArrayLength(output);
    if (capacity < 0 ||
        static_cast<std::uint64_t>(capacity) > TERMLINK_DIRECT_MAX_ENVELOPE_BYTES_V5) {
        return kInvalid;
    }
    TermLinkDirectRouteActionV5 action{};
    std::vector<std::uint8_t> payload(static_cast<std::size_t>(capacity));
    SecureZeroVectorGuard payload_guard(payload);
    std::uint32_t required = 0;
    const auto status = with_v5_bridge(handle, [&](void* bridge) {
        return termlink_direct_poll_route_action_v5(
            bridge, &action, payload.empty() ? nullptr : payload.data(),
            static_cast<std::uint32_t>(payload.size()), &required);
    });
    const jlong required_long = static_cast<jlong>(required);
    env->SetLongArrayRegion(required_array, 0, 1, &required_long);
    if (status == TERMLINK_DIRECT_V5_OK && !env->ExceptionCheck()) {
        if (!write_record(env, action_array, action)) return kInvalid;
        if (required > 0 && required <= payload.size()) {
            env->SetByteArrayRegion(output, 0, static_cast<jsize>(required),
                                    reinterpret_cast<const jbyte*>(payload.data()));
        }
    }
    return env->ExceptionCheck() ? kInvalid : status;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_arbitrateIngressV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray ingress_array, jbyteArray envelope_array) {
    TermLinkDirectIngressV5 ingress{};
    std::vector<std::uint8_t> envelope;
    SecureZeroVectorGuard envelope_guard(envelope);
    if (!copy_record(env, ingress_array, ingress) ||
        !copy_owned_bytes(env, envelope_array, envelope,
                          TERMLINK_DIRECT_MAX_ENVELOPE_BYTES_V5)) return kInvalid;
    const auto status = with_v5_bridge(handle, [&](void* bridge) {
        return termlink_direct_arbitrate_ingress_v5(
            bridge, &ingress, envelope.data(), static_cast<std::uint32_t>(envelope.size()));
    });
    return status;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_tickIngressV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray path_array, jlong now_millis) {
    TermLinkDirectPathV5 path{};
    if (!copy_record(env, path_array, path) || now_millis < 0) return kInvalid;
    return with_v5_bridge(handle, [&](void* bridge) {
        return termlink_direct_tick_ingress_v5(
            bridge, &path, static_cast<std::uint64_t>(now_millis));
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_pollEventV5(
    JNIEnv* env, jobject, jlong handle, jbyteArray output) {
    if (!exact_length(env, output, static_cast<jsize>(sizeof(TermLinkDirectEventV5)))) {
        return kInvalid;
    }
    TermLinkDirectEventV5 event{};
    const auto status = with_v5_bridge(handle, [&](void* bridge) {
        return termlink_direct_poll_event_v5(bridge, &event);
    });
    if (status == 1 && !write_record(env, output, event)) return kInvalid;
    return status;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethodsV5_destroyV5(
    JNIEnv*, jobject, jlong handle) {
    auto holder = remove_v5_holder(handle);
    if (!holder) return TERMLINK_DIRECT_V5_ERR_INVALID_HANDLE;
    std::lock_guard lock(holder->mutex);
    if (holder->bridge == nullptr) return TERMLINK_DIRECT_V5_ERR_INVALID_HANDLE;
    termlink_direct_destroy_v5(holder->bridge);
    holder->bridge = nullptr;
    return kOk;
}
