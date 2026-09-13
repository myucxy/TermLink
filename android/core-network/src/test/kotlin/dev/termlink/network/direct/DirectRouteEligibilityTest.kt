package dev.termlink.network.direct

import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.MessageType
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectRouteEligibilityTest {
    @Test
    fun everyMessageTypeIsExplicitlyFailClosedPerDirection() {
        val androidToPc = setOf(MessageType.INPUT_EVENT)
        val pcToAndroid = setOf(MessageType.OUTPUT_CHUNK)

        MessageType.entries.forEach { messageType ->
            assertEquals(
                "ANDROID_TO_PC $messageType",
                messageType in androidToPc,
                DirectRouteEligibility.isEligible(Direction.ANDROID_TO_PC, messageType),
            )
            assertEquals(
                "PC_TO_ANDROID $messageType",
                messageType in pcToAndroid,
                DirectRouteEligibility.isEligible(Direction.PC_TO_ANDROID, messageType),
            )
        }
    }

    @Test
    fun relayAuthorityAndRecoveryMessagesNeverBecomeDirectEligible() {
        val relayOnly = setOf(
            MessageType.HEARTBEAT,
            MessageType.GOODBYE,
            MessageType.SUBSCRIBE,
            MessageType.UNSUBSCRIBE,
            MessageType.OUTPUT_ACK,
            MessageType.FLOW_CONTROL,
            MessageType.RESIZE_REQUEST,
            MessageType.GAP,
            MessageType.INPUT_ACCEPTED,
            MessageType.INPUT_REJECTED,
            MessageType.RESIZE_APPLIED,
            MessageType.LEASE_REQUEST,
            MessageType.LEASE_GRANTED,
            MessageType.LEASE_DENIED,
            MessageType.LEASE_REVOKED,
            MessageType.SESSION_TERMINATE_REQUEST,
            MessageType.SESSION_TERMINATE_RESPONSE,
            MessageType.HOST_HISTORY_REQUEST,
            MessageType.HOST_HISTORY_RESPONSE,
            MessageType.HOST_HISTORY_CONTROL,
            MessageType.SNAPSHOT_OFFER,
            MessageType.SNAPSHOT_CHUNK,
            MessageType.RESUME_REQUEST,
        )
        for (messageType in relayOnly) {
            assertFalse(DirectRouteEligibility.isEligible(Direction.ANDROID_TO_PC, messageType))
            assertFalse(DirectRouteEligibility.isEligible(Direction.PC_TO_ANDROID, messageType))
        }
    }

    @Test
    fun unknownDirectionAndEnumValuesFailClosed() {
        assertFalse(
            DirectRouteEligibility.isEligible(Direction.DIRECTION_UNSPECIFIED, MessageType.INPUT_EVENT),
        )
        assertFalse(DirectRouteEligibility.isEligible(Direction.UNRECOGNIZED, MessageType.OUTPUT_CHUNK))
        assertFalse(DirectRouteEligibility.isEligible(Direction.ANDROID_TO_PC, MessageType.UNRECOGNIZED))
        assertFalse(DirectRouteEligibility.isEligible(Direction.PC_TO_ANDROID, MessageType.UNRECOGNIZED))
    }

    @Test
    fun envelopeClassifierUsesBothDirectionAndMessageType() {
        val eligible = Envelope.newBuilder()
            .setDirection(Direction.ANDROID_TO_PC)
            .setMessageType(MessageType.INPUT_EVENT)
            .build()
        val reversed = eligible.toBuilder().setDirection(Direction.PC_TO_ANDROID).build()

        assertTrue(DirectRouteEligibility.isEligible(eligible))
        assertFalse(DirectRouteEligibility.isEligible(reversed))
    }
}
