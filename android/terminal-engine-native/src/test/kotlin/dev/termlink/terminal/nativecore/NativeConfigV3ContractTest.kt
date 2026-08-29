package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs

class NativeConfigV3ContractTest {
    @Test
    fun handleV3MapsTheSemanticProfileAndCheckpointPreservesIt() {
        val bridge = FakeNativeBridge()

        val legacy = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 3)
        assertEquals(1, bridge.createCalls)
        assertEquals(0, bridge.createV3Calls)
        assertEquals(NATIVE_OUTPUT_PROFILE_UTF8, bridge.onlyStateForTest().outputProfile)
        legacy.close()

        val terminal = NativeTerminalHandle.create(
            bridge = bridge,
            size = TerminalSize(12, 4),
            initialNextSourceSequence = 7,
            outputProfile = TerminalOutputProfile.UTF8_WITH_STANDALONE_C1,
            historyCapacityRows = 23,
        )
        assertEquals(1, bridge.createCalls, "the legacy entry point must remain untouched")
        assertEquals(1, bridge.createV3Calls)
        assertEquals(
            NativeTerminalConfigV3(
                columns = 12,
                rows = 4,
                initialSourceSequence = 7,
                historyCapacityRows = 23,
                outputProfile = NATIVE_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1,
            ),
            bridge.lastConfigV3,
        )
        assertEquals(TERMLINK_CONFIG_V3_STRUCT_SIZE, bridge.lastConfigV3?.structSize)
        assertEquals(
            NATIVE_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1,
            bridge.onlyStateForTest().outputProfile,
        )
        assertEquals(23, bridge.onlyStateForTest().historyCapacityRows)

        val checkpoint = terminal.exportCheckpoint().copyPayload()
        terminal.close()
        val restored = NativeTerminalHandle.importCheckpoint(bridge, checkpoint)
        assertEquals(
            NATIVE_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1,
            bridge.onlyStateForTest().outputProfile,
        )
        assertEquals(23, bridge.onlyStateForTest().historyCapacityRows)
        assertEquals(7, restored.nextSourceSequence)
        restored.close()
    }

    @Test
    fun fakeAcceptsOnlyTheTwoRawProfileCodes() {
        for (profile in listOf(NATIVE_OUTPUT_PROFILE_UTF8, NATIVE_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1)) {
            val bridge = FakeNativeBridge()
            val allocation = assertIs<NativeBridgeResult.Success<NativeBridgeTerminal>>(
                bridge.createV3(validConfig(outputProfile = profile)),
            ).value
            assertEquals(profile, bridge.onlyStateForTest().outputProfile)
            assertIs<NativeBridgeResult.Success<Unit>>(bridge.destroy(allocation.handle))
        }

        for (profile in listOf(-1, 2, Int.MAX_VALUE)) {
            assertInvalidArgument(validConfig(outputProfile = profile))
        }
    }

    @Test
    fun fakeRequiresExactSizeAndEveryReservedFieldToBeZero() {
        assertEquals(88, TERMLINK_CONFIG_V3_STRUCT_SIZE)
        assertInvalidArgument(validConfig().copy(abiVersion = 0))
        assertInvalidArgument(validConfig().copy(abiVersion = 2))
        assertInvalidArgument(validConfig().copy(structSize = TERMLINK_CONFIG_V3_STRUCT_SIZE - 1))
        assertInvalidArgument(validConfig().copy(structSize = TERMLINK_CONFIG_V3_STRUCT_SIZE + 1))

        val reservedMutations = listOf(
            validConfig().copy(reservedV1Compat0 = 1),
            validConfig().copy(reservedV1Compat1 = 1),
            validConfig().copy(reservedV2Compat0 = 1),
            validConfig().copy(reservedV2Compat1 = 1),
            validConfig().copy(reserved0 = 1),
            validConfig().copy(reserved1 = 1),
            validConfig().copy(reserved2 = 1),
        )
        reservedMutations.forEach(::assertInvalidArgument)
    }

    @Test
    fun fakeValidatesUnsignedAndCoreBoundsBeforeAllocating() {
        assertInvalidArgument(validConfig().copy(initialSourceSequence = -1))
        assertInvalidArgument(validConfig().copy(initialSourceSequence = Long.MAX_VALUE))
        assertInvalidArgument(validConfig().copy(historyCapacityRows = -1))
        assertInvalidArgument(validConfig().copy(historyCapacityRows = UNSIGNED_INT_MAX + 1))

        assertStatus(
            validConfig().copy(columns = 0),
            NativeTerminalStatus.INVALID_DIMENSIONS,
        )
        assertStatus(
            validConfig().copy(rows = 0),
            NativeTerminalStatus.INVALID_DIMENSIONS,
        )
        // Negative Int values are large raw uint32 values, not signed dimensions.
        assertStatus(validConfig().copy(columns = -1), NativeTerminalStatus.LIMIT_EXCEEDED)
        assertStatus(validConfig().copy(rows = -1), NativeTerminalStatus.LIMIT_EXCEEDED)
        assertStatus(validConfig().copy(columns = 513), NativeTerminalStatus.LIMIT_EXCEEDED)
        assertStatus(validConfig().copy(rows = 513), NativeTerminalStatus.LIMIT_EXCEEDED)

        val bridge = FakeNativeBridge()
        val boundary = assertIs<NativeBridgeResult.Success<NativeBridgeTerminal>>(
            bridge.createV3(
                validConfig().copy(
                    columns = 512,
                    rows = 512,
                    historyCapacityRows = UNSIGNED_INT_MAX,
                ),
            ),
        ).value
        assertEquals(UNSIGNED_INT_MAX, bridge.onlyStateForTest().historyCapacityRows)
        assertIs<NativeBridgeResult.Success<Unit>>(bridge.destroy(boundary.handle))
    }

    @Test
    fun configV3DiscoveryIsRequiredOnlyByTheNewHandleOverload() {
        val bridge = FakeNativeBridge().apply {
            configV3StructSize = TERMLINK_CONFIG_V3_STRUCT_SIZE - 1
        }

        val legacy = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)
        assertEquals(1, bridge.createCalls)
        legacy.close()

        val failure = assertFailsWith<NativeTerminalException> {
            NativeTerminalHandle.create(
                bridge,
                TerminalSize(8, 2),
                1,
                TerminalOutputProfile.UTF8_WITH_STANDALONE_C1,
            )
        }
        assertEquals(NativeTerminalStatus.ABI_MISMATCH, failure.status)
        assertEquals("configV3StructSize", failure.operation)
        assertEquals(0, bridge.createV3Calls)
    }

    private fun validConfig(
        outputProfile: Int = NATIVE_OUTPUT_PROFILE_UTF8,
    ): NativeTerminalConfigV3 = NativeTerminalConfigV3(
        columns = 80,
        rows = 24,
        initialSourceSequence = 1,
        historyCapacityRows = 10,
        outputProfile = outputProfile,
    )

    private fun assertInvalidArgument(config: NativeTerminalConfigV3) {
        assertStatus(config, NativeTerminalStatus.INVALID_ARGUMENT)
    }

    private fun assertStatus(config: NativeTerminalConfigV3, expected: NativeTerminalStatus) {
        val bridge = FakeNativeBridge()
        val failure = assertIs<NativeBridgeResult.Failure>(bridge.createV3(config))
        assertEquals(expected.code, failure.statusCode)
        assertEquals(1, bridge.createV3Calls)
        assertEquals(0, bridge.destroyCalls)
    }

    private companion object {
        const val UNSIGNED_INT_MAX = 0xffff_ffffL
    }
}
