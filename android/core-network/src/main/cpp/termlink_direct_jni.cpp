#include <jni.h>

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <utility>
#include <vector>

#include "termlink_direct_ffi_v3.h"

namespace {

constexpr jint kOk = 0;
constexpr jint kInvalid = -2;
constexpr jsize kGatherScalarCount = 4;
constexpr jsize kFixedGatherBlobCount = 3;
constexpr jsize kNominateScalarCount = 7;
constexpr jsize kFixedNominateBlobCount = 19;
constexpr jsize kPollMetadataCount = 11;

static_assert(sizeof(TermLinkDirectCreateV3) == 32);
static_assert(sizeof(TermLinkSocketAddrV2) == 24);
static_assert(sizeof(TermLinkDirectGatherV3) == 176);
static_assert(sizeof(TermLinkDirectNominateV3) == 2696);
static_assert(sizeof(TermLinkDirectCandidatesV3) == 80);
static_assert(sizeof(TermLinkDirectControlV2) == 32);
static_assert(sizeof(TermLinkDirectSignatureV2) == 176);
static_assert(sizeof(TermLinkDirectStartV2) == 2592);
static_assert(sizeof(TermLinkDirectEventV2) == 2136);
static_assert(offsetof(TermLinkDirectGatherV3, local_bind) == 40);
static_assert(offsetof(TermLinkDirectGatherV3, stun_servers) == 88);
static_assert(offsetof(TermLinkDirectGatherV3, reserved) == 144);
static_assert(offsetof(TermLinkDirectNominateV3, start_v2) == 8);
static_assert(offsetof(TermLinkDirectNominateV3, local_send_probe_hmac_key) == 2600);
static_assert(offsetof(TermLinkDirectNominateV3, peer_receive_probe_hmac_key) == 2632);
static_assert(offsetof(TermLinkDirectNominateV3, reserved) == 2664);
static_assert(offsetof(TermLinkDirectCandidatesV3, candidates) == 8);
static_assert(offsetof(TermLinkDirectStartV2, ticket) == 208);
static_assert(offsetof(TermLinkDirectStartV2, peer_certificate_sha256) == 2288);
static_assert(offsetof(TermLinkDirectStartV2, now_ms) == 2552);
static_assert(offsetof(TermLinkDirectEventV2, generation) == 32);
static_assert(offsetof(TermLinkDirectEventV2, payload) == 88);

struct BridgeHolder {
    explicit BridgeHolder(void* value) : bridge(value) {}
    std::mutex mutex;
    void* bridge;
};

std::mutex g_registry_mutex;
std::unordered_map<std::uint64_t, std::shared_ptr<BridgeHolder>> g_registry;
std::atomic<std::uint64_t> g_next_token{1};

std::shared_ptr<BridgeHolder> find_holder(jlong raw_token) {
    if (raw_token <= 0) return {};
    const auto token = static_cast<std::uint64_t>(raw_token);
    std::lock_guard lock(g_registry_mutex);
    const auto found = g_registry.find(token);
    return found == g_registry.end() ? std::shared_ptr<BridgeHolder>{} : found->second;
}

std::uint64_t register_holder(std::shared_ptr<BridgeHolder> holder) {
    for (;;) {
        const auto token = g_next_token.fetch_add(1, std::memory_order_relaxed);
        if (token == 0 || token > static_cast<std::uint64_t>(INT64_MAX)) continue;
        std::lock_guard lock(g_registry_mutex);
        if (g_registry.emplace(token, holder).second) return token;
    }
}

std::shared_ptr<BridgeHolder> remove_holder(jlong raw_token) {
    if (raw_token <= 0) return {};
    const auto token = static_cast<std::uint64_t>(raw_token);
    std::lock_guard lock(g_registry_mutex);
    const auto found = g_registry.find(token);
    if (found == g_registry.end()) return {};
    auto holder = found->second;
    g_registry.erase(found);
    return holder;
}

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

bool copy_bounded_blob(JNIEnv* env, jobjectArray blobs, jsize index, std::uint8_t* destination,
                       std::size_t capacity, std::uint32_t& length) {
    jbyteArray blob = blob_at(env, blobs, index);
    const bool copied = copy_bounded(env, blob, destination, capacity, length);
    if (blob != nullptr) env->DeleteLocalRef(blob);
    return copied;
}

bool decode_address(JNIEnv* env, jbyteArray ip, jlong port, TermLinkSocketAddrV2& address) {
    if (ip == nullptr || port < 0 || port > 65535) return false;
    const auto length = env->GetArrayLength(ip);
    if (length != 4 && length != 16) return false;
    address = {};
    address.ip_length = static_cast<std::uint32_t>(length);
    address.port = static_cast<std::uint32_t>(port);
    env->GetByteArrayRegion(ip, 0, length, reinterpret_cast<jbyte*>(address.ip));
    return !env->ExceptionCheck();
}

bool decode_blob_address(JNIEnv* env, jobjectArray blobs, jsize index, jint port,
                         TermLinkSocketAddrV2& address) {
    jbyteArray ip = blob_at(env, blobs, index);
    const bool decoded = decode_address(env, ip, port, address);
    if (ip != nullptr) env->DeleteLocalRef(ip);
    return decoded;
}

TermLinkDirectControlV2 control_record(jlong epoch,
                                       const std::array<std::uint8_t, 16>& attempt_id) {
    TermLinkDirectControlV2 control{};
    control.abi_version = TERMLINK_DIRECT_ABI_VERSION_V3;
    control.struct_size = static_cast<std::uint32_t>(sizeof(control));
    control.epoch = static_cast<std::uint64_t>(epoch);
    std::memcpy(control.attempt_id, attempt_id.data(), attempt_id.size());
    return control;
}

template <typename Operation>
jint with_bridge(jlong token, Operation&& operation) {
    auto holder = find_holder(token);
    if (!holder) return kInvalid;
    std::lock_guard lock(holder->mutex);
    if (holder->bridge == nullptr) return kInvalid;
    return static_cast<jint>(operation(holder->bridge));
}

}  // namespace

