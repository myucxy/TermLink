package dev.termlink.network.direct

import dev.termlink.protocol.realtime.v1.Direction
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import kotlin.concurrent.thread
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class ProductionDirectPeerRegistryTest {
    @Test
    fun directFailureRetiresOnlyTheExactPeerEpoch() {
        val registry = ProductionDirectPeerActorRegistry<FakeActor>()
        val first = FakeActor(peer(1), 7)
        val second = FakeActor(peer(2), 8)
        assertTrue(registry.install(first))
        assertTrue(registry.install(second))

        assertEquals(
            ProductionDirectPeerUpdateResult.APPLIED,
            registry.markDirectionActive(first.peerDeviceId, first.pathFence, Direction.PC_TO_ANDROID, ProductionDirectPathKind.LAN),
        )
        assertEquals(
            ProductionDirectPeerUpdateResult.APPLIED,
            registry.markDirectionActive(first.peerDeviceId, first.pathFence, Direction.ANDROID_TO_PC, ProductionDirectPathKind.LAN),
        )
        assertEquals(ProductionDirectPeerCarrier.LAN_DIRECT, registry.carrierFor(first.peerDeviceId))
        assertEquals(ProductionDirectPeerCarrier.NEGOTIATING_DIRECT, registry.carrierFor(second.peerDeviceId))

        assertTrue(registry.failPeer(first.peerDeviceId, first.pathFence))
        assertEquals(listOf(ProductionDirectPeerRetirementReason.DIRECT_FAILURE), first.retirements)
        assertEquals(ProductionDirectPeerCarrier.RELAY, registry.carrierFor(first.peerDeviceId))
        assertSame(second, registry.actorFor(second.peerDeviceId))
        assertEquals(ProductionDirectPeerCarrier.NEGOTIATING_DIRECT, registry.carrierFor(second.peerDeviceId))
        assertTrue(second.retirements.isEmpty())

        assertFalse(registry.failPeer(first.peerDeviceId, first.pathFence))
        assertFalse(registry.failPeer(second.peerDeviceId, first.pathFence))
        assertTrue(second.retirements.isEmpty())
        registry.close()
    }

    @Test
    fun carrierRequiresBothDirectionsOnTheSamePathKind() {
        val registry = ProductionDirectPeerActorRegistry<FakeActor>()
        val actor = FakeActor(peer(3), 11)
        registry.install(actor)
        assertEquals(ProductionDirectPeerCarrier.NEGOTIATING_DIRECT, registry.carrierFor(actor.peerDeviceId))

        registry.markDirectionActive(
            actor.peerDeviceId,
            actor.pathFence,
            Direction.ANDROID_TO_PC,
            ProductionDirectPathKind.NAT,
        )
        assertEquals(ProductionDirectPeerCarrier.NEGOTIATING_DIRECT, registry.carrierFor(actor.peerDeviceId))
        registry.markDirectionActive(
            actor.peerDeviceId,
            actor.pathFence,
            Direction.PC_TO_ANDROID,
            ProductionDirectPathKind.NAT,
        )
        assertEquals(ProductionDirectPeerCarrier.NAT_DIRECT, registry.carrierFor(actor.peerDeviceId))
        registry.close()
    }

    @Test
    fun mismatchedPathKindFailsClosedForOnlyThatPeer() {
        val registry = ProductionDirectPeerActorRegistry<FakeActor>()
        val invalid = FakeActor(peer(4), 12)
        val healthy = FakeActor(peer(5), 13)
        registry.install(invalid)
        registry.install(healthy)
        registry.markDirectionActive(
            invalid.peerDeviceId,
            invalid.pathFence,
            Direction.PC_TO_ANDROID,
            ProductionDirectPathKind.LAN,
        )
        registry.markDirectionActive(
            healthy.peerDeviceId,
            healthy.pathFence,
            Direction.PC_TO_ANDROID,
            ProductionDirectPathKind.NAT,
        )

        assertEquals(
            ProductionDirectPeerUpdateResult.PEER_RETIRED,
            registry.markDirectionActive(
                invalid.peerDeviceId,
                invalid.pathFence,
                Direction.ANDROID_TO_PC,
                ProductionDirectPathKind.NAT,
            ),
        )
        assertEquals(
            listOf(ProductionDirectPeerRetirementReason.PATH_KIND_MISMATCH),
            invalid.retirements,
        )
        assertNull(registry.actorFor(invalid.peerDeviceId))
        assertSame(healthy, registry.actorFor(healthy.peerDeviceId))
        assertTrue(healthy.retirements.isEmpty())
        registry.close()
    }

    @Test
    fun higherEpochReplacesOnlyItsPeerAndStaleInstallIsRetired() {
        val registry = ProductionDirectPeerActorRegistry<FakeActor>()
        val original = FakeActor(peer(6), 20)
        val unrelated = FakeActor(peer(7), 2)
        registry.install(original)
        registry.install(unrelated)

        val stale = FakeActor(original.peerDeviceId, 19)
        assertFalse(registry.install(stale))
        assertEquals(listOf(ProductionDirectPeerRetirementReason.STALE_INSTALL), stale.retirements)
        assertSame(original, registry.actorFor(original.peerDeviceId))

        val successor = FakeActor(original.peerDeviceId, 21)
        assertTrue(registry.install(successor))
        assertEquals(listOf(ProductionDirectPeerRetirementReason.REPLACED), original.retirements)
        assertSame(successor, registry.actorFor(original.peerDeviceId))
        assertSame(unrelated, registry.actorFor(unrelated.peerDeviceId))
        assertTrue(unrelated.retirements.isEmpty())

        assertEquals(
            ProductionDirectPeerUpdateResult.STALE,
            registry.markDirectionActive(
                original.peerDeviceId,
                original.pathFence,
                Direction.ANDROID_TO_PC,
                ProductionDirectPathKind.LAN,
            ),
        )
        assertEquals(
            ProductionDirectPeerCarrier.NEGOTIATING_DIRECT,
            registry.carrierFor(successor.peerDeviceId),
        )
        registry.close()
    }

    @Test
    fun retireAllClearsEveryCarrierButKeepsRegistryReusable() {
        val registry = ProductionDirectPeerActorRegistry<FakeActor>()
        val first = FakeActor(peer(10), 3)
        val second = FakeActor(peer(11), 4)
        registry.install(first)
        registry.install(second)

        registry.retireAll(ProductionDirectPeerRetirementReason.NETWORK_CHANGED)

        assertEquals(
            listOf(ProductionDirectPeerRetirementReason.NETWORK_CHANGED),
            first.retirements,
        )
        assertEquals(
            listOf(ProductionDirectPeerRetirementReason.NETWORK_CHANGED),
            second.retirements,
        )
        assertEquals(ProductionDirectPeerCarrier.RELAY, registry.carrierFor(first.peerDeviceId))
        assertEquals(ProductionDirectPeerCarrier.RELAY, registry.carrierFor(second.peerDeviceId))

        val replacement = FakeActor(first.peerDeviceId, 5)
        assertTrue(registry.install(replacement))
        assertSame(replacement, registry.actorFor(first.peerDeviceId))
        assertTrue(replacement.retirements.isEmpty())
        registry.close()
    }

    @Test
    fun sameEpochDifferentFenceCannotReplaceOrMutateCurrentActor() {
        val registry = ProductionDirectPeerActorRegistry<FakeActor>()
        val current = FakeActor(peer(13), 9, fenceSalt = 130)
        val conflicting = FakeActor(current.peerDeviceId, 9, fenceSalt = 131)
        assertTrue(registry.install(current))

        assertFalse(registry.install(conflicting))
        assertEquals(
            listOf(ProductionDirectPeerRetirementReason.STALE_INSTALL),
            conflicting.retirements,
        )
        assertSame(current, registry.actorFor(current.peerDeviceId))
        assertEquals(
            ProductionDirectPeerUpdateResult.STALE,
            registry.markDirectionActive(
                current.peerDeviceId,
                conflicting.pathFence,
                Direction.PC_TO_ANDROID,
                ProductionDirectPathKind.LAN,
            ),
        )
        assertFalse(registry.failPeer(current.peerDeviceId, conflicting.pathFence))
        assertSame(current, registry.actorFor(current.peerDeviceId))
        assertEquals(
            ProductionDirectPeerCarrier.NEGOTIATING_DIRECT,
            registry.carrierFor(current.peerDeviceId),
        )
        assertTrue(current.retirements.isEmpty())
        registry.close()
    }

    @Test
    fun slowPeerRetirementDoesNotHoldRegistryLockForHealthyPeer() {
        val registry = ProductionDirectPeerActorRegistry<ProductionDirectPeerActor>()
        val retirementEntered = CountDownLatch(1)
        val allowRetirement = CountDownLatch(1)
        val slow = BlockingActor(peer(14), 10, retirementEntered, allowRetirement)
        val healthy = FakeActor(peer(15), 11)
        registry.install(slow)
        registry.install(healthy)

        val retiringThread = thread(start = true, name = "direct-peer-retirement-test") {
            registry.failPeer(slow.peerDeviceId, slow.pathFence)
        }
        val updateResult = AtomicReference<ProductionDirectPeerUpdateResult>()
        val updateFinished = CountDownLatch(1)
        var updatingThread: Thread? = null
        try {
            assertTrue(retirementEntered.await(2, TimeUnit.SECONDS))
            updatingThread = thread(start = true, name = "healthy-direct-peer-update-test") {
                updateResult.set(registry.markDirectionActive(
                    healthy.peerDeviceId,
                    healthy.pathFence,
                    Direction.PC_TO_ANDROID,
                    ProductionDirectPathKind.NAT,
                ))
                updateFinished.countDown()
            }
            assertTrue(updateFinished.await(500, TimeUnit.MILLISECONDS))
            assertEquals(ProductionDirectPeerUpdateResult.APPLIED, updateResult.get())
            assertSame(healthy, registry.actorFor(healthy.peerDeviceId))
        } finally {
            allowRetirement.countDown()
            retiringThread.join(2_000)
            updatingThread?.join(2_000)
        }
        assertFalse(retiringThread.isAlive)
        assertFalse(updatingThread?.isAlive == true)
        registry.close()
    }

    @Test
    fun unspecifiedDirectionCannotActivateOrRetirePeer() {
        val registry = ProductionDirectPeerActorRegistry<FakeActor>()
        val actor = FakeActor(peer(12), 6)
        registry.install(actor)

        assertEquals(
            ProductionDirectPeerUpdateResult.STALE,
            registry.markDirectionActive(
                actor.peerDeviceId,
                actor.pathFence,
                Direction.DIRECTION_UNSPECIFIED,
                ProductionDirectPathKind.NAT,
            ),
        )
        assertSame(actor, registry.actorFor(actor.peerDeviceId))
        assertEquals(
            ProductionDirectPeerCarrier.NEGOTIATING_DIRECT,
            registry.carrierFor(actor.peerDeviceId),
        )
        assertTrue(actor.retirements.isEmpty())
        registry.close()
    }

    @Test
    fun closeIsIdempotentAndRejectsFutureActors() {
        val registry = ProductionDirectPeerActorRegistry<FakeActor>()
        val active = FakeActor(peer(8), 1)
        registry.install(active)
        registry.close()
        registry.close()
        assertEquals(listOf(ProductionDirectPeerRetirementReason.CLOSED), active.retirements)

        val late = FakeActor(peer(9), 1)
        assertFalse(registry.install(late))
        assertEquals(listOf(ProductionDirectPeerRetirementReason.CLOSED), late.retirements)
        assertEquals(ProductionDirectPeerCarrier.RELAY, registry.carrierFor(late.peerDeviceId))
    }

    private class FakeActor(
        override val peerDeviceId: UUID,
        pathEpoch: Long,
        fenceSalt: Long = peerDeviceId.leastSignificantBits,
    ) : ProductionDirectPeerActor {
        override val pathFence = ProductionDirectPathFence(
            pathEpoch = pathEpoch,
            connectionId = UUID(0x2000, fenceSalt),
            attemptId = UUID(0x3000, fenceSalt),
        )
        val retirements = mutableListOf<ProductionDirectPeerRetirementReason>()

        override fun retire(reason: ProductionDirectPeerRetirementReason) {
            retirements += reason
        }
    }

    private class BlockingActor(
        override val peerDeviceId: UUID,
        pathEpoch: Long,
        private val retirementEntered: CountDownLatch,
        private val allowRetirement: CountDownLatch,
    ) : ProductionDirectPeerActor {
        override val pathFence = ProductionDirectPathFence(
            pathEpoch = pathEpoch,
            connectionId = UUID(0x4000, peerDeviceId.leastSignificantBits),
            attemptId = UUID(0x5000, peerDeviceId.leastSignificantBits),
        )

        override fun retire(reason: ProductionDirectPeerRetirementReason) {
            retirementEntered.countDown()
            check(allowRetirement.await(2, TimeUnit.SECONDS))
        }
    }

    private fun peer(value: Long): UUID = UUID(0x1000, value)
}
