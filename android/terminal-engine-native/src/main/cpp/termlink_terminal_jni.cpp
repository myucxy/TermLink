#include <jni.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <limits>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <utility>
#include <vector>

#include "termlink_terminal.h"

// All Java_* implementation names are private and reached only through RegisterNatives. Android's
// loader sees JNI_OnLoad as the sole bridge entry point.
#undef JNIEXPORT
#define JNIEXPORT __attribute__((visibility("hidden")))

namespace {

constexpr jsize kAllocationWords = 3;
constexpr jsize kConfigV3Words = 14;
constexpr jsize kEventWords = 5;
constexpr jsize kMouseWords = 14;
constexpr jsize kInputV1Words = 9;
constexpr jsize kInputV2Words = 18;
constexpr jsize kInputV3Words = 27;
constexpr jsize kMaximumBatchEvents = 256;
constexpr uint64_t kMaximumSnapshotBytes = UINT64_C(64) * 1024U * 1024U;

static_assert(sizeof(TlTerminalConfigV1) == 40);
static_assert(sizeof(TlTerminalConfigV3) == 88);
static_assert(sizeof(TlTerminalEvent) == 56);
static_assert(sizeof(TlTerminalBuffer) == 24);
static_assert(sizeof(TlTerminalCheckpointInfo) == 40);
static_assert(sizeof(TlTerminalPresentationInfoV1) == 48);
static_assert(sizeof(TlTerminalInputV1) == 56);
static_assert(sizeof(TlTerminalInputV2) == 96);
static_assert(sizeof(TlTerminalInputV3) == 160);
static_assert(sizeof(TlTerminalMouseInputV1) == 64);

struct TerminalEntry final {
    explicit TerminalEntry(TlTerminal* value) : terminal(value) {}
    ~TerminalEntry() {
        if (terminal != nullptr) {
            tl_terminal_destroy(terminal);
        }
    }

    std::mutex mutex;
    TlTerminal* terminal;
};

struct TerminalDeleter final {
    void operator()(TlTerminal* terminal) const {
        if (terminal != nullptr) {
            tl_terminal_destroy(terminal);
        }
    }
};

using TerminalOwner = std::unique_ptr<TlTerminal, TerminalDeleter>;

std::mutex g_registry_mutex;
std::unordered_map<uint64_t, std::shared_ptr<TerminalEntry>> g_registry;
std::atomic<uint64_t> g_next_token{1};

class BufferOwner final {
  public:
    BufferOwner() = default;
    ~BufferOwner() {
        if (buffer_.data != nullptr) {
            (void)tl_terminal_buffer_free(&buffer_);
        }
    }
    BufferOwner(const BufferOwner&) = delete;
    BufferOwner& operator=(const BufferOwner&) = delete;
    TlTerminalBuffer* out() { return &buffer_; }
    const TlTerminalBuffer& get() const { return buffer_; }

  private:
    TlTerminalBuffer buffer_{TL_TERMINAL_ABI_VERSION, sizeof(TlTerminalBuffer), nullptr,
#if UINTPTR_MAX == UINT32_MAX
                             0,
#endif
                             0};
};

class CheckpointOwner final {
  public:
    ~CheckpointOwner() {
        if (checkpoint_ != nullptr) {
            tl_terminal_checkpoint_destroy(checkpoint_);
        }
    }
    CheckpointOwner(const CheckpointOwner&) = delete;
    CheckpointOwner& operator=(const CheckpointOwner&) = delete;
    CheckpointOwner() = default;
    TlTerminalCheckpoint** out() { return &checkpoint_; }
    TlTerminalCheckpoint* get() const { return checkpoint_; }

  private:
    TlTerminalCheckpoint* checkpoint_ = nullptr;
};

class ByteArrayPin final {
  public:
    ByteArrayPin(JNIEnv* env, jbyteArray array, bool owns_local_ref = false)
        : env_(env), array_(array), owns_local_ref_(owns_local_ref) {
        length_ = array_ == nullptr ? 0 : env_->GetArrayLength(array_);
        if (array_ != nullptr && length_ != 0) {
            bytes_ = env_->GetByteArrayElements(array_, nullptr);
        }
    }
    ~ByteArrayPin() {
        if (bytes_ != nullptr) {
            env_->ReleaseByteArrayElements(array_, bytes_, JNI_ABORT);
        }
        if (owns_local_ref_ && array_ != nullptr) {
            env_->DeleteLocalRef(array_);
        }
    }
    ByteArrayPin(const ByteArrayPin&) = delete;
    ByteArrayPin& operator=(const ByteArrayPin&) = delete;
    bool valid() const { return !env_->ExceptionCheck() && (length_ == 0 || bytes_ != nullptr); }
    const uint8_t* data() const { return reinterpret_cast<const uint8_t*>(bytes_); }
    uint64_t size() const { return static_cast<uint64_t>(length_); }

