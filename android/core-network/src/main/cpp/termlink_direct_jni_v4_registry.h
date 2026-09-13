#ifndef TERMLINK_DIRECT_JNI_V4_REGISTRY_H
#define TERMLINK_DIRECT_JNI_V4_REGISTRY_H

#include <cstdint>

#include "termlink_direct_ffi_v5.h"

/*
 * JNI-internal ownership seam. Kotlin passes only the opaque registry token;
 * the native V4 pointer never crosses JNI. The implementation locks the V4
 * holder for the whole one-shot promotion and permanently disables ordinary
 * V4 operations after the promotion call returns, whether activation succeeds
 * or fails. Only destruction of the consumed wrapper remains available.
 */
std::int32_t termlink_direct_jni_promote_v4_token(
    std::int64_t v4_token, void* v5_bridge,
    const TermLinkDirectPromoteV5* promote);

#endif  // TERMLINK_DIRECT_JNI_V4_REGISTRY_H
