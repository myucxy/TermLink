package dev.termlink.network.direct.nativebridge

/** JNI methods use exact, owned ABI records; C++ rejects any wrong-sized record. */
internal interface DirectNativeMethodsV5 {
    fun abiContractV5(): LongArray
    fun createV5(
        enabled: Boolean,
        queueCapacityFrames: Int,
        queueCapacityBytes: Long,
        path: ByteArray,
        outHandle: LongArray,
    ): Int
    fun pathUsageV5(handle: Long, path: ByteArray): Int
    fun attachV5(handle: Long, path: ByteArray): Int
    fun promoteV5(handle: Long, v4Handle: Long, promote: ByteArray): Int
    fun submitSignatureV5(handle: Long, signature: ByteArray): Int
    fun receiveSigningPayloadV5(
        handle: Long,
        path: ByteArray,
        metadata: LongArray,
        output: ByteArray,
    ): Int
    fun pathInfoV5(handle: Long, path: ByteArray, output: ByteArray): Int
    fun receiveEnvelopeV5(
        handle: Long,
        path: ByteArray,
        metadata: LongArray,
        output: ByteArray,
    ): Int
    fun startProposalV5(handle: Long, proposal: ByteArray): Int
    fun receiveProposalV5(handle: Long, proposal: ByteArray): Int
    fun receiveAcceptV5(handle: Long, transition: ByteArray): Int
    fun receiveCommitV5(handle: Long, transition: ByteArray): Int
    fun commitEchoSentV5(handle: Long, transition: ByteArray): Int
    fun receiveFallbackV5(handle: Long, transition: ByteArray): Int
    fun tickV5(handle: Long, transition: ByteArray): Int
    fun updateAppliedCursorV5(handle: Long, cursor: ByteArray): Int
    fun deregisterSessionV5(handle: Long, session: ByteArray): Int
    fun routeEnvelopeV5(handle: Long, route: ByteArray, envelope: ByteArray): Int
    fun pollRouteActionV5(
        handle: Long,
        action: ByteArray,
        output: ByteArray,
        requiredBytes: LongArray,
    ): Int
    fun arbitrateIngressV5(handle: Long, ingress: ByteArray, envelope: ByteArray): Int
    fun tickIngressV5(handle: Long, path: ByteArray, nowMillis: Long): Int
    fun pollEventV5(handle: Long, output: ByteArray): Int
    fun cancelV5(handle: Long, path: ByteArray): Int
    fun networkChangedV5(handle: Long, path: ByteArray): Int
    fun renewApplyV5(handle: Long, ticket: ByteArray): Int
    fun destroyV5(handle: Long): Int
}

internal object JniDirectNativeMethodsV5 : DirectNativeMethodsV5 {
    init {
        System.loadLibrary("termlink_direct_jni")
    }

    external override fun abiContractV5(): LongArray
    external override fun createV5(
        enabled: Boolean,
        queueCapacityFrames: Int,
        queueCapacityBytes: Long,
        path: ByteArray,
        outHandle: LongArray,
    ): Int
    external override fun pathUsageV5(handle: Long, path: ByteArray): Int
    external override fun attachV5(handle: Long, path: ByteArray): Int
    external override fun promoteV5(handle: Long, v4Handle: Long, promote: ByteArray): Int
    external override fun submitSignatureV5(handle: Long, signature: ByteArray): Int
    external override fun receiveSigningPayloadV5(
        handle: Long,
        path: ByteArray,
        metadata: LongArray,
        output: ByteArray,
    ): Int
    external override fun pathInfoV5(handle: Long, path: ByteArray, output: ByteArray): Int
    external override fun receiveEnvelopeV5(
        handle: Long,
        path: ByteArray,
        metadata: LongArray,
        output: ByteArray,
    ): Int
    external override fun startProposalV5(handle: Long, proposal: ByteArray): Int
    external override fun receiveProposalV5(handle: Long, proposal: ByteArray): Int
    external override fun receiveAcceptV5(handle: Long, transition: ByteArray): Int
    external override fun receiveCommitV5(handle: Long, transition: ByteArray): Int
    external override fun commitEchoSentV5(handle: Long, transition: ByteArray): Int
    external override fun receiveFallbackV5(handle: Long, transition: ByteArray): Int
    external override fun tickV5(handle: Long, transition: ByteArray): Int
    external override fun updateAppliedCursorV5(handle: Long, cursor: ByteArray): Int
    external override fun deregisterSessionV5(handle: Long, session: ByteArray): Int
    external override fun routeEnvelopeV5(handle: Long, route: ByteArray, envelope: ByteArray): Int
    external override fun pollRouteActionV5(
        handle: Long,
        action: ByteArray,
        output: ByteArray,
        requiredBytes: LongArray,
    ): Int
    external override fun arbitrateIngressV5(
        handle: Long,
        ingress: ByteArray,
        envelope: ByteArray,
    ): Int
    external override fun tickIngressV5(handle: Long, path: ByteArray, nowMillis: Long): Int
    external override fun pollEventV5(handle: Long, output: ByteArray): Int
    external override fun renewApplyV5(handle: Long, ticket: ByteArray): Int
    external override fun cancelV5(handle: Long, path: ByteArray): Int
    external override fun networkChangedV5(handle: Long, path: ByteArray): Int
    external override fun destroyV5(handle: Long): Int
}