  private:
    JNIEnv* env_;
    jbyteArray array_;
    bool owns_local_ref_;
    jsize length_ = 0;
    jbyte* bytes_ = nullptr;
};

template <typename Function>
jint safe_status(Function&& function) noexcept {
    try {
        return static_cast<jint>(function());
    } catch (...) {
        return static_cast<jint>(TL_STATUS_PANIC);
    }
}

bool exact_length(JNIEnv* env, jarray array, jsize expected) {
    return array != nullptr && env->GetArrayLength(array) == expected;
}

bool minimum_length(JNIEnv* env, jarray array, jsize minimum) {
    return array != nullptr && env->GetArrayLength(array) >= minimum;
}

bool read_longs(JNIEnv* env, jlongArray array, jsize expected, std::vector<jlong>* output) {
    if (!exact_length(env, array, expected)) {
        return false;
    }
    output->resize(static_cast<size_t>(expected));
    env->GetLongArrayRegion(array, 0, expected, output->data());
    return !env->ExceptionCheck();
}

bool write_longs(JNIEnv* env, jlongArray array, const std::vector<jlong>& values) {
    const jsize count = static_cast<jsize>(values.size());
    if (!minimum_length(env, array, count)) {
        return false;
    }
    env->SetLongArrayRegion(array, 0, count, values.data());
    return !env->ExceptionCheck();
}

std::shared_ptr<TerminalEntry> find_terminal(jlong raw_token) {
    if (raw_token <= 0) {
        return nullptr;
    }
    const auto token = static_cast<uint64_t>(raw_token);
    std::lock_guard lock(g_registry_mutex);
    const auto found = g_registry.find(token);
    return found == g_registry.end() ? nullptr : found->second;
}

uint64_t register_terminal(TerminalOwner terminal) {
    // The unique owner covers allocation failure. Once TerminalEntry exists it becomes the owner,
    // including when unordered_map growth throws before the token can be published.
    auto entry = std::make_shared<TerminalEntry>(terminal.get());
    (void)terminal.release();
    for (;;) {
        const uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
        if (token == 0 || token > static_cast<uint64_t>(std::numeric_limits<jlong>::max())) {
            continue;
        }
        std::lock_guard lock(g_registry_mutex);
        if (g_registry.emplace(token, entry).second) {
            return token;
        }
    }
}

uint32_t destroy_token(jlong raw_token) {
    if (raw_token <= 0) {
        return TL_STATUS_INVALID_ARGUMENT;
    }
    std::shared_ptr<TerminalEntry> entry;
    {
        std::lock_guard lock(g_registry_mutex);
        const auto found = g_registry.find(static_cast<uint64_t>(raw_token));
        if (found == g_registry.end()) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        entry = found->second;
        g_registry.erase(found);
    }
    std::lock_guard entry_lock(entry->mutex);
    if (entry->terminal == nullptr) {
        return TL_STATUS_INVALID_ARGUMENT;
    }
    tl_terminal_destroy(entry->terminal);
    entry->terminal = nullptr;
    return TL_STATUS_OK;
}

uint32_t publish_allocation(JNIEnv* env, jlongArray output, TlTerminal* terminal,
                            uint64_t next_sequence) {
    const uint64_t token = register_terminal(TerminalOwner(terminal));
    const std::vector<jlong> values{static_cast<jlong>(token), static_cast<jlong>(next_sequence),
                                    static_cast<jlong>(next_sequence)};
    if (!write_longs(env, output, values)) {
        (void)destroy_token(static_cast<jlong>(token));
        return TL_STATUS_PANIC;
    }
    return TL_STATUS_OK;
}

uint32_t publish_buffer(JNIEnv* env, jobjectArray output, BufferOwner* owner, uint64_t maximum) {
    if (!minimum_length(env, output, 1)) {
        return TL_STATUS_INVALID_ARGUMENT;
    }
    const TlTerminalBuffer& buffer = owner->get();
    if (buffer.data == nullptr || buffer.len > maximum ||
        buffer.len > static_cast<uint64_t>(std::numeric_limits<jsize>::max())) {
        return TL_STATUS_PANIC;
    }
    const auto length = static_cast<jsize>(buffer.len);
    jbyteArray bytes = env->NewByteArray(length);
    if (bytes == nullptr || env->ExceptionCheck()) {
        return TL_STATUS_PANIC;
    }
    if (length != 0) {
        env->SetByteArrayRegion(bytes, 0, length, reinterpret_cast<const jbyte*>(buffer.data));
    }
    if (!env->ExceptionCheck()) {
        env->SetObjectArrayElement(output, 0, bytes);
    }
    env->DeleteLocalRef(bytes);
    return env->ExceptionCheck() ? TL_STATUS_PANIC : TL_STATUS_OK;
}

template <typename Function>
uint32_t with_terminal(jlong token, Function&& function) {
    auto entry = find_terminal(token);
    if (entry == nullptr) {
        return TL_STATUS_INVALID_ARGUMENT;
    }
    std::lock_guard lock(entry->mutex);
    if (entry->terminal == nullptr) {
        return TL_STATUS_INVALID_ARGUMENT;
    }
    return function(entry->terminal);
}

uint32_t marshal_events(JNIEnv* env, jlongArray raw_events, jobjectArray raw_payloads,
                        std::vector<TlTerminalEvent>* events,
                        std::vector<std::unique_ptr<ByteArrayPin>>* pins) {
    if (raw_events == nullptr || raw_payloads == nullptr) {
        return TL_STATUS_NULL_POINTER;
    }
    const jsize word_count = env->GetArrayLength(raw_events);
    if (word_count <= 0 || word_count % kEventWords != 0) {
        return TL_STATUS_INVALID_ARGUMENT;
    }
    const jsize event_count = word_count / kEventWords;
    if (event_count > kMaximumBatchEvents || env->GetArrayLength(raw_payloads) != event_count) {
        return TL_STATUS_LIMIT_EXCEEDED;
    }
    std::vector<jlong> words(static_cast<size_t>(word_count));
    env->GetLongArrayRegion(raw_events, 0, word_count, words.data());
    if (env->ExceptionCheck()) {
        return TL_STATUS_PANIC;
    }
    events->reserve(static_cast<size_t>(event_count));
    pins->reserve(static_cast<size_t>(event_count));
    for (jsize index = 0; index < event_count; ++index) {
        const size_t offset = static_cast<size_t>(index * kEventWords);
        TlTerminalEvent event{};
        event.abi_version = TL_TERMINAL_ABI_VERSION;
        event.struct_size = sizeof(TlTerminalEvent);
        event.source_sequence = static_cast<uint64_t>(words[offset]);
        event.kind = static_cast<uint32_t>(words[offset + 1]);
        event.columns = static_cast<uint32_t>(words[offset + 2]);
        event.rows = static_cast<uint32_t>(words[offset + 3]);
        event.advance_micros = static_cast<uint64_t>(words[offset + 4]);
        if (event.kind == TL_EVENT_OUTPUT) {
            auto payload = static_cast<jbyteArray>(env->GetObjectArrayElement(raw_payloads, index));
            if (payload == nullptr || env->ExceptionCheck()) {
                return env->ExceptionCheck() ? TL_STATUS_PANIC : TL_STATUS_INVALID_ARGUMENT;
            }
            auto pin = std::make_unique<ByteArrayPin>(env, payload, true);
            if (!pin->valid()) {
                return TL_STATUS_PANIC;
            }
            event.data = pin->data();
            event.data_len = pin->size();
            pins->push_back(std::move(pin));
        }
        events->push_back(event);
    }
    return TL_STATUS_OK;
}

uint32_t checkpoint_for(TlTerminal* terminal, CheckpointOwner* checkpoint,
                        TlTerminalCheckpointInfo* info) {
    uint32_t status = tl_terminal_checkpoint_begin(terminal, checkpoint->out());
    if (status != TL_STATUS_OK) {
        return status;
    }
    if (info != nullptr) {
        *info = TlTerminalCheckpointInfo{TL_TERMINAL_ABI_VERSION,
                                         sizeof(TlTerminalCheckpointInfo), 0, 0, {0, 0}};
        status = tl_terminal_checkpoint_get_info(checkpoint->get(), info);
    }
    return status;
}

}  // namespace

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_abiVersion(JNIEnv*, jobject) {
    return static_cast<jint>(tl_terminal_abi_version());
}
extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderFrameV1FormatVersion(JNIEnv*, jobject) {
    return static_cast<jint>(tl_terminal_render_frame_v1_format_version());
}
extern "C" JNIEXPORT jlong JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderFrameV1MaxBytes(JNIEnv*, jobject) {
    return static_cast<jlong>(tl_terminal_render_frame_v1_max_bytes());
}
extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderFrameV3FormatVersion(JNIEnv*, jobject) {
    return static_cast<jint>(tl_terminal_render_frame_v3_format_version());
}
extern "C" JNIEXPORT jlong JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderFrameV3MaxBytes(JNIEnv*, jobject) {
    return static_cast<jlong>(tl_terminal_render_frame_v3_max_bytes());
}
extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_hostEffectFrameV2FormatVersion(JNIEnv*, jobject) {
    return static_cast<jint>(tl_terminal_host_effect_frame_v2_format_version());
}
extern "C" JNIEXPORT jlong JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_hostEffectFrameV2MaxBytes(JNIEnv*, jobject) {
    return static_cast<jlong>(tl_terminal_host_effect_frame_v2_max_bytes());
}
extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_mouseInputV1StructSize(JNIEnv*, jobject) {
    return static_cast<jint>(tl_terminal_mouse_input_v1_struct_size());
}
extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_inputV1StructSize(JNIEnv*, jobject) {
    return static_cast<jint>(tl_terminal_input_v1_struct_size());
}
extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_inputV2StructSize(JNIEnv*, jobject) {
    return static_cast<jint>(tl_terminal_input_v2_struct_size());
}
extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_inputV3StructSize(JNIEnv*, jobject) {
    return static_cast<jint>(tl_terminal_input_v3_struct_size());
}
extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_configV3StructSize(JNIEnv*, jobject) {
    return static_cast<jint>(tl_terminal_config_v3_struct_size());
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_create(
    JNIEnv* env, jobject, jint columns, jint rows, jlong initial_sequence, jlongArray output) {
    return safe_status([&]() -> uint32_t {
        if (!minimum_length(env, output, kAllocationWords) || initial_sequence < 0) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        TlTerminalConfigV1 config{TL_TERMINAL_ABI_VERSION, sizeof(TlTerminalConfigV1),
                                  static_cast<uint32_t>(columns), static_cast<uint32_t>(rows),
                                  static_cast<uint64_t>(initial_sequence), {0, 0}};
        TlTerminal* terminal = nullptr;
        const uint32_t status = tl_terminal_create(&config, &terminal);
        if (status != TL_STATUS_OK) {
            return status;
        }
        return publish_allocation(env, output, terminal, static_cast<uint64_t>(initial_sequence));
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_createV3(
    JNIEnv* env, jobject, jlongArray raw_config, jlongArray output) {
    return safe_status([&]() -> uint32_t {
        std::vector<jlong> words;
        if (!read_longs(env, raw_config, kConfigV3Words, &words) ||
            !minimum_length(env, output, kAllocationWords)) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        if (words[4] < 0 || words[7] < 0) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        TlTerminalConfigV3 config{};
        config.abi_version = static_cast<uint32_t>(words[0]);
        config.struct_size = static_cast<uint32_t>(words[1]);
        config.columns = static_cast<uint32_t>(words[2]);
        config.rows = static_cast<uint32_t>(words[3]);
        config.initial_source_sequence = static_cast<uint64_t>(words[4]);
        config.reserved_v1_compat[0] = static_cast<uint64_t>(words[5]);
        config.reserved_v1_compat[1] = static_cast<uint64_t>(words[6]);
        config.history_capacity_rows = static_cast<uint64_t>(words[7]);
        config.reserved_v2_compat[0] = static_cast<uint64_t>(words[8]);
        config.reserved_v2_compat[1] = static_cast<uint64_t>(words[9]);
        config.output_profile = static_cast<uint32_t>(words[10]);
        config.reserved0 = static_cast<uint32_t>(words[11]);
        config.reserved[0] = static_cast<uint64_t>(words[12]);
        config.reserved[1] = static_cast<uint64_t>(words[13]);
        TlTerminal* terminal = nullptr;
        const uint32_t status = tl_terminal_create_v3(&config, &terminal);
        if (status != TL_STATUS_OK) {
            return status;
        }
        return publish_allocation(env, output, terminal, config.initial_source_sequence);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_importCheckpoint(
    JNIEnv* env, jobject, jbyteArray snapshot, jlongArray output) {
    return safe_status([&]() -> uint32_t {
        if (!minimum_length(env, output, kAllocationWords)) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        ByteArrayPin pin(env, snapshot);
        if (!pin.valid()) {
            return TL_STATUS_PANIC;
        }
        if (pin.size() == 0 || pin.size() > kMaximumSnapshotBytes) {
            return TL_STATUS_LIMIT_EXCEEDED;
        }
        TlTerminal* terminal = nullptr;
        uint32_t status = tl_terminal_import_snapshot(pin.data(), pin.size(), &terminal);
        if (status != TL_STATUS_OK) {
            return status;
        }
        TlTerminalPresentationInfoV1 info{TL_TERMINAL_ABI_VERSION,
                                          sizeof(TlTerminalPresentationInfoV1), 0, 0, 0, 0, 0, {0}};
        status = tl_terminal_get_presentation_info_v1(terminal, &info);
        if (status != TL_STATUS_OK) {
            tl_terminal_destroy(terminal);
            return status;
        }
        return publish_allocation(env, output, terminal, info.next_source_sequence);
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_destroy(JNIEnv*, jobject, jlong token) {
    return safe_status([&]() { return destroy_token(token); });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_applyBatch(
    JNIEnv* env, jobject, jlong token, jlongArray raw_events, jobjectArray raw_payloads) {
    return safe_status([&]() -> uint32_t {
        std::vector<TlTerminalEvent> events;
        std::vector<std::unique_ptr<ByteArrayPin>> pins;
        const uint32_t marshal_status = marshal_events(env, raw_events, raw_payloads, &events, &pins);
        if (marshal_status != TL_STATUS_OK) {
            return marshal_status;
        }
        return with_terminal(token, [&](TlTerminal* terminal) {
            return tl_terminal_apply_event_batch_v1(terminal, events.data(),
                                                     static_cast<uint32_t>(events.size()));
        });
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_applyBatchWithHostEffectsV2(
    JNIEnv* env, jobject, jlong token, jlongArray raw_events, jobjectArray raw_payloads,
    jobjectArray output) {
    return safe_status([&]() -> uint32_t {
        // The Rust call commits atomically, so reject an unusable Java output slot first.
        if (!minimum_length(env, output, 1)) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        std::vector<TlTerminalEvent> events;
        std::vector<std::unique_ptr<ByteArrayPin>> pins;
        const uint32_t marshal_status = marshal_events(env, raw_events, raw_payloads, &events, &pins);
        if (marshal_status != TL_STATUS_OK) {
            return marshal_status;
        }
        BufferOwner buffer;
        const uint32_t status = with_terminal(token, [&](TlTerminal* terminal) {
            return tl_terminal_apply_event_batch_with_host_effects_v2(
                terminal, events.data(), static_cast<uint32_t>(events.size()), buffer.out());
        });
        if (status != TL_STATUS_OK) {
            return status;
        }
        return publish_buffer(env, output, &buffer, tl_terminal_host_effect_frame_v2_max_bytes());
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_render(
    JNIEnv* env, jobject, jlong token, jlong maximum, jobjectArray output) {
    return safe_status([&]() -> uint32_t {
        if (maximum <= 0) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        BufferOwner buffer;
        const uint32_t status = with_terminal(token, [&](TlTerminal* terminal) {
            CheckpointOwner checkpoint;
            uint32_t inner = checkpoint_for(terminal, &checkpoint, nullptr);
            if (inner == TL_STATUS_OK) {
                inner = tl_terminal_checkpoint_export_render_frame_v1(
                    checkpoint.get(), static_cast<uint64_t>(maximum), buffer.out());
            }
            return inner;
        });
        if (status != TL_STATUS_OK) {
            return status;
        }
        return publish_buffer(env, output, &buffer, static_cast<uint64_t>(maximum));
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderV3(
    JNIEnv* env, jobject, jlong token, jlong maximum, jobjectArray output) {
    return safe_status([&]() -> uint32_t {
        if (maximum <= 0) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        BufferOwner buffer;
        const uint32_t status = with_terminal(token, [&](TlTerminal* terminal) {
            CheckpointOwner checkpoint;
            uint32_t inner = checkpoint_for(terminal, &checkpoint, nullptr);
            if (inner == TL_STATUS_OK) {
                inner = tl_terminal_checkpoint_export_render_frame_v3(
                    checkpoint.get(), static_cast<uint64_t>(maximum), buffer.out());
            }
            return inner;
        });
        if (status != TL_STATUS_OK) {
            return status;
        }
        return publish_buffer(env, output, &buffer, static_cast<uint64_t>(maximum));
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderV3AtScrollback(
    JNIEnv* env, jobject, jlong token, jlong offset_rows, jlong maximum,
    jlongArray output_info, jobjectArray output) {
    return safe_status([&]() -> uint32_t {
        if (offset_rows < 0 || maximum <= 0 || !minimum_length(env, output_info, 3) ||
            !minimum_length(env, output, 1) ||
            static_cast<uint64_t>(offset_rows) > std::numeric_limits<uint32_t>::max()) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        BufferOwner buffer;
        uint32_t available_rows = 0;
        uint64_t generation_rows = 0;
        uint32_t applied_offset = 0;
        const uint32_t status = with_terminal(token, [&](TlTerminal* terminal) {
            CheckpointOwner checkpoint;
            uint32_t inner = checkpoint_for(terminal, &checkpoint, nullptr);
            if (inner == TL_STATUS_OK) {
                inner = tl_terminal_checkpoint_get_scrollback_info_v1(
                    checkpoint.get(), &available_rows, &generation_rows);
            }
            if (inner == TL_STATUS_OK) {
                applied_offset = std::min(static_cast<uint32_t>(offset_rows), available_rows);
                inner = tl_terminal_checkpoint_export_render_frame_v3_at_scrollback(
                    checkpoint.get(), applied_offset, static_cast<uint64_t>(maximum),
                    buffer.out());
            }
            return inner;
        });
        if (status != TL_STATUS_OK) {
            return status;
        }
        if (generation_rows > static_cast<uint64_t>(std::numeric_limits<jlong>::max())) {
            return TL_STATUS_LIMIT_EXCEEDED;
        }
        const std::vector<jlong> values{
            static_cast<jlong>(available_rows), static_cast<jlong>(generation_rows),
            static_cast<jlong>(applied_offset)};
        if (!write_longs(env, output_info, values)) {
            return TL_STATUS_PANIC;
        }
        return publish_buffer(env, output, &buffer, static_cast<uint64_t>(maximum));
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_encodeInputV1(
    JNIEnv* env, jobject, jlong token, jlongArray raw_input, jbyteArray raw_data,
    jobjectArray output) {
    return safe_status([&]() -> uint32_t {
        std::vector<jlong> words;
        if (!read_longs(env, raw_input, kInputV1Words, &words)) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        ByteArrayPin data(env, raw_data);
        if (!data.valid()) {
            return TL_STATUS_PANIC;
        }
        if (words[6] < 0 || static_cast<uint64_t>(words[6]) != data.size()) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        TlTerminalInputV1 input{};
        input.abi_version = static_cast<uint32_t>(words[0]);
        input.struct_size = static_cast<uint32_t>(words[1]);
        input.kind = static_cast<uint32_t>(words[2]);
        input.key = static_cast<uint32_t>(words[3]);
        input.modifiers = static_cast<uint32_t>(words[4]);
        input.character = static_cast<uint32_t>(words[5]);
        input.data = data.data();
        input.data_len = data.size();
        input.reserved[0] = static_cast<uint64_t>(words[7]);
        input.reserved[1] = static_cast<uint64_t>(words[8]);
        BufferOwner buffer;
        const uint32_t status = with_terminal(token, [&](TlTerminal* terminal) {
            return tl_terminal_encode_input_v1(terminal, &input, buffer.out());
        });
        if (status != TL_STATUS_OK) {
            return status;
        }
        return publish_buffer(env, output, &buffer, tl_terminal_host_effect_frame_v2_max_bytes());
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_encodeMouse(
    JNIEnv* env, jobject, jlong token, jlongArray raw_input, jobjectArray output) {
    return safe_status([&]() -> uint32_t {
        std::vector<jlong> words;
        if (!read_longs(env, raw_input, kMouseWords, &words)) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        TlTerminalMouseInputV1 input{};
        input.abi_version = static_cast<uint32_t>(words[0]);
        input.struct_size = static_cast<uint32_t>(words[1]);
        input.action = static_cast<uint32_t>(words[2]);
        input.button = static_cast<uint32_t>(words[3]);
        input.wheel = static_cast<uint32_t>(words[4]);
        input.buttons_held = static_cast<uint32_t>(words[5]);
        input.position_flags = static_cast<uint32_t>(words[6]);
        input.modifiers = static_cast<uint32_t>(words[7]);
        input.cell_column = static_cast<uint32_t>(words[8]);
        input.cell_row = static_cast<uint32_t>(words[9]);
        input.pixel_x = static_cast<uint32_t>(words[10]);
        input.pixel_y = static_cast<uint32_t>(words[11]);
        input.reserved[0] = static_cast<uint64_t>(words[12]);
        input.reserved[1] = static_cast<uint64_t>(words[13]);
        BufferOwner buffer;
        const uint32_t status = with_terminal(token, [&](TlTerminal* terminal) {
            return tl_terminal_encode_mouse_input_v1(terminal, &input, buffer.out());
        });
        if (status != TL_STATUS_OK) {
            return status;
        }
        return publish_buffer(env, output, &buffer, tl_terminal_host_effect_frame_v2_max_bytes());
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_encodeInputV2(
    JNIEnv* env, jobject, jlong token, jlongArray raw_input, jobjectArray output) {
    return safe_status([&]() -> uint32_t {
        std::vector<jlong> words;
        if (!read_longs(env, raw_input, kInputV2Words, &words)) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        if (words[6] != 0 || words[14] < 0 ||
            static_cast<uint64_t>(words[14]) > UINT32_MAX) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        TlTerminalInputV2 input{};
        input.abi_version = static_cast<uint32_t>(words[0]);
        input.struct_size = static_cast<uint32_t>(words[1]);
        input.kind = static_cast<uint32_t>(words[2]);
        input.key = static_cast<uint32_t>(words[3]);
        input.modifiers = static_cast<uint32_t>(words[4]);
        input.character = static_cast<uint32_t>(words[5]);
        input.data_len = static_cast<uint64_t>(words[7]);
        input.reserved_v1_compat[0] = static_cast<uint64_t>(words[8]);
        input.reserved_v1_compat[1] = static_cast<uint64_t>(words[9]);
        input.win32_virtual_key = static_cast<uint32_t>(words[10]);
        input.win32_scan_code = static_cast<uint32_t>(words[11]);
        input.win32_unicode_char = static_cast<uint32_t>(words[12]);
        input.win32_key_down = static_cast<uint32_t>(words[13]);
        input.win32_control_key_state = static_cast<uint32_t>(words[14]);
        input.win32_repeat_count = static_cast<uint32_t>(words[15]);
        input.reserved[0] = static_cast<uint64_t>(words[16]);
        input.reserved[1] = static_cast<uint64_t>(words[17]);
        BufferOwner buffer;
        const uint32_t status = with_terminal(token, [&](TlTerminal* terminal) {
            return tl_terminal_encode_input_v2(terminal, &input, buffer.out());
        });
        if (status != TL_STATUS_OK) {
            return status;
        }
        return publish_buffer(env, output, &buffer, tl_terminal_host_effect_frame_v2_max_bytes());
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_encodeInputV3(
    JNIEnv* env, jobject, jlong token, jlongArray raw_input, jbyteArray associated_text,
    jobjectArray output) {
    return safe_status([&]() -> uint32_t {
        std::vector<jlong> words;
        if (!read_longs(env, raw_input, kInputV3Words, &words)) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        ByteArrayPin text(env, associated_text);
        if (!text.valid()) {
            return TL_STATUS_PANIC;
        }
        if (words[6] != 0 || words[14] < 0 ||
            static_cast<uint64_t>(words[14]) > UINT32_MAX || words[22] < 0 ||
            static_cast<uint64_t>(words[22]) != text.size()) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        TlTerminalInputV3 input{};
        input.abi_version = static_cast<uint32_t>(words[0]);
        input.struct_size = static_cast<uint32_t>(words[1]);
        input.kind = static_cast<uint32_t>(words[2]);
        input.key = static_cast<uint32_t>(words[3]);
        input.modifiers = static_cast<uint32_t>(words[4]);
        input.character = static_cast<uint32_t>(words[5]);
        input.data_len = static_cast<uint64_t>(words[7]);
        input.reserved_v1_compat[0] = static_cast<uint64_t>(words[8]);
        input.reserved_v1_compat[1] = static_cast<uint64_t>(words[9]);
        input.win32_virtual_key = static_cast<uint32_t>(words[10]);
        input.win32_scan_code = static_cast<uint32_t>(words[11]);
        input.win32_unicode_char = static_cast<uint32_t>(words[12]);
        input.win32_key_down = static_cast<uint32_t>(words[13]);
        input.win32_control_key_state = static_cast<uint32_t>(words[14]);
        input.win32_repeat_count = static_cast<uint32_t>(words[15]);
        input.reserved[0] = static_cast<uint64_t>(words[16]);
        input.reserved[1] = static_cast<uint64_t>(words[17]);
        input.key_event_type = static_cast<uint32_t>(words[18]);
        input.keyboard_modifiers = static_cast<uint32_t>(words[19]);
        input.shifted_key = static_cast<uint32_t>(words[20]);
        input.base_layout_key = static_cast<uint32_t>(words[21]);
        input.associated_text = text.data();
        input.associated_text_len = text.size();
        input.reserved_v3[0] = static_cast<uint64_t>(words[23]);
        input.reserved_v3[1] = static_cast<uint64_t>(words[24]);
        input.reserved_v3[2] = static_cast<uint64_t>(words[25]);
        input.reserved_v3[3] = static_cast<uint64_t>(words[26]);
        BufferOwner buffer;
        const uint32_t status = with_terminal(token, [&](TlTerminal* terminal) {
            return tl_terminal_encode_input_v3(terminal, &input, buffer.out());
        });
        if (status != TL_STATUS_OK) {
            return status;
        }
        return publish_buffer(env, output, &buffer, tl_terminal_host_effect_frame_v2_max_bytes());
    });
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_termlink_terminal_nativecore_JniNativeMethods_exportCheckpoint(
    JNIEnv* env, jobject, jlong token, jlong maximum, jlongArray output_info,
    jobjectArray output_payload) {
    return safe_status([&]() -> uint32_t {
        if (maximum <= 0 || static_cast<uint64_t>(maximum) > kMaximumSnapshotBytes ||
            !minimum_length(env, output_info, 2)) {
            return TL_STATUS_INVALID_ARGUMENT;
        }
        BufferOwner buffer;
        TlTerminalCheckpointInfo info{};
        const uint32_t status = with_terminal(token, [&](TlTerminal* terminal) {
            CheckpointOwner checkpoint;
            uint32_t inner = checkpoint_for(terminal, &checkpoint, &info);
            if (inner == TL_STATUS_OK) {
                inner = tl_terminal_checkpoint_export_snapshot(checkpoint.get(), buffer.out());
            }
            return inner;
        });
        if (status != TL_STATUS_OK) {
            return status;
        }
        if (buffer.get().len > static_cast<uint64_t>(maximum)) {
            return TL_STATUS_LIMIT_EXCEEDED;
        }
        const std::vector<jlong> values{static_cast<jlong>(info.revision),
                                        static_cast<jlong>(info.next_source_sequence)};
        if (!write_longs(env, output_info, values)) {
            return TL_STATUS_PANIC;
        }
        return publish_buffer(env, output_payload, &buffer, static_cast<uint64_t>(maximum));
    });
}

extern "C" __attribute__((visibility("default"))) jint JNICALL JNI_OnLoad(JavaVM* vm, void*) {
    JNIEnv* env = nullptr;
    if (vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6) != JNI_OK || env == nullptr) {
        return JNI_ERR;
    }
    jclass methods_class =
        env->FindClass("dev/termlink/terminal/nativecore/JniNativeMethods");
    if (methods_class == nullptr) {
        return JNI_ERR;
    }
    JNINativeMethod methods[] = {
        {const_cast<char*>("abiVersion"), const_cast<char*>("()I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_abiVersion)},
        {const_cast<char*>("renderFrameV1FormatVersion"), const_cast<char*>("()I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderFrameV1FormatVersion)},
        {const_cast<char*>("renderFrameV1MaxBytes"), const_cast<char*>("()J"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderFrameV1MaxBytes)},
        {const_cast<char*>("renderFrameV3FormatVersion"), const_cast<char*>("()I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderFrameV3FormatVersion)},
        {const_cast<char*>("renderFrameV3MaxBytes"), const_cast<char*>("()J"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderFrameV3MaxBytes)},
        {const_cast<char*>("hostEffectFrameV2FormatVersion"), const_cast<char*>("()I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_hostEffectFrameV2FormatVersion)},
        {const_cast<char*>("hostEffectFrameV2MaxBytes"), const_cast<char*>("()J"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_hostEffectFrameV2MaxBytes)},
        {const_cast<char*>("mouseInputV1StructSize"), const_cast<char*>("()I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_mouseInputV1StructSize)},
        {const_cast<char*>("inputV1StructSize"), const_cast<char*>("()I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_inputV1StructSize)},
        {const_cast<char*>("inputV2StructSize"), const_cast<char*>("()I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_inputV2StructSize)},
        {const_cast<char*>("inputV3StructSize"), const_cast<char*>("()I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_inputV3StructSize)},
        {const_cast<char*>("configV3StructSize"), const_cast<char*>("()I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_configV3StructSize)},
        {const_cast<char*>("create"), const_cast<char*>("(IIJ[J)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_create)},
        {const_cast<char*>("createV3"), const_cast<char*>("([J[J)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_createV3)},
        {const_cast<char*>("importCheckpoint"), const_cast<char*>("([B[J)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_importCheckpoint)},
        {const_cast<char*>("destroy"), const_cast<char*>("(J)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_destroy)},
        {const_cast<char*>("applyBatch"), const_cast<char*>("(J[J[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_applyBatch)},
        {const_cast<char*>("applyBatchWithHostEffectsV2"),
         const_cast<char*>("(J[J[[B[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_applyBatchWithHostEffectsV2)},
        {const_cast<char*>("render"), const_cast<char*>("(JJ[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_render)},
        {const_cast<char*>("renderV3"), const_cast<char*>("(JJ[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderV3)},
        {const_cast<char*>("renderV3AtScrollback"), const_cast<char*>("(JJJ[J[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_renderV3AtScrollback)},
        {const_cast<char*>("encodeInputV1"), const_cast<char*>("(J[J[B[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_encodeInputV1)},
        {const_cast<char*>("encodeMouse"), const_cast<char*>("(J[J[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_encodeMouse)},
        {const_cast<char*>("encodeInputV2"), const_cast<char*>("(J[J[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_encodeInputV2)},
        {const_cast<char*>("encodeInputV3"), const_cast<char*>("(J[J[B[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_encodeInputV3)},
        {const_cast<char*>("exportCheckpoint"), const_cast<char*>("(JJ[J[[B)I"),
         reinterpret_cast<void*>(Java_dev_termlink_terminal_nativecore_JniNativeMethods_exportCheckpoint)},
    };
    const jint registration = env->RegisterNatives(
        methods_class, methods, static_cast<jint>(sizeof(methods) / sizeof(methods[0])));
    env->DeleteLocalRef(methods_class);
    return registration == JNI_OK ? JNI_VERSION_1_6 : JNI_ERR;
}
