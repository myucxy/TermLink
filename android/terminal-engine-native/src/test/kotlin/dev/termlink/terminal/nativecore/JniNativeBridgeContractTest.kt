package dev.termlink.terminal.nativecore

import kotlin.test.Test
import kotlin.test.assertTrue

class JniNativeBridgeContractTest {
    @Test
    fun productionBridgeHasAStableNativeBridgeTypeWithoutLoadingAndroidElfOnTheJvm() {
        assertTrue(NativeBridge::class.java.isAssignableFrom(JniNativeBridge::class.java))
    }

    @Test
    fun configurableLimitsCannotExceedTheFrozenCBatchAndSnapshotBounds() {
        runCatching { NativeTerminalLimits(maxEventsPerBatch = 257) }
            .onSuccess { error("257 events must exceed the C ABI hard limit") }
        runCatching { NativeTerminalLimits(maxOutputBytesPerBatch = 4 * 1024 * 1024 + 1) }
            .onSuccess { error("aggregate output must stay within the C ABI hard limit") }
        runCatching { NativeTerminalLimits(maxCheckpointBytes = 64 * 1024 * 1024 + 1) }
            .onSuccess { error("snapshot bytes must stay within the C ABI hard limit") }
    }
}
