package dev.termlink.network.direct.nativebridge

internal interface DirectNativeMethodsV4 {
    fun abiContractV4(): LongArray
    fun createV4(enabled: Boolean, queueCapacity: Int, outHandle: LongArray): Int
    fun gatherV4(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
        stunPorts: IntArray,
    ): Int
    fun beginChecksV4(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
        encodedPairs: Array<ByteArray>,
    ): Int
    fun appendCheckPairsV4(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
        encodedPairs: Array<ByteArray>,
    ): Int
    fun commitNominationV4(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
    ): Int
    fun cancelV4(handle: Long, epoch: Long, attemptId: ByteArray): Int
    fun networkChangedV4(handle: Long, epoch: Long, attemptId: ByteArray): Int
    fun submitSignatureV4(
        handle: Long,
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ): Int
    fun pollV4(
        handle: Long,
        metadata: LongArray,
        attemptId: ByteArray,
        endpointIp: ByteArray,
        payload: ByteArray,
    ): Int
    fun destroyV4(handle: Long): Int
}

internal object JniDirectNativeMethodsV4 : DirectNativeMethodsV4 {
    init {
        System.loadLibrary("termlink_direct_jni")
    }

    external override fun abiContractV4(): LongArray
    external override fun createV4(enabled: Boolean, queueCapacity: Int, outHandle: LongArray): Int
    external override fun gatherV4(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
        stunPorts: IntArray,
    ): Int
    external override fun beginChecksV4(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
        encodedPairs: Array<ByteArray>,
    ): Int
    external override fun appendCheckPairsV4(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
        encodedPairs: Array<ByteArray>,
    ): Int
    external override fun commitNominationV4(
        handle: Long,
        scalars: LongArray,
        blobs: Array<ByteArray>,
    ): Int
    external override fun cancelV4(handle: Long, epoch: Long, attemptId: ByteArray): Int
    external override fun networkChangedV4(handle: Long, epoch: Long, attemptId: ByteArray): Int
    external override fun submitSignatureV4(
        handle: Long,
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ): Int
    external override fun pollV4(
        handle: Long,
        metadata: LongArray,
        attemptId: ByteArray,
        endpointIp: ByteArray,
        payload: ByteArray,
    ): Int
    external override fun destroyV4(handle: Long): Int
}
