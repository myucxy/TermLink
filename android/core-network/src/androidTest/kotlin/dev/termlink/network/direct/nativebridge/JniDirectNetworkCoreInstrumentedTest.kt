package dev.termlink.network.direct.nativebridge

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class JniDirectNetworkCoreInstrumentedTest {
    @Test
    fun abiV4LayoutMatchesTheIndependentMultipairHeader() {
        val contract = (JniDirectNetworkCoreV4().abiContract() as
            DirectNativeResultV4.Success).value
        assertEquals(DIRECT_NATIVE_ABI_VERSION_V4, contract.abiVersion)
        assertTrue(contract.isCompatible)
        assertTrue(!JniDirectNetworkCoreV4().directRoutingPermitted)
    }

    @Test
    fun disabledV4CreateAndGatherReturnWithoutNetworkIo() {
        val handle = (JniDirectNetworkCoreV4().create(
            DirectNativeCreateConfigV4(enabled = false),
        ) as DirectNativeResultV4.Success).value
        handle.use {
            assertTrue(it.gather(disabledGatherRequestV4(networkHandle = 1)) is
                DirectNativeResultV4.Success)
            val deadline = System.nanoTime() + 2_000_000_000L
            var disabled: DirectNativeEventV4? = null
            while (System.nanoTime() < deadline && disabled == null) {
                val event = (it.poll() as DirectNativeResultV4.Success).value
                if (event?.kind == DirectNativeEventKindV4.DIRECT_FAILED) disabled = event
                if (disabled == null) Thread.yield()
            }
            assertEquals(DirectNativePathStateV4.RELAY_ONLY, disabled?.state)
            assertEquals(1, disabled?.reason)
        }
    }

    @Test
    fun abiVersionAndEveryNativeStructSizeMatchThePackagedHeader() {
        val contract = (JniDirectNetworkCore().abiContract() as DirectNativeResult.Success).value
        assertEquals(DIRECT_NATIVE_ABI_VERSION, contract.abiVersion)
        assertEquals(32, contract.createSize)
        assertEquals(24, contract.socketAddressSize)
        assertEquals(176, contract.gatherSize)
        assertEquals(2_696, contract.nominateSize)
        assertEquals(80, contract.candidatesSize)
        assertEquals(32, contract.controlSize)
        assertEquals(176, contract.signatureSize)
        assertEquals(2_136, contract.eventSize)
        assertEquals(40, contract.gatherLocalBindOffset)
        assertEquals(88, contract.gatherStunServersOffset)
        assertEquals(144, contract.gatherReservedOffset)
        assertEquals(8, contract.nominateStartV2Offset)
        assertEquals(2_600, contract.nominateLocalSendProbeKeyOffset)
        assertEquals(2_632, contract.nominatePeerReceiveProbeKeyOffset)
        assertEquals(2_664, contract.nominateReservedOffset)
        assertEquals(8, contract.candidatesArrayOffset)
        assertEquals(208, contract.startTicketOffset)
        assertEquals(2_288, contract.startPeerCertificateSha256Offset)
        assertEquals(2_552, contract.startNowMillisOffset)
        assertEquals(32, contract.eventGenerationOffset)
        assertEquals(88, contract.eventPayloadOffset)
        assertTrue(contract.isCompatible)
    }

    @Test
    fun disabledShadowCreateAndGatherReturnWithoutNetworkIo() {
        val core = JniDirectNetworkCore()
        val handle = (core.create(DirectNativeCreateConfig(enabled = false)) as
            DirectNativeResult.Success).value
        // Disabled mode validates the non-zero ABI field and returns before socket creation.
        // A synthetic non-zero value keeps this zero-permission test independent of network state.
        val request = disabledGatherRequest(networkHandle = 1L)
        handle.use {
            assertTrue(it.gather(request) is DirectNativeResult.Success)
            val deadline = System.nanoTime() + 2_000_000_000L
            var disabled: DirectNativeEvent? = null
            while (System.nanoTime() < deadline && disabled == null) {
                val event = (it.poll() as DirectNativeResult.Success).value
                if (event?.kind == DirectNativeEventKind.DIRECT_FAILED) disabled = event
                if (disabled == null) Thread.yield()
            }
            assertEquals(DirectNativePathState.RELAY_ONLY, disabled?.state)
            assertEquals(1, disabled?.reason)
        }
    }

    private fun disabledGatherRequest(networkHandle: Long) = DirectNativeGatherRequest(
        epoch = 1,
        attemptId = ByteArray(16) { 1 },
        networkHandle = networkHandle,
        localBind = DirectNativeSocketAddress(byteArrayOf(10, 0, 0, 2), 0),
        advertisedLocalIp = byteArrayOf(10, 0, 0, 2),
        stunServers = emptyList(),
        gatherTimeoutMillis = 500,
    )

    private fun disabledGatherRequestV4(networkHandle: Long) = DirectNativeGatherRequestV4(
        epoch = 1,
        attemptId = ByteArray(16) { 1 },
        networkHandle = networkHandle,
        localBind = DirectNativeSocketAddressV4(byteArrayOf(10, 0, 0, 2), 0),
        advertisedLocalIp = byteArrayOf(10, 0, 0, 2),
        networkCost = 0,
        candidateExpiresAtMillis = System.currentTimeMillis() + 60_000,
        stunServers = emptyList(),
        gatherTimeoutMillis = 500,
    )
}
