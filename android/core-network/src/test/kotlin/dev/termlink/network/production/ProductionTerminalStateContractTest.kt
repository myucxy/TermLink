package dev.termlink.network.production

import dev.termlink.protocol.realtime.v1.TerminalAttachResultPayload
import org.junit.Assert.assertEquals
import org.junit.Test
import kotlin.test.assertFailsWith

class ProductionTerminalStateContractTest {
    @Test
    fun acceptsOnlyKnownExactProfiles() {
        assertEquals(listOf(
            ProductionTerminalProfile.GRAPHICS_UNICODE16.state,
            ProductionTerminalProfile.GRAPHICS.state,
            ProductionTerminalProfile.LEGACY.state,
        ), ProductionTerminalStateContract.SUPPORTED_STATES)
        for (profile in ProductionTerminalProfile.entries) {
            assertEquals(profile, ProductionTerminalStateContract.requireAccepted(
                TerminalAttachResultPayload.newBuilder().setRequiredState(profile.state).build(),
            ))
            assertEquals(if (profile == ProductionTerminalProfile.GRAPHICS_UNICODE16) 2 else 1,
                profile.widthPolicy)
            assertEquals(profile != ProductionTerminalProfile.LEGACY, profile.isGraphics)
            assertEquals(profile.state, profile.stateContract)
            assertEquals(0, profile.outputProfile)
            assertEquals(2, profile.resourceProfile)
        }
        for (state in listOf("", "terminal_state_2_22_v3", "terminal_state_2_22_v1 ")) {
            assertEquals("TERMINAL_STATE_UNSUPPORTED", assertFailsWith<TerminalCompatibilityException> {
                ProductionTerminalStateContract.requireAccepted(TerminalAttachResultPayload.newBuilder().setRequiredState(state).build())
            }.code)
        }
    }

    @Test
    fun hostErrorCannotBeOverriddenByKnownProfile() {
        assertEquals("TERMINAL_CLIENT_UPGRADE_REQUIRED", assertFailsWith<TerminalCompatibilityException> {
            ProductionTerminalStateContract.requireAccepted(TerminalAttachResultPayload.newBuilder()
                .setRequiredState(ProductionTerminalProfile.GRAPHICS.state)
                .setErrorCode("TERMINAL_CLIENT_UPGRADE_REQUIRED").build())
        }.code)
    }

    @Test
    fun graphicsEnvelopeNeeds257ChunksWithoutExpandingLegacyBudget() {
        val legacy = ProductionTerminalProfile.LEGACY
        val graphics = ProductionTerminalProfile.GRAPHICS
        val unicode16 = ProductionTerminalProfile.GRAPHICS_UNICODE16
        assertEquals(128, legacy.maxHostHistoryChunks)
        assertEquals(257, graphics.maxHostHistoryChunks)
        assertEquals(graphics.maxHostHistoryChunks, unicode16.maxHostHistoryChunks)
        assertEquals(128, legacy.hostHistoryChunkCount(64L * 1024 * 1024))
        assertEquals(256, graphics.hostHistoryChunkCount(128L * 1024 * 1024))
        assertEquals(257, graphics.hostHistoryChunkCount(128L * 1024 * 1024 + 1))
        assertEquals(257, graphics.hostHistoryChunkCount(128L * 1024 * 1024 + 4096))
        assertFailsWith<IllegalArgumentException> { legacy.hostHistoryChunkCount(64L * 1024 * 1024 + 1) }
        for (size in listOf(0L, -1L, 128L * 1024 * 1024 + 4097, Long.MAX_VALUE)) {
            assertFailsWith<IllegalArgumentException> { graphics.hostHistoryChunkCount(size) }
        }
    }
}
