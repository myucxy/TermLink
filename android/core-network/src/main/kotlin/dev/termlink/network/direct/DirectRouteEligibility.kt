package dev.termlink.network.direct

import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.MessageType

/**
 * Fail-closed policy for admitting an existing realtime Envelope to the optional Direct route.
 *
 * Subscription, replay, flow-control, lease, lifecycle, termination, snapshot and host-history
 * traffic remains on Relay. Those messages either maintain server authority or change recovery
 * state that a peer-to-peer path must not bypass. The small allow-list below is deliberately
 * directional so a newly added protobuf enum cannot silently become Direct eligible.
 */
internal object DirectRouteEligibility {
    private val androidToPc = setOf(
        MessageType.INPUT_EVENT,
    )

    private val pcToAndroid = setOf(
        MessageType.OUTPUT_CHUNK,
    )

    fun isEligible(direction: Direction, messageType: MessageType): Boolean = when (direction) {
        Direction.ANDROID_TO_PC -> messageType in androidToPc
        Direction.PC_TO_ANDROID -> messageType in pcToAndroid
        Direction.DIRECTION_UNSPECIFIED,
        Direction.UNRECOGNIZED,
        -> false
    }

    fun isEligible(envelope: Envelope): Boolean =
        isEligible(envelope.direction, envelope.messageType)
}