extern "C" JNIEXPORT jlongArray JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethods_abiContract(
    JNIEnv* env, jobject) {
    const std::array<jlong, 22> values{
        static_cast<jlong>(termlink_direct_abi_version_v3()),
        static_cast<jlong>(sizeof(TermLinkDirectCreateV3)),
        static_cast<jlong>(sizeof(TermLinkSocketAddrV2)),
        static_cast<jlong>(sizeof(TermLinkDirectGatherV3)),
        static_cast<jlong>(sizeof(TermLinkDirectNominateV3)),
        static_cast<jlong>(sizeof(TermLinkDirectCandidatesV3)),
        static_cast<jlong>(sizeof(TermLinkDirectControlV2)),
        static_cast<jlong>(sizeof(TermLinkDirectSignatureV2)),
        static_cast<jlong>(sizeof(TermLinkDirectEventV2)),
        static_cast<jlong>(offsetof(TermLinkDirectGatherV3, local_bind)),
        static_cast<jlong>(offsetof(TermLinkDirectGatherV3, stun_servers)),
        static_cast<jlong>(offsetof(TermLinkDirectGatherV3, reserved)),
        static_cast<jlong>(offsetof(TermLinkDirectNominateV3, start_v2)),
        static_cast<jlong>(offsetof(TermLinkDirectNominateV3,
                                   local_send_probe_hmac_key)),
        static_cast<jlong>(offsetof(TermLinkDirectNominateV3,
                                   peer_receive_probe_hmac_key)),
        static_cast<jlong>(offsetof(TermLinkDirectNominateV3, reserved)),
        static_cast<jlong>(offsetof(TermLinkDirectCandidatesV3, candidates)),
        static_cast<jlong>(offsetof(TermLinkDirectStartV2, ticket)),
        static_cast<jlong>(offsetof(TermLinkDirectStartV2, peer_certificate_sha256)),
        static_cast<jlong>(offsetof(TermLinkDirectStartV2, now_ms)),
        static_cast<jlong>(offsetof(TermLinkDirectEventV2, generation)),
        static_cast<jlong>(offsetof(TermLinkDirectEventV2, payload)),
    };
    jlongArray result = env->NewLongArray(static_cast<jsize>(values.size()));
    if (result != nullptr) {
        env->SetLongArrayRegion(result, 0, static_cast<jsize>(values.size()), values.data());
    }
    return result;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethods_create(
    JNIEnv* env, jobject, jboolean enabled, jint queue_capacity, jlongArray out_handle) {
    if (out_handle == nullptr || env->GetArrayLength(out_handle) != 1 ||
        queue_capacity <= 0 || queue_capacity > 64) {
        return kInvalid;
    }
    TermLinkDirectCreateV3 config{};
    config.abi_version = TERMLINK_DIRECT_ABI_VERSION_V3;
    config.struct_size = static_cast<std::uint32_t>(sizeof(config));
    config.enabled = enabled == JNI_TRUE ? 1U : 0U;
    config.queue_capacity = static_cast<std::uint32_t>(queue_capacity);
    config.platform = TERMLINK_DIRECT_PLATFORM_ANDROID_V2;
    void* bridge = termlink_direct_create_v3(&config);
    if (bridge == nullptr) return -1;
    const auto token = register_holder(std::make_shared<BridgeHolder>(bridge));
    const jlong encoded = static_cast<jlong>(token);
    env->SetLongArrayRegion(out_handle, 0, 1, &encoded);
    if (env->ExceptionCheck()) {
        auto holder = remove_holder(encoded);
        if (holder) {
            std::lock_guard lock(holder->mutex);
            termlink_direct_destroy_v3(holder->bridge);
            holder->bridge = nullptr;
        }
        return -1;
    }
    return kOk;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethods_gather(
    JNIEnv* env, jobject, jlong token, jlongArray scalar_array, jobjectArray blobs,
    jintArray stun_port_array) {
    if (scalar_array == nullptr || blobs == nullptr || stun_port_array == nullptr ||
        env->GetArrayLength(scalar_array) != kGatherScalarCount) {
        return kInvalid;
    }
    const auto stun_count = env->GetArrayLength(stun_port_array);
    if (stun_count < 0 || stun_count > 2 ||
        env->GetArrayLength(blobs) != kFixedGatherBlobCount + stun_count) {
        return kInvalid;
    }
    std::array<jlong, kGatherScalarCount> scalars{};
    env->GetLongArrayRegion(scalar_array, 0, kGatherScalarCount, scalars.data());
    std::array<jint, 2> stun_ports{};
    if (stun_count > 0) env->GetIntArrayRegion(stun_port_array, 0, stun_count, stun_ports.data());
    if (env->ExceptionCheck() || scalars[0] <= 0 || scalars[1] == 0 ||
        (scalars[2] != 0 && scalars[2] < 1024) || scalars[2] > 65535 ||
        scalars[3] < 100 || scalars[3] > 5000) {
        return kInvalid;
    }

    TermLinkDirectGatherV3 gather{};
    gather.abi_version = TERMLINK_DIRECT_ABI_VERSION_V3;
    gather.struct_size = static_cast<std::uint32_t>(sizeof(gather));
    gather.epoch = static_cast<std::uint64_t>(scalars[0]);
    gather.network_handle = static_cast<std::uint64_t>(scalars[1]);
    gather.gather_timeout_ms = static_cast<std::uint32_t>(scalars[3]);
    if (!copy_exact_blob(env, blobs, 0, gather.attempt_id, sizeof(gather.attempt_id)) ||
        !decode_blob_address(env, blobs, 1, static_cast<jint>(scalars[2]),
                             gather.local_bind) ||
        !copy_bounded_blob(env, blobs, 2, gather.advertised_local_ip,
                           sizeof(gather.advertised_local_ip),
                           gather.advertised_local_ip_length) ||
        (gather.advertised_local_ip_length != 4 && gather.advertised_local_ip_length != 16)) {
        return kInvalid;
    }
    gather.stun_count = static_cast<std::uint32_t>(stun_count);
    for (jsize index = 0; index < stun_count; ++index) {
        if (stun_ports[static_cast<std::size_t>(index)] < 1024) return kInvalid;
        if (!decode_blob_address(env, blobs, kFixedGatherBlobCount + index,
                                 stun_ports[static_cast<std::size_t>(index)],
                                 gather.stun_servers[static_cast<std::size_t>(index)])) {
            return kInvalid;
        }
    }
    return with_bridge(token, [&gather](void* bridge) {
        return termlink_direct_gather_v3(bridge, &gather);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethods_nominate(
    JNIEnv* env, jobject, jlong token, jlongArray scalar_array, jobjectArray blobs) {
    if (scalar_array == nullptr || blobs == nullptr ||
        env->GetArrayLength(scalar_array) != kNominateScalarCount ||
        env->GetArrayLength(blobs) != kFixedNominateBlobCount) {
        return kInvalid;
    }
    std::array<jlong, kNominateScalarCount> scalars{};
    env->GetLongArrayRegion(scalar_array, 0, kNominateScalarCount, scalars.data());
    if (env->ExceptionCheck() || scalars[0] <= 0 || scalars[1] < 1 || scalars[1] > 2 ||
        scalars[2] < 500 || scalars[2] > 10000 || scalars[3] == 0 ||
        scalars[4] < 1024 || scalars[4] > 65535 ||
        scalars[5] < 1024 || scalars[5] > 65535 || scalars[6] <= 0) {
        return kInvalid;
    }

    TermLinkDirectNominateV3 nominate{};
    nominate.abi_version = TERMLINK_DIRECT_ABI_VERSION_V3;
    nominate.struct_size = static_cast<std::uint32_t>(sizeof(nominate));
    auto& start = nominate.start_v2;
    start.abi_version = TERMLINK_DIRECT_ABI_VERSION_V2;
    start.struct_size = static_cast<std::uint32_t>(sizeof(start));
    start.epoch = static_cast<std::uint64_t>(scalars[0]);
    start.role = static_cast<std::uint32_t>(scalars[1]);
    start.timeout_ms = static_cast<std::uint32_t>(scalars[2]);
    start.network_handle = static_cast<std::uint64_t>(scalars[3]);
    start.now_ms = static_cast<std::int64_t>(scalars[6]);

    if (!copy_exact_blob(env, blobs, 0, start.attempt_id, sizeof(start.attempt_id)) ||
        !copy_exact_blob(env, blobs, 1, start.connection_id, sizeof(start.connection_id)) ||
        !copy_exact_blob(env, blobs, 2, start.local_device_id, sizeof(start.local_device_id)) ||
        !copy_exact_blob(env, blobs, 3, start.peer_device_id, sizeof(start.peer_device_id)) ||
        !decode_blob_address(env, blobs, 4, static_cast<jint>(scalars[4]), start.local_bind) ||
        !decode_blob_address(env, blobs, 5, static_cast<jint>(scalars[5]), start.remote_candidate) ||
        !copy_bounded_blob(env, blobs, 6, start.ticket, sizeof(start.ticket), start.ticket_length) ||
        !copy_exact_blob(env, blobs, 7, start.server_signing_public_key,
                         sizeof(start.server_signing_public_key)) ||
        !copy_exact_blob(env, blobs, 8, start.peer_certificate_sha256,
                         sizeof(start.peer_certificate_sha256)) ||
        !copy_exact_blob(env, blobs, 9, start.peer_device_signing_public_key,
                         sizeof(start.peer_device_signing_public_key)) ||
        !copy_exact_blob(env, blobs, 10, start.probe_hmac_key, sizeof(start.probe_hmac_key)) ||
        !copy_exact_blob(env, blobs, 10, nominate.local_send_probe_hmac_key,
                         sizeof(nominate.local_send_probe_hmac_key)) ||
        !copy_exact_blob(env, blobs, 11, nominate.peer_receive_probe_hmac_key,
                         sizeof(nominate.peer_receive_probe_hmac_key)) ||
        !copy_exact_blob(env, blobs, 12, start.ticket_id, sizeof(start.ticket_id)) ||
        !copy_exact_blob(env, blobs, 13, start.nomination_id, sizeof(start.nomination_id)) ||
        !copy_exact_blob(env, blobs, 14, start.initiator_candidate_id,
                         sizeof(start.initiator_candidate_id)) ||
        !copy_exact_blob(env, blobs, 15, start.responder_candidate_id,
                         sizeof(start.responder_candidate_id)) ||
        !copy_exact_blob(env, blobs, 16, start.local_probe_nonce,
                         sizeof(start.local_probe_nonce)) ||
        !copy_exact_blob(env, blobs, 17, start.peer_probe_nonce,
                         sizeof(start.peer_probe_nonce)) ||
        !copy_bounded_blob(env, blobs, 18, start.local_proof_nonce,
                           sizeof(start.local_proof_nonce), start.local_proof_nonce_length)) {
        return kInvalid;
    }
    return with_bridge(token, [&nominate](void* bridge) {
        return termlink_direct_nominate_v3(bridge, &nominate);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethods_cancel(
    JNIEnv* env, jobject, jlong token, jlong epoch, jbyteArray attempt_array) {
    std::array<std::uint8_t, 16> attempt{};
    if (epoch <= 0 || !copy_exact(env, attempt_array, attempt.data(), attempt.size())) return kInvalid;
    const auto control = control_record(epoch, attempt);
    return with_bridge(token, [&control](void* bridge) {
        return termlink_direct_cancel_v3(bridge, &control);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethods_networkChanged(
    JNIEnv* env, jobject, jlong token, jlong epoch, jbyteArray attempt_array) {
    std::array<std::uint8_t, 16> attempt{};
    if (epoch <= 0 || !copy_exact(env, attempt_array, attempt.data(), attempt.size())) return kInvalid;
    const auto control = control_record(epoch, attempt);
    return with_bridge(token, [&control](void* bridge) {
        return termlink_direct_network_changed_v3(bridge, &control);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethods_submitSignature(
    JNIEnv* env, jobject, jlong token, jlong epoch, jbyteArray attempt_array,
    jlong request_id, jbyteArray signature_array) {
    if (epoch <= 0 || request_id <= 0) return kInvalid;
    TermLinkDirectSignatureV2 signature{};
    signature.abi_version = TERMLINK_DIRECT_ABI_VERSION_V3;
    signature.struct_size = static_cast<std::uint32_t>(sizeof(signature));
    signature.epoch = static_cast<std::uint64_t>(epoch);
    signature.request_id = static_cast<std::uint64_t>(request_id);
    if (!copy_exact(env, attempt_array, signature.attempt_id, sizeof(signature.attempt_id)) ||
        !copy_bounded(env, signature_array, signature.signature, sizeof(signature.signature),
                      signature.signature_length) || signature.signature_length == 0) {
        return kInvalid;
    }
    return with_bridge(token, [&signature](void* bridge) {
        return termlink_direct_submit_signature_v3(bridge, &signature);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethods_poll(
    JNIEnv* env, jobject, jlong token, jlongArray metadata_array, jbyteArray attempt_array,
    jbyteArray endpoint_ip_array, jbyteArray payload_array) {
    if (metadata_array == nullptr || env->GetArrayLength(metadata_array) != kPollMetadataCount ||
        attempt_array == nullptr || env->GetArrayLength(attempt_array) != 16 ||
        endpoint_ip_array == nullptr || env->GetArrayLength(endpoint_ip_array) != 16 ||
        payload_array == nullptr || env->GetArrayLength(payload_array) != 2048) {
        return kInvalid;
    }
    TermLinkDirectEventV2 event{};
    const jint status = with_bridge(token, [&event](void* bridge) {
        return termlink_direct_poll_event_v3(bridge, &event);
    });
    if (status != 1) return status;
    if (event.payload_length > sizeof(event.payload) || event.endpoint.ip_length > 16) return -3;
    const std::array<jlong, kPollMetadataCount> metadata{
        static_cast<jlong>(event.abi_version),
        static_cast<jlong>(event.struct_size),
        static_cast<jlong>(event.kind),
        static_cast<jlong>(event.state),
        static_cast<jlong>(event.reason),
        static_cast<jlong>(event.payload_length),
        static_cast<jlong>(event.epoch),
        static_cast<jlong>(event.generation),
        static_cast<jlong>(event.request_id),
        static_cast<jlong>(event.endpoint.ip_length),
        static_cast<jlong>(event.endpoint.port),
    };
    env->SetLongArrayRegion(metadata_array, 0, kPollMetadataCount, metadata.data());
    env->SetByteArrayRegion(attempt_array, 0, 16,
                            reinterpret_cast<const jbyte*>(event.attempt_id));
    env->SetByteArrayRegion(endpoint_ip_array, 0, 16,
                            reinterpret_cast<const jbyte*>(event.endpoint.ip));
    if (event.payload_length > 0) {
        env->SetByteArrayRegion(payload_array, 0, static_cast<jsize>(event.payload_length),
                                reinterpret_cast<const jbyte*>(event.payload));
    }
    return env->ExceptionCheck() ? -3 : 1;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_network_direct_nativebridge_JniDirectNativeMethods_destroy(
    JNIEnv*, jobject, jlong token) {
    auto holder = remove_holder(token);
    if (!holder) return kInvalid;
    std::lock_guard lock(holder->mutex);
    if (holder->bridge == nullptr) return kInvalid;
    termlink_direct_destroy_v3(holder->bridge);
    holder->bridge = nullptr;
    return kOk;
}

extern "C" JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM*, void*) {
    return JNI_VERSION_1_6;
}

extern "C" JNIEXPORT void JNICALL JNI_OnUnload(JavaVM*, void*) {
    std::vector<std::shared_ptr<BridgeHolder>> holders;
    {
        std::lock_guard lock(g_registry_mutex);
        holders.reserve(g_registry.size());
        for (auto& entry : g_registry) holders.push_back(entry.second);
        g_registry.clear();
    }
    for (auto& holder : holders) {
        std::lock_guard lock(holder->mutex);
        if (holder->bridge != nullptr) {
            termlink_direct_destroy_v3(holder->bridge);
            holder->bridge = nullptr;
        }
    }
}
