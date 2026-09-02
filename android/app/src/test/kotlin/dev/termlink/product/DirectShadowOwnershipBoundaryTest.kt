package dev.termlink.product

import dev.termlink.network.direct.ProductionDirectShadowRuntime
import java.util.Locale
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectShadowOwnershipBoundaryTest {
    @Test
    fun exactAuthenticationOwnerFenceRejectsStaleDirectRuntime() {
        val oldRuntime = Any()
        val successorRuntime = Any()

        assertFalse(authenticationRuntimeClaimMatches(
            currentOwner = successorRuntime,
            currentOwnerGeneration = 12,
            expectedOwner = oldRuntime,
            expectedGeneration = 12,
        ))
        assertTrue(authenticationRuntimeClaimMatches(
            currentOwner = successorRuntime,
            currentOwnerGeneration = 12,
            expectedOwner = successorRuntime,
            expectedGeneration = 12,
        ))
    }

    @Test
    fun directRuntimeExposesNoSessionRendererAckOrRelayControlSurface() {
        val forbidden = listOf(
            "disconnect",
            "reconnect",
            "sendEnvelope",
            "registerSession",
            "logout",
            "renderer",
            "snapshotHistory",
            "acknowledgement",
        )
        val methodNames = ProductionDirectShadowRuntime::class.java.methods
            .map { it.name.lowercase(Locale.ROOT) }
        val fieldTypes = ProductionDirectShadowRuntime::class.java.declaredFields
            .map { it.type.name.lowercase(Locale.ROOT) }

        forbidden.forEach { name ->
            assertFalse("unexpected Direct ownership surface: $name", methodNames.any { name in it })
        }
        listOf("envelope", "renderer", "terminal", "history", "snapshot", "ack", "sessionhost")
            .forEach { fragment ->
                assertFalse(
                    "unexpected Direct owned type: $fragment",
                    fieldTypes.any { fragment in it },
                )
            }
    }
}
