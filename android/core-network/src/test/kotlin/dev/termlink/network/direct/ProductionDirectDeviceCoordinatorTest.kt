package dev.termlink.network.direct

import dev.termlink.network.direct.nativebridge.DirectNativeDirectionV5
import java.util.UUID
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking

class ProductionDirectDeviceCoordinatorTest {
    @Test
    fun shadowDiscoveryAndProductionDataHaveIndependentFailClosedGates(): Unit {
        val staged = ProductionDirectFeatureGates(
            shadowDiscoveryEnabled = true,
            dataPlaneEnabled = false,
        )
        assertTrue(staged.shadowDiscoveryEnabled)
        assertFalse(staged.dataPlaneEnabled)
        assertFailsWith<IllegalArgumentException> {
            ProductionDirectFeatureGates(shadowDiscoveryEnabled = false, dataPlaneEnabled = true)
        }
    }

    @Test
    fun pendingPromotionOwnsAndClearsUnclaimedBinding(): Unit {
        val binding = ByteArray(32) { (it + 1).toByte() }
        val promoted = promoted(binding)

        promoted.close()

        assertNull(promoted.claimIncrementalCutover(setOf(UUID(0, 4))))
        assertTrue(binding.all { it == 0.toByte() })
    }

    @Test
    fun concurrentActivationCanClaimOneIncrementalBatchExactlyOnce(): Unit = runBlocking {
        val binding = ByteArray(32) { (it + 1).toByte() }
        val promoted = promoted(binding)
        val firstSession = UUID(0, 4)
        val lateSession = UUID(0, 5)

        val claims = List(16) {
            async(Dispatchers.Default) { promoted.claimIncrementalCutover(setOf(firstSession)) }
        }.awaitAll()

        assertEquals(1, claims.count { it != null })
        val claim = claims.single { it != null }!!
        assertTrue(claim.directPathBinding.contentEquals(binding))
        assertTrue(claim.directPathBinding !== binding)
        assertEquals(
            ProductionDirectSessionProjection.NEGOTIATING,
            promoted.sessionProjection(firstSession),
        )
        promoted.replaceCommittedSessions(
            DirectNativeDirectionV5.ANDROID_TO_PC,
            setOf(firstSession),
        )
        promoted.replaceCommittedSessions(
            DirectNativeDirectionV5.PC_TO_ANDROID,
            setOf(firstSession),
        )
        assertEquals(
            ProductionDirectSessionProjection.DIRECT,
            promoted.sessionProjection(firstSession),
        )
        val incremental = assertNotNull(
            promoted.claimIncrementalCutover(setOf(firstSession, lateSession)),
        )
        assertEquals(setOf(lateSession), incremental.sessionIds)
        assertEquals(
            ProductionDirectSessionProjection.DIRECT,
            promoted.sessionProjection(firstSession),
        )
        assertEquals(
            ProductionDirectSessionProjection.NEGOTIATING,
            promoted.sessionProjection(lateSession),
        )
        promoted.abortIncrementalCutover(incremental.sessionIds)
        assertEquals(
            ProductionDirectSessionProjection.RELAY,
            promoted.sessionProjection(lateSession),
        )
        incremental.close()
        assertTrue(incremental.directPathBinding.all { it == 0.toByte() })
        claim.close()
        assertTrue(claim.directPathBinding.all { it == 0.toByte() })
        promoted.close()
        assertTrue(binding.all { it == 0.toByte() })
        assertEquals(
            ProductionDirectSessionProjection.RELAY,
            promoted.sessionProjection(firstSession),
        )
    }

    @Test
    fun promotedBindingRejectsInvalidSecretLength(): Unit {
        assertFailsWith<IllegalArgumentException> {
            promoted(ByteArray(31))
        }
    }

    @Test
    fun incrementalClaimsAreStableAndBoundedToNativeSessionCapacity() {
        val promoted = promoted(ByteArray(32) { (it + 1).toByte() })
        val sessions = (1..40).map { UUID(0, it.toLong()) }.reversed().toSet()

        val first = assertNotNull(promoted.claimIncrementalCutover(sessions))
        val expectedFirst = sessions.sortedBy(UUID::toString).take(32)
        assertEquals(expectedFirst, first.sessionIds.toList())
        promoted.replaceCommittedSessions(
            DirectNativeDirectionV5.ANDROID_TO_PC,
            first.sessionIds,
        )
        first.close()

        val second = assertNotNull(promoted.claimIncrementalCutover(sessions))
        assertEquals(sessions.sortedBy(UUID::toString).drop(32), second.sessionIds.toList())
        second.close()
        promoted.close()
    }

    @Test
    fun lateSessionProjectsDirectOnlyAfterBothNativeDirectionsCommit() {
        val promoted = promoted(ByteArray(32) { (it + 1).toByte() })
        val session = UUID(0, 44)

        promoted.observeSessionCarrier(
            DirectNativeDirectionV5.ANDROID_TO_PC,
            session,
            dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5.LAN_DIRECT,
        )
        assertEquals(ProductionDirectSessionProjection.NEGOTIATING, promoted.sessionProjection(session))

        promoted.observeSessionCarrier(
            DirectNativeDirectionV5.PC_TO_ANDROID,
            session,
            dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5.NAT_DIRECT,
        )
        assertEquals(ProductionDirectSessionProjection.DIRECT, promoted.sessionProjection(session))

        promoted.observeSessionCarrier(
            DirectNativeDirectionV5.PC_TO_ANDROID,
            session,
            dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5.RELAY,
        )
        assertEquals(ProductionDirectSessionProjection.NEGOTIATING, promoted.sessionProjection(session))

        promoted.removeSession(session)
        assertEquals(ProductionDirectSessionProjection.RELAY, promoted.sessionProjection(session))
        promoted.close()
    }

    private fun promoted(binding: ByteArray) = ProductionDirectPromotedPeerCutover(
        peerDeviceId = UUID.fromString("10000000-0000-0000-0000-000000000001"),
        pathFence = ProductionDirectPathFence(
            pathEpoch = 7,
            connectionId = UUID.fromString("20000000-0000-0000-0000-000000000002"),
            attemptId = UUID.fromString("30000000-0000-0000-0000-000000000003"),
        ),
        keyEpoch = 11,
        directPathBinding = binding,
    )
}
