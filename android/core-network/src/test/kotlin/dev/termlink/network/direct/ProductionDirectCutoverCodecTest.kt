package dev.termlink.network.direct

import com.google.protobuf.ByteString
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.transport.v1.DirectCutoverProposalV1
import dev.termlink.protocol.transport.v1.SessionCutoverBarrierV1
import java.util.UUID
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class ProductionDirectCutoverCodecTest {
    @Test
    fun fixedAndroidToPcProposalMatchesWindowsWireAndDigestVector() {
        val binding = (1..32).map(Int::toByte).toByteArray()
        val mutableBarriers = mutableListOf(
            barrier("ffffffff-0000-0000-0000-000000000001", 300),
            barrier("00000000-ffff-eeee-dddd-ccccbbbb0002", 1),
        )

        val proposal = ProductionDirectCutoverCodec.createProposal(
            connectionId = CONNECTION_ID,
            pathEpoch = PATH_EPOCH,
            direction = Direction.ANDROID_TO_PC,
            barrierId = BARRIER_ID,
            directPathBinding = binding,
            sessionBarriers = mutableBarriers,
            duplicateGraceMilliseconds = 1_250,
        )

        assertArrayEquals(EXPECTED_WIRE_HEX.hexToBytes(), proposal.wireBytes)
        assertArrayEquals(EXPECTED_DIGEST_HEX.hexToBytes(), proposal.barrierSha256)
        assertEquals(BARRIER_ID, proposal.barrierId)
        assertEquals(Direction.ANDROID_TO_PC, proposal.direction)
        assertEquals(1_250, proposal.duplicateGraceMilliseconds)
        assertEquals(
            listOf(
                UUID.fromString("00000000-ffff-eeee-dddd-ccccbbbb0002"),
                UUID.fromString("ffffffff-0000-0000-0000-000000000001"),
            ),
            proposal.sessionBarriers.map { it.sessionId },
        )

        binding.fill(0)
        mutableBarriers.clear()
        val exposedWire = proposal.wireBytes.also { it.fill(0) }
        val exposedBinding = proposal.directPathBinding.also { it.fill(0) }
        val exposedDigest = proposal.barrierSha256.also { it.fill(0) }
        assertTrue(exposedWire.all { it == 0.toByte() })
        assertTrue(exposedBinding.all { it == 0.toByte() })
        assertTrue(exposedDigest.all { it == 0.toByte() })
        assertArrayEquals(EXPECTED_WIRE_HEX.hexToBytes(), proposal.wireBytes)
        assertArrayEquals((1..32).map(Int::toByte).toByteArray(), proposal.directPathBinding)
        assertArrayEquals(EXPECTED_DIGEST_HEX.hexToBytes(), proposal.barrierSha256)
        assertThrows(UnsupportedOperationException::class.java) {
            (proposal.sessionBarriers as MutableList).add(barrier(UUID.randomUUID().toString(), 4))
        }
    }

    @Test
    fun parseAcceptsOnlyStrictCanonicalWireAndRetainsIndependentDirection() {
        val wire = EXPECTED_WIRE_HEX.hexToBytes()
        val parsed = ProductionDirectCutoverCodec.tryParseProposal(
            exactWireBytes = wire,
            connectionId = CONNECTION_ID,
            pathEpoch = PATH_EPOCH,
            expectedDirection = Direction.ANDROID_TO_PC,
        )

        assertNotNull(parsed)
        requireNotNull(parsed)
        wire.fill(0)
        assertArrayEquals(EXPECTED_WIRE_HEX.hexToBytes(), parsed.wireBytes)
        assertArrayEquals(EXPECTED_DIGEST_HEX.hexToBytes(), parsed.barrierSha256)
        val message = DirectCutoverProposalV1.parseFrom(parsed.wireBytes)
        assertTrue(message.sessionBarriersList.all { it.hasAndroidToPcFirstDirectSeq() })
        assertTrue(message.sessionBarriersList.none { it.hasPcToAndroidFirstDirectSeq() })

        val reverse = ProductionDirectCutoverCodec.createProposal(
            connectionId = CONNECTION_ID,
            pathEpoch = PATH_EPOCH,
            direction = Direction.PC_TO_ANDROID,
            barrierId = BARRIER_ID,
            directPathBinding = BINDING,
            sessionBarriers = listOf(barrier("00000000-ffff-eeee-dddd-ccccbbbb0002", 1)),
            duplicateGraceMilliseconds = 0,
        )
        val reverseMessage = DirectCutoverProposalV1.parseFrom(reverse.wireBytes)
        assertTrue(reverseMessage.sessionBarriersList.single().hasPcToAndroidFirstDirectSeq())
        assertTrue(!reverseMessage.sessionBarriersList.single().hasAndroidToPcFirstDirectSeq())
        assertNotNull(
            ProductionDirectCutoverCodec.tryParseProposal(
                reverse.wireBytes,
                CONNECTION_ID,
                PATH_EPOCH,
                Direction.PC_TO_ANDROID,
            ),
        )
    }

    @Test
    fun parserRejectsWrongDirectionOrderingDuplicatesAndDirectionAmbiguity() {
        val valid = EXPECTED_WIRE_HEX.hexToBytes()
        assertRejected(valid, Direction.PC_TO_ANDROID)

        val first = barrierMessage("ffffffff-0000-0000-0000-000000000001", 3)
        val second = barrierMessage("00000000-ffff-eeee-dddd-ccccbbbb0002", 4)
        assertRejected(messageWire(first, second))
        assertRejected(messageWire(second, second))

        val bothDirections = second.toBuilder()
            .setPcToAndroidFirstDirectSeq(4)
            .build()
        assertRejected(messageWire(bothDirections))
        val noDirection = second.toBuilder()
            .clearAndroidToPcFirstDirectSeq()
            .build()
        assertRejected(messageWire(noDirection))
        val zeroSequence = second.toBuilder()
            .setAndroidToPcFirstDirectSeq(0)
            .build()
        assertRejected(messageWire(zeroSequence))
        val unsignedAboveLongMax = second.toBuilder()
            .setAndroidToPcFirstDirectSeq(Long.MIN_VALUE)
            .build()
        assertRejected(messageWire(unsignedAboveLongMax))
        val unsignedGraceAboveIntMax = DirectCutoverProposalV1.newBuilder()
            .setBarrierId(ByteString.copyFrom(uuidBytes(BARRIER_ID)))
            .setDirectPathBinding(ByteString.copyFrom(BINDING))
            .addSessionBarriers(second)
            .setDuplicateGraceMs(-1)
            .build()
            .toByteArray()
        assertRejected(unsignedGraceAboveIntMax)
        assertRejected(
            DirectCutoverProposalV1.newBuilder()
                .setBarrierId(ByteString.copyFrom(ByteArray(16)))
                .setDirectPathBinding(ByteString.copyFrom(BINDING))
                .addSessionBarriers(second)
                .build()
                .toByteArray(),
        )
        assertRejected(
            DirectCutoverProposalV1.newBuilder()
                .setBarrierId(ByteString.copyFrom(uuidBytes(BARRIER_ID)))
                .setDirectPathBinding(ByteString.copyFrom(ByteArray(32)))
                .addSessionBarriers(second)
                .build()
                .toByteArray(),
        )
        assertRejected(
            messageWire(
                second.toBuilder()
                    .setSessionId(ByteString.copyFrom(ByteArray(16)))
                    .build(),
            ),
        )
    }

    @Test
    fun parserRejectsUnknownDuplicateNonMinimalAndNonCanonicalFields() {
        val valid = EXPECTED_WIRE_HEX.hexToBytes()
        val unknownField = valid + byteArrayOf(0xa0.toByte(), 0x06, 0x01)
        val duplicateBarrierId = valid + byteArrayOf(0x0a, 0x10) + uuidBytes(BARRIER_ID)
        val nonMinimalGrace = valid.copyOfRange(0, valid.size - 3) +
            byteArrayOf(0x20, 0xe2.toByte(), 0x89.toByte(), 0x00)
        val graceBeforeKnownFields = byteArrayOf(0x20, 0xe2.toByte(), 0x09) +
            valid.copyOfRange(0, valid.size - 3)

        assertRejected(unknownField)
        assertRejected(duplicateBarrierId)
        assertRejected(nonMinimalGrace)
        assertRejected(graceBeforeKnownFields)
        assertRejected(byteArrayOf(0x0a))
        assertRejected(ByteArray(ProductionDirectCutoverCodec.MAXIMUM_PROPOSAL_BYTES + 1))
    }

    @Test
    fun creationRejectsInvalidOwnersBindingsBarriersSequencesAndGrace() {
        val validBarrier = barrier("00000000-ffff-eeee-dddd-ccccbbbb0002", 1)
        fun create(
            connectionId: UUID = CONNECTION_ID,
            pathEpoch: Long = PATH_EPOCH,
            direction: Direction = Direction.ANDROID_TO_PC,
            barrierId: UUID = BARRIER_ID,
            binding: ByteArray = BINDING,
            barriers: List<ProductionDirectSessionCutoverBarrier> = listOf(validBarrier),
            grace: Int = 0,
        ) = ProductionDirectCutoverCodec.createProposal(
            connectionId,
            pathEpoch,
            direction,
            barrierId,
            binding,
            barriers,
            grace,
        )

        assertThrows(IllegalArgumentException::class.java) { create(connectionId = ZERO_UUID) }
        assertThrows(IllegalArgumentException::class.java) { create(pathEpoch = 0) }
        assertThrows(IllegalArgumentException::class.java) { create(pathEpoch = -1) }
        assertThrows(IllegalArgumentException::class.java) {
            create(direction = Direction.DIRECTION_UNSPECIFIED)
        }
        assertThrows(IllegalArgumentException::class.java) { create(barrierId = ZERO_UUID) }
        assertThrows(IllegalArgumentException::class.java) { create(binding = ByteArray(31) { 1 }) }
        assertThrows(IllegalArgumentException::class.java) { create(binding = ByteArray(32)) }
        assertThrows(IllegalArgumentException::class.java) { create(barriers = emptyList()) }
        assertThrows(IllegalArgumentException::class.java) {
            create(barriers = List(33) { index -> barrier(UUID(1, index.toLong() + 1).toString(), 1) })
        }
        assertThrows(IllegalArgumentException::class.java) {
            create(barriers = listOf(validBarrier, validBarrier.copy(firstDirectSequence = 2)))
        }
        assertThrows(IllegalArgumentException::class.java) {
            create(barriers = listOf(ProductionDirectSessionCutoverBarrier(ZERO_UUID, 1)))
        }
        assertThrows(IllegalArgumentException::class.java) {
            create(barriers = listOf(validBarrier.copy(firstDirectSequence = 0)))
        }
        assertThrows(IllegalArgumentException::class.java) { create(grace = -1) }
        assertThrows(IllegalArgumentException::class.java) { create(grace = 5_001) }
    }

    @Test
    fun exactLimitsAreAcceptedAndUnsignedUuidBytesDetermineOrder() {
        val barriers = (0 until ProductionDirectCutoverCodec.MAXIMUM_SESSION_BARRIERS).map { index ->
            ProductionDirectSessionCutoverBarrier(
                sessionId = UUID(index.toLong() shl 59, index.toLong() + 1),
                firstDirectSequence = if (index == 31) Long.MAX_VALUE else index.toLong() + 1,
            )
        }.reversed()
        val proposal = ProductionDirectCutoverCodec.createProposal(
            CONNECTION_ID,
            PATH_EPOCH,
            Direction.PC_TO_ANDROID,
            BARRIER_ID,
            BINDING,
            barriers,
            ProductionDirectCutoverCodec.MAXIMUM_DUPLICATE_GRACE_MILLISECONDS,
        )

        assertEquals(32, proposal.sessionBarriers.size)
        assertEquals(barriers.last().sessionId, proposal.sessionBarriers.first().sessionId)
        assertEquals(Long.MAX_VALUE, proposal.sessionBarriers.last().firstDirectSequence)
        assertTrue(proposal.wireBytes.size <= ProductionDirectCutoverCodec.MAXIMUM_PROPOSAL_BYTES)
        assertNotNull(
            ProductionDirectCutoverCodec.tryParseProposal(
                proposal.wireBytes,
                CONNECTION_ID,
                PATH_EPOCH,
                Direction.PC_TO_ANDROID,
            ),
        )

        val unsignedOrder = ProductionDirectCutoverCodec.createProposal(
            CONNECTION_ID,
            PATH_EPOCH,
            Direction.ANDROID_TO_PC,
            BARRIER_ID,
            BINDING,
            listOf(
                barrier("80000000-0000-0000-0000-000000000001", 2),
                barrier("7fffffff-ffff-ffff-ffff-ffffffffffff", 1),
            ),
            0,
        )
        assertEquals(
            UUID.fromString("7fffffff-ffff-ffff-ffff-ffffffffffff"),
            unsignedOrder.sessionBarriers.first().sessionId,
        )
    }

    @Test
    fun digestRejectsInvalidOwnerEpochAndWireBounds() {
        assertThrows(IllegalArgumentException::class.java) {
            ProductionDirectCutoverCodec.computeBarrierSha256(ZERO_UUID, PATH_EPOCH, byteArrayOf(1))
        }
        assertThrows(IllegalArgumentException::class.java) {
            ProductionDirectCutoverCodec.computeBarrierSha256(CONNECTION_ID, 0, byteArrayOf(1))
        }
        assertThrows(IllegalArgumentException::class.java) {
            ProductionDirectCutoverCodec.computeBarrierSha256(CONNECTION_ID, PATH_EPOCH, ByteArray(0))
        }
        assertThrows(IllegalArgumentException::class.java) {
            ProductionDirectCutoverCodec.computeBarrierSha256(
                CONNECTION_ID,
                PATH_EPOCH,
                ByteArray(ProductionDirectCutoverCodec.MAXIMUM_PROPOSAL_BYTES + 1),
            )
        }
    }

    private fun assertRejected(
        wire: ByteArray,
        expectedDirection: Direction = Direction.ANDROID_TO_PC,
    ) {
        assertNull(
            ProductionDirectCutoverCodec.tryParseProposal(
                exactWireBytes = wire,
                connectionId = CONNECTION_ID,
                pathEpoch = PATH_EPOCH,
                expectedDirection = expectedDirection,
            ),
        )
    }

    private fun messageWire(vararg barriers: SessionCutoverBarrierV1): ByteArray =
        DirectCutoverProposalV1.newBuilder()
            .setBarrierId(ByteString.copyFrom(uuidBytes(BARRIER_ID)))
            .setDirectPathBinding(ByteString.copyFrom(BINDING))
            .addAllSessionBarriers(barriers.toList())
            .setDuplicateGraceMs(1_250)
            .build()
            .toByteArray()

    private fun barrierMessage(id: String, sequence: Long): SessionCutoverBarrierV1 =
        SessionCutoverBarrierV1.newBuilder()
            .setSessionId(ByteString.copyFrom(uuidBytes(UUID.fromString(id))))
            .setAndroidToPcFirstDirectSeq(sequence)
            .build()

    private fun barrier(id: String, sequence: Long) = ProductionDirectSessionCutoverBarrier(
        sessionId = UUID.fromString(id),
        firstDirectSequence = sequence,
    )

    private fun uuidBytes(value: UUID): ByteArray = value.toString().replace("-", "").hexToBytes()

    private fun String.hexToBytes(): ByteArray {
        require(length % 2 == 0)
        return ByteArray(length / 2) { index ->
            substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
    }

    private companion object {
        val CONNECTION_ID: UUID = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        const val PATH_EPOCH: Long = 0x0102030405060708
        val BARRIER_ID: UUID = UUID.fromString("fedcba98-7654-3210-ffff-eeee00001111")
        val ZERO_UUID: UUID = UUID(0, 0)
        val BINDING: ByteArray = (1..32).map(Int::toByte).toByteArray()
        const val EXPECTED_WIRE_HEX =
            "0a10fedcba9876543210ffffeeee00001111" +
                "12200102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20" +
                "1a140a1000000000ffffeeeeddddccccbbbb00021801" +
                "1a150a10ffffffff00000000000000000000000118ac02" +
                "20e209"
        const val EXPECTED_DIGEST_HEX = "8322fe77aca31e9df68620a041e9b596a84f00357a290cc88a1493a5e63303ec"
    }
}
