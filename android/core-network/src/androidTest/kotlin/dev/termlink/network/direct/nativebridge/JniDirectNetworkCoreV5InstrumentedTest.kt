package dev.termlink.network.direct.nativebridge

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class JniDirectNetworkCoreV5InstrumentedTest {
    @Test
    fun packagedLibraryExposesCompatibleV5AbiAndJoinsItsWorker() {
        val core = JniDirectNetworkCoreV5()
        val contractResult = core.abiContract()
        assertTrue(contractResult is DirectNativeResultV5.Success<*>)
        val contract = (contractResult as DirectNativeResultV5.Success<*>).value
        assertTrue(contract is DirectNativeAbiContractV5)
        contract as DirectNativeAbiContractV5
        assertEquals(DIRECT_NATIVE_ABI_VERSION_V5, contract.abiVersion)
        val createResult = core.create(DirectNativeCreateConfigV5(enabled = false))
        assertTrue(createResult is DirectNativeResultV5.Success<*>)
        val handle = (createResult as DirectNativeResultV5.Success<*>).value
        assertTrue(handle is DirectNativeHandleV5)
        handle as DirectNativeHandleV5
        handle.close()
    }

    @Test
    fun packagedLibraryExecutesSessionDeregisterAcrossBothDirections() {
        val path = DirectNativePathV5(
            epoch = 7,
            connectionId = ByteArray(16) { 8 },
            attemptId = ByteArray(16) { 9 },
        )
        val createResult = JniDirectNetworkCoreV5().create(
            DirectNativeCreateConfigV5(enabled = true, initialPath = path),
        )
        assertTrue(createResult is DirectNativeResultV5.Success<*>)
        val handle = (createResult as DirectNativeResultV5.Success<*>).value
        assertTrue(handle is DirectNativeHandleV5)
        handle as DirectNativeHandleV5
        try {
            val sessionId = ByteArray(16) { 10 }
            DirectNativeDirectionV5.entries.forEach { direction ->
                assertTrue(
                    handle.deregisterSession(
                        DirectNativeCursorV5(path, direction, sessionId, highestContiguous = 0),
                    ) is DirectNativeResultV5.Success<*>,
                )
            }
        } finally {
            handle.close()
        }
    }
}
