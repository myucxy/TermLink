package dev.termlink.network.direct.nativebridge

internal interface DirectNativeMethods {
    fun abiContract(): LongArray
    fun create(enabled: Boolean, queueCapacity: Int, outHandle: LongArray): Int
    fun gather(handle: Long, scalars: LongArray, blobs: Array<ByteArray>, stunPorts: IntArray): Int
    fun nominate(handle: Long, scalars: LongArray, blobs: Array<ByteArray>): Int
    fun cancel(handle: Long, epoch: Long, attemptId: ByteArray): Int
    fun networkChanged(handle: Long, epoch: Long, attemptId: ByteArray): Int
    fun submitSignature(
        handle: Long,
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ): Int
    fun poll(
        handle: Long,
        metadata: LongArray,
        attemptId: ByteArray,
        endpointIp: ByteArray,
        payload: ByteArray,
    ): Int
    fun destroy(handle: Long): Int
}

internal object JniDirectNativeMethods : DirectNativeMethods {
    init {
        System.loadLibrary("termlink_direct_jni")
    }

    external override fun abiContract(): LongArray
    external override fun create(enabled: Boolean, queueCapacity: Int, outHandle: LongArray): Int
    external override fun gather(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
        stunPorts: IntArray,
    ): Int
    external override fun nominate(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
    ): Int
    external override fun cancel(handle: Long, epoch: Long, attemptId: ByteArray): Int
    external override fun networkChanged(handle: Long, epoch: Long, attemptId: ByteArray): Int
    external override fun submitSignature(
        handle: Long,
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ): Int
    external override fun poll(
        handle: Long,
        metadata: LongArray,
        attemptId: ByteArray,
        endpointIp: ByteArray,
        payload: ByteArray,
    ): Int
    external override fun destroy(handle: Long): Int
}
